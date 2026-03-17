using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

public static class VolumeRenderingPresetCatalog
{
    public static TransferFunctionPreset[] OrderedPresets { get; } =
    [
        TransferFunctionPreset.Default,
        TransferFunctionPreset.Bone,
        TransferFunctionPreset.SoftTissue,
        TransferFunctionPreset.Lung,
        TransferFunctionPreset.Angio,
        TransferFunctionPreset.Skin,
        TransferFunctionPreset.Endoscopy,
        TransferFunctionPreset.PetHotIron,
        TransferFunctionPreset.PetSpectrum,
        TransferFunctionPreset.Perfusion,
    ];

    public static string GetLabel(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.SoftTissue => "Soft Tissue",
        TransferFunctionPreset.Angio => "Vascular Red",
        TransferFunctionPreset.Skin => "Skin Surface",
        TransferFunctionPreset.Endoscopy => "Endoscopy",
        TransferFunctionPreset.PetHotIron => "PET Hot Iron",
        TransferFunctionPreset.PetSpectrum => "PET Spectrum",
        TransferFunctionPreset.Perfusion => "Perfusion",
        _ => preset.ToString(),
    };

    public static int GetRecommendedColorScheme(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.Bone => (int)ColorScheme.Bone,
        TransferFunctionPreset.SoftTissue => (int)ColorScheme.Gold,
        TransferFunctionPreset.Angio => (int)ColorScheme.Flow,
        TransferFunctionPreset.Skin => (int)ColorScheme.Gold,
        TransferFunctionPreset.Endoscopy => (int)ColorScheme.Gold,
        TransferFunctionPreset.PetHotIron => (int)ColorScheme.Pet,
        TransferFunctionPreset.PetSpectrum => (int)ColorScheme.Spectrum,
        TransferFunctionPreset.Perfusion => (int)ColorScheme.Jet,
        _ => (int)ColorScheme.Grayscale,
    };

    public static string GetDescription(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.Default => "Neutral CT ramp with grayscale output.",
        TransferFunctionPreset.Bone => "High-density osseous structures with a bone-style LUT.",
        TransferFunctionPreset.SoftTissue => "Warm soft-tissue rendering for parenchyma and organs.",
        TransferFunctionPreset.Lung => "Low-density pulmonary structures with restrained grayscale contrast.",
        TransferFunctionPreset.Angio => "CTA-style vessel emphasis with a red-hot LUT.",
        TransferFunctionPreset.Skin => "Surface-focused tissue band for external anatomy.",
        TransferFunctionPreset.Endoscopy => "Front-surface mucosal emphasis for luminal fly-through style views.",
        TransferFunctionPreset.PetHotIron => "PET-style intensity rendering with a hot-iron LUT.",
        TransferFunctionPreset.PetSpectrum => "PET-style high-contrast spectrum rendering.",
        TransferFunctionPreset.Perfusion => "Perfusion-style parametric rendering with a rainbow LUT.",
        _ => string.Empty,
    };

    public static VolumeShadingPreset[] OrderedShadingPresets { get; } =
    [
        VolumeShadingPreset.Default,
        VolumeShadingPreset.SoftTissue,
        VolumeShadingPreset.GlossyBone,
        VolumeShadingPreset.GlossyVascular,
        VolumeShadingPreset.Endoscopy,
    ];

    public static string GetShadingLabel(VolumeShadingPreset preset) => preset switch
    {
        VolumeShadingPreset.SoftTissue => "Soft Tissue",
        VolumeShadingPreset.GlossyBone => "Glossy Bone",
        VolumeShadingPreset.GlossyVascular => "Glossy Vascular",
        _ => preset.ToString(),
    };

    public static string GetShadingDescription(VolumeShadingPreset preset) => preset switch
    {
        VolumeShadingPreset.Default => "Balanced headlight shading for general CT DVR.",
        VolumeShadingPreset.SoftTissue => "Softer highlight model for parenchyma and organ surfaces.",
        VolumeShadingPreset.GlossyBone => "Sharper highlights and stronger bone surface separation.",
        VolumeShadingPreset.GlossyVascular => "High-specular vascular look for CTA-style vessel rendering.",
        VolumeShadingPreset.Endoscopy => "Wet-surface highlight model for luminal fly-through views.",
        _ => string.Empty,
    };

