// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/VolumeTransferFunction.cs
// 1D transfer function for direct volume rendering.
//
// Phase 1: Maps scalar value (HU) → opacity.  Display colour comes from the
// existing windowing + colour LUT pipeline (DicomPixelRenderer).
// Phase 2 will extend this to RGBA colour mapping with interactive editor.
// ------------------------------------------------------------------------------------------------

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Named transfer-function presets suitable for CT data.
/// </summary>
public enum TransferFunctionPreset
{
    /// <summary>General-purpose ramp – similar to the legacy MpVrt compositing.</summary>
    Default,

    /// <summary>Emphasises bony structures (HU > 200).</summary>
    Bone,

    /// <summary>Soft-tissue window (-100 … 300 HU).</summary>
    SoftTissue,

    /// <summary>Lung parenchyma (-900 … -200 HU).</summary>
    Lung,

    /// <summary>CT-Angiography – contrast-enhanced vessels (HU > 150).</summary>
    Angio,

    /// <summary>Surface / skin rendering – thin opacity band near tissue boundary.</summary>
    Skin,

    /// <summary>Endoscopic surface rendering with stronger front-surface emphasis.</summary>
    Endoscopy,

    /// <summary>PET-style intensity rendering intended for hot-iron colour mapping.</summary>
    PetHotIron,

    /// <summary>PET-style intensity rendering intended for spectrum/rainbow colour mapping.</summary>
    PetSpectrum,

    /// <summary>Perfusion-style parametric rendering with mid/high-value emphasis.</summary>
    Perfusion,
}

/// <summary>
/// A single control point in the piecewise-linear opacity function.
/// </summary>
public readonly record struct OpacityControlPoint(double Value, double Opacity);

/// <summary>
/// 1D transfer function that maps scalar voxel values to opacity.
/// <para>
/// Phase 1 design: the lookup returns opacity only.  The final displayed
/// colour is determined by the existing windowing / colour-LUT pipeline.
/// </para>
/// <para>
/// Internally a precomputed lookup table (<see cref="LutSize"/> entries)
/// avoids per-sample computation during ray marching.
/// </para>
/// </summary>
public sealed class VolumeTransferFunction
{
    /// <summary>Resolution of the internal lookup table.</summary>
    private const int LutSize = 4096;

    private readonly double[] _opacityLut = new double[LutSize];
    private readonly float[]? _colorRLut;
    private readonly float[]? _colorGLut;
    private readonly float[]? _colorBLut;

    /// <summary>Minimum scalar value mapped to index 0.</summary>
    public double MinValue { get; }

    /// <summary>Maximum scalar value mapped to index LutSize-1.</summary>
    public double MaxValue { get; }

    /// <summary>
    /// Gradient-magnitude modulation strength.
    /// 0 = disabled.  Values around 0.005–0.02 work well for CT data.
    /// When enabled, opacity is scaled by <c>min(1, |∇f| × strength)</c>,
    /// emphasising boundaries (Kindlmann boundary-emphasis principle).
    /// </summary>
    public double GradientModulationStrength { get; set; }

    /// <summary>The preset that was used to create this TF, if any.</summary>
    public TransferFunctionPreset Preset { get; }

    public bool HasColorLookup => _colorRLut is not null && _colorGLut is not null && _colorBLut is not null;

    // ------------------------------------------------------------------
    //  Construction
    // ------------------------------------------------------------------

    private VolumeTransferFunction(
        IReadOnlyList<OpacityControlPoint> controlPoints,
        double minValue,
        double maxValue,
        double gradientModulationStrength,
        TransferFunctionPreset preset,
        bool enableAutoColor,
        double autoColorCenter,
        double autoColorWidth)
    {
        MinValue = minValue;
        MaxValue = maxValue;
        GradientModulationStrength = gradientModulationStrength;
        Preset = preset;
        BuildLut(controlPoints);

        if (enableAutoColor)
        {
            _colorRLut = new float[LutSize];
            _colorGLut = new float[LutSize];
            _colorBLut = new float[LutSize];
            BuildColorLut(autoColorCenter, autoColorWidth);
        }
    }

    // ------------------------------------------------------------------
    //  Lookup
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the opacity for a raw scalar value.
    /// Hot path — called millions of times per frame.
    /// </summary>
    public double LookupOpacity(double value)
    {
        int index = MapToIndex(value);
        return _opacityLut[index];
    }

