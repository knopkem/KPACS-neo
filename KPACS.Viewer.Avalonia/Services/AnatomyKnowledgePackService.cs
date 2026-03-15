using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class AnatomyKnowledgePackService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public AnatomyKnowledgePack LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string json = File.ReadAllText(filePath);
        return LoadFromJson(json);
    }

    public async Task<AnatomyKnowledgePack> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return LoadFromJson(json);
    }

    public AnatomyKnowledgePack LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AnatomyKnowledgePack? pack = JsonSerializer.Deserialize<AnatomyKnowledgePack>(json, SerializerOptions);
        if (pack is null)
        {
            throw new InvalidOperationException("The anatomy knowledge pack JSON could not be deserialized.");
        }

        pack.Normalize();
        Validate(pack);
        return pack;
    }

    public string Serialize(AnatomyKnowledgePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        pack.Normalize();
        Validate(pack);
        return JsonSerializer.Serialize(pack, SerializerOptions);
    }

    public async Task SaveToFileAsync(AnatomyKnowledgePack pack, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string json = Serialize(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public bool TryLoadFromFile(string filePath, out AnatomyKnowledgePack? pack, out string? error)
    {
        try
        {
            pack = LoadFromFile(filePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            pack = null;
            error = ex.Message;
            return false;
        }
    }

    public AnatomyKnowledgePack CreateDefaultCraniumBasePack()
    {
        AnatomyKnowledgePack pack = new()
        {
            PackId = "kpacs.cranium.base",
            PackVersion = "1.0",
            DisplayName = "KPACS Cranium Base",
            Vendor = "K-PACS",
            Module = "Cranium",
            SupportedModalities = ["CT", "MR"],
            SupportedMrProfiles = ["T1", "T2", "FLAIR"],
            FrameDefinition = new AnatomyFrameDefinition
            {
                Axes = ["LeftRight", "AnteriorPosterior", "CranialCaudal"],
                Anchors = ["Midline", "SkullBasePlane", "CranialVertex", "LeftOrbitCenter", "RightOrbitCenter", "PosteriorFossaCenter"],
            },
            Compartments =
            [
                new AnatomyCompartmentDefinition { Id = "intracranial_space", DisplayName = "Intracranial Space", AllowedModalities = ["CT", "MR"] },
                new AnatomyCompartmentDefinition { Id = "posterior_fossa", DisplayName = "Posterior Fossa", AllowedModalities = ["CT", "MR"], Parent = "intracranial_space" },
                new AnatomyCompartmentDefinition { Id = "ventricular_csf", DisplayName = "Ventricular CSF", AllowedModalities = ["CT", "MR"], Parent = "intracranial_space" },
                new AnatomyCompartmentDefinition { Id = "orbits", DisplayName = "Orbits", AllowedModalities = ["CT", "MR"] },
                new AnatomyCompartmentDefinition { Id = "paranasal_sinuses", DisplayName = "Paranasal Sinuses", AllowedModalities = ["CT", "MR"] },
            ],
            Landmarks =
            [
                new AnatomyLandmarkDefinition { Id = "midline", Type = "axis", Stability = "high", Required = true },
                new AnatomyLandmarkDefinition { Id = "skull_base_plane", Type = "plane", Stability = "high", Required = true },
                new AnatomyLandmarkDefinition { Id = "cranial_vertex", Type = "point", Stability = "high", Required = true },
                new AnatomyLandmarkDefinition { Id = "ventricular_axis", Type = "curve_or_axis", Stability = "medium", Required = true },
            ],
            Structures =
            [
                new AnatomyStructureDefinition
                {
                    Id = "left_lateral_ventricle",
                    DisplayName = "Left Lateral Ventricle",
                    AllowedCompartments = ["ventricular_csf", "intracranial_space"],
                    SupportedModalities = ["CT", "MR"],
                    ExpectedMaterial = new AnatomyExpectedMaterial
                    {
                        CtClass = ["CSF"],
                        MrT1 = ["low"],
                        MrT2 = ["high"],
                        MrFlair = ["suppressed_or_low"],
                    },
                    RequiredRelations =
                    [
                        new AnatomyRelationRule { Type = "left_of", Target = "midline", Strength = "hard" },
                        new AnatomyRelationRule { Type = "cranial_to", Target = "brainstem", Strength = "hard" },
                    ],
                    ForbiddenRelations =
                    [
                        new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" },
                        new AnatomyRelationRule { Type = "touches", Target = "skull_base_plane", Strength = "hard" },
                    ],
                },
                new AnatomyStructureDefinition
                {
                    Id = "brainstem",
                    DisplayName = "Brainstem",
                    AllowedCompartments = ["posterior_fossa", "intracranial_space"],
                    SupportedModalities = ["CT", "MR"],
                    ExpectedMaterial = new AnatomyExpectedMaterial
                    {
                        CtClass = ["BrainParenchyma"],
                        MrT1 = ["intermediate"],
                        MrT2 = ["intermediate"],
                        MrFlair = ["intermediate"],
                    },
                    RequiredRelations =
                    [
                        new AnatomyRelationRule { Type = "inside", Target = "posterior_fossa", Strength = "hard" },
                        new AnatomyRelationRule { Type = "near", Target = "skull_base_plane", Strength = "hard" },
                    ],
                    ForbiddenRelations =
                    [
                        new AnatomyRelationRule { Type = "inside", Target = "ventricular_csf", Strength = "hard" },
                    ],
                },
            ],
            ValidationCases =
            [
                new AnatomyValidationCase
                {
                    Id = "lv-vs-brainstem",
                    Description = "Left lateral ventricle must not be classified as brainstem.",
                    ExpectedStructureId = "left_lateral_ventricle",
                    ForbiddenStructureIds = ["brainstem"],
                },
            ],
        };

        pack.Normalize();
        return pack;
    }

    private static void Validate(AnatomyKnowledgePack pack)
    {
        if (string.IsNullOrWhiteSpace(pack.PackId))
        {
            throw new InvalidOperationException("The anatomy knowledge pack is missing PackId.");
        }

        if (string.IsNullOrWhiteSpace(pack.Module))
        {
            throw new InvalidOperationException("The anatomy knowledge pack is missing Module.");
        }

        if (pack.Structures.Count == 0)
        {
            throw new InvalidOperationException("The anatomy knowledge pack must define at least one structure.");
        }

        HashSet<string> compartmentIds = pack.Compartments
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> structureIds = pack.Structures
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (AnatomyStructureDefinition structure in pack.Structures)
        {
            if (string.IsNullOrWhiteSpace(structure.Id))
            {
                throw new InvalidOperationException("A structure in the anatomy knowledge pack is missing Id.");
            }

            foreach (string allowedCompartment in structure.AllowedCompartments)
            {
                if (!compartmentIds.Contains(allowedCompartment))
                {
                    throw new InvalidOperationException($"Structure '{structure.Id}' references unknown compartment '{allowedCompartment}'.");
                }
            }
        }

        foreach (AnatomyValidationCase validationCase in pack.ValidationCases)
        {
            if (!string.IsNullOrWhiteSpace(validationCase.ExpectedStructureId)
                && !structureIds.Contains(validationCase.ExpectedStructureId))
            {
                throw new InvalidOperationException($"Validation case '{validationCase.Id}' references unknown expected structure '{validationCase.ExpectedStructureId}'.");
            }

            foreach (string forbiddenStructureId in validationCase.ForbiddenStructureIds)
            {
                if (!structureIds.Contains(forbiddenStructureId))
                {
                    throw new InvalidOperationException($"Validation case '{validationCase.Id}' references unknown forbidden structure '{forbiddenStructureId}'.");
                }
            }
        }
    }
}
