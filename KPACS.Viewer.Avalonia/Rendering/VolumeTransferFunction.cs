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

    // ------------------------------------------------------------------
    //  Construction
    // ------------------------------------------------------------------

    private VolumeTransferFunction(
        IReadOnlyList<OpacityControlPoint> controlPoints,
        double minValue,
        double maxValue,
        double gradientModulationStrength,
        TransferFunctionPreset preset)
    {
        MinValue = minValue;
        MaxValue = maxValue;
        GradientModulationStrength = gradientModulationStrength;
        Preset = preset;
        BuildLut(controlPoints);
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

    // ------------------------------------------------------------------
    //  Factory methods — CT Presets
    // ------------------------------------------------------------------

    /// <summary>Creates a transfer function for the given preset and value range.</summary>
    public static VolumeTransferFunction Create(
        TransferFunctionPreset preset,
        double minValue,
        double maxValue) => preset switch
    {
        TransferFunctionPreset.Bone => CreateBone(minValue, maxValue),
        TransferFunctionPreset.SoftTissue => CreateSoftTissue(minValue, maxValue),
        TransferFunctionPreset.Lung => CreateLung(minValue, maxValue),
        TransferFunctionPreset.Angio => CreateAngio(minValue, maxValue),
        TransferFunctionPreset.Skin => CreateSkin(minValue, maxValue),
        _ => CreateDefault(minValue, maxValue),
    };

    /// <summary>
    /// General-purpose ramp similar to the legacy hardcoded opacity function.
    /// Low values are transparent; opacity rises gradually towards the upper range.
    /// </summary>
    public static VolumeTransferFunction CreateDefault(double minValue, double maxValue)
    {
        // Reproduce the feel of the old Pow(norm, 1.6) * 0.35 curve
        // with a piecewise-linear approximation.
        double range = maxValue - minValue;
        double p05 = minValue + 0.05 * range;
        double p20 = minValue + 0.20 * range;
        double p50 = minValue + 0.50 * range;
        double p80 = minValue + 0.80 * range;

        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
                new(minValue, 0.0),
                new(p05, 0.0),
                new(p20, 0.02),
                new(p50, 0.12),
                new(p80, 0.28),
                new(maxValue, 0.35),
            },
            minValue, maxValue, 0.0, TransferFunctionPreset.Default);
    }

    /// <summary>
    /// Bone preset — transparent below ~200 HU, rising to high opacity above 700 HU.
    /// </summary>
    public static VolumeTransferFunction CreateBone(double minValue, double maxValue)
    {
        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
                new(minValue, 0.0),
                new(100, 0.0),
                new(200, 0.0),
                new(400, 0.15),
                new(700, 0.60),
                new(1200, 0.85),
                new(maxValue, 0.90),
            },
            minValue, maxValue, 0.0, TransferFunctionPreset.Bone);
    }

    /// <summary>
    /// Soft-tissue preset — moderate opacity in the -100 … 300 HU range.
    /// </summary>
    public static VolumeTransferFunction CreateSoftTissue(double minValue, double maxValue)
    {
        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
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
            },
            minValue, maxValue, 0.008, TransferFunctionPreset.SoftTissue);
    }

    /// <summary>
    /// Lung preset — focuses on -900 … -200 HU (air-filled parenchyma).
    /// Soft tissue is slightly visible; bone is transparent.
    /// </summary>
    public static VolumeTransferFunction CreateLung(double minValue, double maxValue)
    {
        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
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
            },
            minValue, maxValue, 0.0, TransferFunctionPreset.Lung);
    }

    /// <summary>
    /// CT-Angiography preset — transparent below ~150 HU, steep rise for
    /// contrast-enhanced vessels.
    /// </summary>
    public static VolumeTransferFunction CreateAngio(double minValue, double maxValue)
    {
        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
                new(minValue, 0.0),
                new(100, 0.0),
                new(150, 0.0),
                new(200, 0.20),
                new(300, 0.55),
                new(500, 0.80),
                new(800, 0.90),
                new(maxValue, 0.90),
            },
            minValue, maxValue, 0.0, TransferFunctionPreset.Angio);
    }

    /// <summary>
    /// Skin/surface preset — thin opacity band near tissue boundary (-200 … 0 HU).
    /// </summary>
    public static VolumeTransferFunction CreateSkin(double minValue, double maxValue)
    {
        return new VolumeTransferFunction(
            new OpacityControlPoint[]
            {
                new(minValue, 0.0),
                new(-400, 0.0),
                new(-200, 0.05),
                new(-100, 0.40),
                new(0, 0.50),
                new(100, 0.15),
                new(200, 0.0),
                new(maxValue, 0.0),
            },
            minValue, maxValue, 0.015, TransferFunctionPreset.Skin);
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
}