    public void LookupColor(double value, out float red, out float green, out float blue)
    {
        if (!HasColorLookup)
        {
            red = 0f;
            green = 0f;
            blue = 0f;
            return;
        }

        int index = MapToIndex(value);
        red = _colorRLut![index];
        green = _colorGLut![index];
        blue = _colorBLut![index];
    }

    /// <summary>
    /// Modulates opacity by gradient magnitude (boundary emphasis).
    /// Returns <paramref name="opacity"/> unchanged when modulation is disabled.
    /// </summary>
    public double ModulateByGradient(double opacity, double gradientMagnitude)
    {
        if (GradientModulationStrength <= 0.0)
        {
            return opacity;
        }

        double modulation = Math.Min(1.0, gradientMagnitude * GradientModulationStrength);
        return opacity * modulation;
    }

    public float[] CreateOpacityLutSnapshot()
    {
        float[] lut = new float[_opacityLut.Length];
        for (int i = 0; i < _opacityLut.Length; i++)
        {
            lut[i] = (float)_opacityLut[i];
        }

        return lut;
    }

    public (float[] R, float[] G, float[] B) CreateColorLutSnapshots()
    {
        float[] red = new float[LutSize];
        float[] green = new float[LutSize];
        float[] blue = new float[LutSize];

        if (HasColorLookup)
        {
            Array.Copy(_colorRLut!, red, LutSize);
            Array.Copy(_colorGLut!, green, LutSize);
            Array.Copy(_colorBLut!, blue, LutSize);
            return (red, green, blue);
        }

        for (int i = 0; i < LutSize; i++)
        {
            float normalized = i / (float)(LutSize - 1);
            red[i] = normalized;
            green[i] = normalized;
            blue[i] = normalized;
        }

        return (red, green, blue);
    }

    // ------------------------------------------------------------------
    //  Factory methods — CT Presets
    // ------------------------------------------------------------------

    /// <summary>Creates a transfer function for the given preset and value range.</summary>
    public static VolumeTransferFunction Create(
        TransferFunctionPreset preset,
        double minValue,
        double maxValue)
    {
        PresetDefinition definition = GetPresetDefinition(preset, minValue, maxValue);
        return new VolumeTransferFunction(
            definition.ControlPoints,
            minValue,
            maxValue,
            definition.GradientModulationStrength,
            definition.Preset,
            enableAutoColor: false,
            autoColorCenter: 0.0,
            autoColorWidth: 1.0);
    }

    public static VolumeTransferFunction CreateWindowed(
        TransferFunctionPreset preset,
        double minValue,
        double maxValue,
        double center,
        double width,
        bool enableAutoColor = false)
    {
        PresetDefinition definition = GetPresetDefinition(preset, minValue, maxValue);
        (double defaultCenter, double defaultWidth) = GetSuggestedWindow(definition.ControlPoints, minValue, maxValue);
        double safeDefaultWidth = Math.Max(1.0, defaultWidth);
        double safeWidth = Math.Max(1.0, width);

        List<OpacityControlPoint> remappedControlPoints = definition.ControlPoints
            .Select(point => new OpacityControlPoint(
                Math.Clamp(center + ((point.Value - defaultCenter) / safeDefaultWidth) * safeWidth, minValue, maxValue),
                point.Opacity))
            .OrderBy(point => point.Value)
            .ToList();

        return new VolumeTransferFunction(
            remappedControlPoints,
            minValue,
            maxValue,
            definition.GradientModulationStrength,
            definition.Preset,
            enableAutoColor,
            center,
            safeWidth);
    }

    public static (double Center, double Width) GetSuggestedWindow(
        TransferFunctionPreset preset,
        double minValue,
        double maxValue)
    {
        PresetDefinition definition = GetPresetDefinition(preset, minValue, maxValue);
        return GetSuggestedWindow(definition.ControlPoints, minValue, maxValue);
    }