    public static VolumeShadingDefinition GetShadingDefinition(VolumeShadingPreset preset) => preset switch
    {
        VolumeShadingPreset.SoftTissue => new VolumeShadingDefinition(0.28, 0.76, 0.10, 18.0),
        VolumeShadingPreset.GlossyBone => new VolumeShadingDefinition(0.10, 0.40, 1.05, 54.0),
        VolumeShadingPreset.GlossyVascular => new VolumeShadingDefinition(0.08, 0.32, 1.30, 72.0),
        VolumeShadingPreset.Endoscopy => new VolumeShadingDefinition(0.08, 0.56, 1.10, 64.0),
        _ => new VolumeShadingDefinition(0.25, 0.75, 0.20, 24.0),
    };

    public static VolumeLightDirectionPreset[] OrderedLightDirectionPresets { get; } =
    [
        VolumeLightDirectionPreset.Headlight,
        VolumeLightDirectionPreset.LeftFront,
        VolumeLightDirectionPreset.RightFront,
        VolumeLightDirectionPreset.TopFront,
        VolumeLightDirectionPreset.RakingLeft,
    ];

    public static string GetLightDirectionLabel(VolumeLightDirectionPreset preset) => preset switch
    {
        VolumeLightDirectionPreset.LeftFront => "Left Front",
        VolumeLightDirectionPreset.RightFront => "Right Front",
        VolumeLightDirectionPreset.TopFront => "Top Front",
        VolumeLightDirectionPreset.RakingLeft => "Raking Left",
        _ => "Headlight",
    };

    public static string GetLightDirectionDescription(VolumeLightDirectionPreset preset) => preset switch
    {
        VolumeLightDirectionPreset.Headlight => "Light follows the camera and minimizes directional highlights.",
        VolumeLightDirectionPreset.LeftFront => "Off-axis light from the viewer's left for clearer glossy shape cues.",
        VolumeLightDirectionPreset.RightFront => "Off-axis light from the viewer's right for vascular and tubular highlights.",
        VolumeLightDirectionPreset.TopFront => "Top-biased light for endoscopy-style wet-surface illumination.",
        VolumeLightDirectionPreset.RakingLeft => "More tangential light to enhance surface relief.",
        _ => string.Empty,
    };

    public static VolumeLightDirectionDefinition GetLightDirectionDefinition(VolumeLightDirectionPreset preset) => preset switch
    {
        VolumeLightDirectionPreset.LeftFront => new VolumeLightDirectionDefinition(-32.0, 16.0),
        VolumeLightDirectionPreset.RightFront => new VolumeLightDirectionDefinition(32.0, 16.0),
        VolumeLightDirectionPreset.TopFront => new VolumeLightDirectionDefinition(0.0, 38.0),
        VolumeLightDirectionPreset.RakingLeft => new VolumeLightDirectionDefinition(-58.0, 8.0),
        _ => new VolumeLightDirectionDefinition(0.0, 0.0),
    };

    public static VolumeLightDirectionPreset GetRecommendedLightDirectionPreset(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.Bone => VolumeLightDirectionPreset.LeftFront,
        TransferFunctionPreset.Angio => VolumeLightDirectionPreset.RightFront,
        TransferFunctionPreset.Endoscopy => VolumeLightDirectionPreset.TopFront,
        TransferFunctionPreset.PetHotIron => VolumeLightDirectionPreset.RightFront,
        TransferFunctionPreset.PetSpectrum => VolumeLightDirectionPreset.RightFront,
        _ => VolumeLightDirectionPreset.Headlight,
    };

    public static VolumeShadingPreset GetRecommendedShadingPreset(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.Bone => VolumeShadingPreset.GlossyBone,
        TransferFunctionPreset.SoftTissue => VolumeShadingPreset.SoftTissue,
        TransferFunctionPreset.Angio => VolumeShadingPreset.GlossyVascular,
        TransferFunctionPreset.Endoscopy => VolumeShadingPreset.Endoscopy,
        _ => VolumeShadingPreset.Default,
    };
}