    /// <summary>
    /// General-purpose ramp similar to the legacy hardcoded opacity function.
    /// Low values are transparent; opacity rises gradually towards the upper range.
    /// </summary>
    public static VolumeTransferFunction CreateDefault(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.Default, minValue, maxValue);
    }

    /// <summary>
    /// Bone preset — transparent below ~200 HU, rising to high opacity above 700 HU.
    /// </summary>
    public static VolumeTransferFunction CreateBone(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.Bone, minValue, maxValue);
    }

    /// <summary>
    /// Soft-tissue preset — moderate opacity in the -100 … 300 HU range.
    /// </summary>
    public static VolumeTransferFunction CreateSoftTissue(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.SoftTissue, minValue, maxValue);
    }

    /// <summary>
    /// Lung preset — focuses on -900 … -200 HU (air-filled parenchyma).
    /// Soft tissue is slightly visible; bone is transparent.
    /// </summary>
    public static VolumeTransferFunction CreateLung(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.Lung, minValue, maxValue);
    }

    /// <summary>
    /// CT-Angiography preset — transparent below ~150 HU, steep rise for
    /// contrast-enhanced vessels.
    /// </summary>
    public static VolumeTransferFunction CreateAngio(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.Angio, minValue, maxValue);
    }

    /// <summary>
    /// Skin/surface preset — thin opacity band near tissue boundary (-200 … 0 HU).
    /// </summary>
    public static VolumeTransferFunction CreateSkin(double minValue, double maxValue)
    {
        return Create(TransferFunctionPreset.Skin, minValue, maxValue);
    }

    // ------------------------------------------------------------------
    //  Internals
    // ------------------------------------------------------------------

    private int MapToIndex(double value)
    {
        double normalized = (value - MinValue) / (MaxValue - MinValue);
        int index = (int)(normalized * (LutSize - 1));
        return Math.Clamp(index, 0, LutSize - 1);
    }

    private void BuildLut(IReadOnlyList<OpacityControlPoint> controlPoints)
    {
        if (controlPoints.Count == 0)
        {
            return;
        }

        for (int i = 0; i < LutSize; i++)
        {
            double value = MinValue + (MaxValue - MinValue) * i / (LutSize - 1);
            _opacityLut[i] = InterpolateOpacity(controlPoints, value);
        }
    }

    private void BuildColorLut(double center, double width)
    {
        if (!HasColorLookup)
        {
            return;
        }

        for (int i = 0; i < LutSize; i++)
        {
            double value = MinValue + (MaxValue - MinValue) * i / (LutSize - 1);
            (float red, float green, float blue) = ColorLut.SampleAutoCtDvrColor(value, center, width, Preset);
            _colorRLut![i] = red;
            _colorGLut![i] = green;
            _colorBLut![i] = blue;
        }
    }

    private static double InterpolateOpacity(IReadOnlyList<OpacityControlPoint> points, double value)
    {
        if (value <= points[0].Value)
        {
            return points[0].Opacity;
        }

        if (value >= points[^1].Value)
        {
            return points[^1].Opacity;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            if (value >= points[i].Value && value <= points[i + 1].Value)
            {
                double span = points[i + 1].Value - points[i].Value;
                if (span <= 0)
                {
                    return points[i].Opacity;
                }

                double t = (value - points[i].Value) / span;
                return points[i].Opacity + t * (points[i + 1].Opacity - points[i].Opacity);
            }
        }

        return points[^1].Opacity;
    }

    private static PresetDefinition GetPresetDefinition(
        TransferFunctionPreset preset,
        double minValue,
        double maxValue)
    {
        return preset switch
        {
            TransferFunctionPreset.Bone => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(100, 0.0),
                    new(200, 0.0),
                    new(400, 0.15),
                    new(700, 0.60),
                    new(1200, 0.85),
                    new(maxValue, 0.90),
                ],
                0.0,
                TransferFunctionPreset.Bone),
            TransferFunctionPreset.SoftTissue => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(-200, 0.0),
                    new(-100, 0.01),
                    new(0, 0.08),
                    new(40, 0.20),
                    new(80, 0.25),
                    new(200, 0.18),
                    new(300, 0.05),
                    new(500, 0.0),
                    new(maxValue, 0.0),
                ],
                0.008,
                TransferFunctionPreset.SoftTissue),
            TransferFunctionPreset.Lung => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(-950, 0.0),
                    new(-900, 0.02),
                    new(-700, 0.12),
                    new(-500, 0.20),
                    new(-300, 0.15),
                    new(-200, 0.08),
                    new(-50, 0.03),
                    new(100, 0.0),
                    new(maxValue, 0.0),
                ],
                0.0,
                TransferFunctionPreset.Lung),
            TransferFunctionPreset.Angio => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(100, 0.0),
                    new(150, 0.0),
                    new(200, 0.20),
                    new(300, 0.55),
                    new(500, 0.80),
                    new(800, 0.90),
                    new(maxValue, 0.90),
                ],
                0.0,
                TransferFunctionPreset.Angio),
            TransferFunctionPreset.Skin => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(-400, 0.0),
                    new(-200, 0.05),
                    new(-100, 0.40),
                    new(0, 0.50),
                    new(100, 0.15),
                    new(200, 0.0),
                    new(maxValue, 0.0),
                ],
                0.015,
                TransferFunctionPreset.Skin),
            TransferFunctionPreset.Endoscopy => new PresetDefinition(
                [
                    new(minValue, 0.0),
                    new(-900, 0.0),
                    new(-350, 0.0),
                    new(-180, 0.08),
                    new(-80, 0.40),
                    new(0, 0.72),
                    new(90, 0.82),
                    new(180, 0.45),
                    new(260, 0.08),
                    new(320, 0.0),
                    new(maxValue, 0.0),
                ],
                0.02,
                TransferFunctionPreset.Endoscopy),
            TransferFunctionPreset.PetHotIron => CreateRelativeDefinition(
                minValue,
                maxValue,
                [
                    new(0.00, 0.0),
                    new(0.08, 0.0),
                    new(0.18, 0.05),
                    new(0.32, 0.14),
                    new(0.50, 0.30),
                    new(0.68, 0.52),
                    new(0.84, 0.75),
                    new(1.00, 0.90),
                ],
                gradientModulationStrength: 0.0,
                preset: TransferFunctionPreset.PetHotIron),
            TransferFunctionPreset.PetSpectrum => CreateRelativeDefinition(
                minValue,
                maxValue,
                [
                    new(0.00, 0.0),
                    new(0.10, 0.0),
                    new(0.22, 0.04),
                    new(0.38, 0.12),
                    new(0.56, 0.28),
                    new(0.74, 0.48),
                    new(0.88, 0.68),
                    new(1.00, 0.82),
                ],
                gradientModulationStrength: 0.0,
                preset: TransferFunctionPreset.PetSpectrum),
            TransferFunctionPreset.Perfusion => CreateRelativeDefinition(
                minValue,
                maxValue,
                [
                    new(0.00, 0.0),
                    new(0.14, 0.0),
                    new(0.26, 0.03),
                    new(0.42, 0.10),
                    new(0.58, 0.24),
                    new(0.72, 0.42),
                    new(0.86, 0.64),
                    new(1.00, 0.84),
                ],
                gradientModulationStrength: 0.0,
                preset: TransferFunctionPreset.Perfusion),
            _ => CreateDefaultDefinition(minValue, maxValue),
        };
    }

    private static PresetDefinition CreateRelativeDefinition(
        double minValue,
        double maxValue,
        IReadOnlyList<OpacityControlPoint> normalizedPoints,
        double gradientModulationStrength,
        TransferFunctionPreset preset)
    {
        double range = Math.Max(1.0, maxValue - minValue);
        List<OpacityControlPoint> controlPoints = normalizedPoints
            .Select(point => new OpacityControlPoint(
                minValue + Math.Clamp(point.Value, 0.0, 1.0) * range,
                point.Opacity))
            .ToList();

        return new PresetDefinition(controlPoints, gradientModulationStrength, preset);
    }

    private static PresetDefinition CreateDefaultDefinition(double minValue, double maxValue)
    {
        double range = maxValue - minValue;
        double p05 = minValue + 0.05 * range;
        double p20 = minValue + 0.20 * range;
        double p50 = minValue + 0.50 * range;
        double p80 = minValue + 0.80 * range;

        return new PresetDefinition(
            [
                new(minValue, 0.0),
                new(p05, 0.0),
                new(p20, 0.02),
                new(p50, 0.12),
                new(p80, 0.28),
                new(maxValue, 0.35),
            ],
            0.0,
            TransferFunctionPreset.Default);
    }

    private static (double Center, double Width) GetSuggestedWindow(
        IReadOnlyList<OpacityControlPoint> controlPoints,
        double minValue,
        double maxValue)
    {
        List<OpacityControlPoint> significant = controlPoints
            .Where(point => point.Opacity > 0.0001)
            .ToList();

        if (significant.Count == 0)
        {
            double range = Math.Max(1.0, maxValue - minValue);
            return (minValue + range * 0.5, range);
        }

        double low = significant.First().Value;
        double high = significant.Last().Value;
        double width = Math.Max(1.0, high - low);
        return ((low + high) * 0.5, width);
    }

    private sealed record PresetDefinition(
        IReadOnlyList<OpacityControlPoint> ControlPoints,
        double GradientModulationStrength,
        TransferFunctionPreset Preset);
}
