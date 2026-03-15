namespace KPACS.Viewer.Models;

public sealed class AnatomyKnowledgePack
{
    public string PackId { get; set; } = string.Empty;
    public string PackVersion { get; set; } = "1.0";
    public string DisplayName { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public List<string> SupportedModalities { get; set; } = [];
    public List<string> SupportedMrProfiles { get; set; } = [];
    public AnatomyFrameDefinition FrameDefinition { get; set; } = new();
    public List<AnatomyCompartmentDefinition> Compartments { get; set; } = [];
    public List<AnatomyLandmarkDefinition> Landmarks { get; set; } = [];
    public List<AnatomyStructureDefinition> Structures { get; set; } = [];
    public AnatomyClassifierWeights ClassifierWeights { get; set; } = new();
    public List<AnatomyValidationCase> ValidationCases { get; set; } = [];
    public AnatomyPackMigration Migration { get; set; } = new();

    public void Normalize()
    {
        PackId = (PackId ?? string.Empty).Trim();
        PackVersion = string.IsNullOrWhiteSpace(PackVersion) ? "1.0" : PackVersion.Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        Vendor = (Vendor ?? string.Empty).Trim();
        Module = (Module ?? string.Empty).Trim();
        SupportedModalities = NormalizeStringList(SupportedModalities);
        SupportedMrProfiles = NormalizeStringList(SupportedMrProfiles);
        FrameDefinition ??= new AnatomyFrameDefinition();
        FrameDefinition.Normalize();
        Compartments = NormalizeObjectList(Compartments, static item => item.Normalize());
        Landmarks = NormalizeObjectList(Landmarks, static item => item.Normalize());
        Structures = NormalizeObjectList(Structures, static item => item.Normalize());
        ClassifierWeights ??= new AnatomyClassifierWeights();
        ValidationCases = NormalizeObjectList(ValidationCases, static item => item.Normalize());
        Migration ??= new AnatomyPackMigration();
        Migration.Normalize();
    }

    private static List<string> NormalizeStringList(List<string>? values)
    {
        return values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
    }

    private static List<T> NormalizeObjectList<T>(List<T>? values, Action<T> normalize)
        where T : class
    {
        List<T> result = [];
        if (values is null)
        {
            return result;
        }

        foreach (T value in values)
        {
            if (value is null)
            {
                continue;
            }

            normalize(value);
            result.Add(value);
        }

        return result;
    }
}

public sealed class AnatomyFrameDefinition
{
    public List<string> Axes { get; set; } = [];
    public List<string> Anchors { get; set; } = [];

    public void Normalize()
    {
        Axes = Axes
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Anchors = Anchors
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AnatomyCompartmentDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> AllowedModalities { get; set; } = [];
    public string Parent { get; set; } = string.Empty;

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        Parent = (Parent ?? string.Empty).Trim();
        AllowedModalities = AllowedModalities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AnatomyLandmarkDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Stability { get; set; } = string.Empty;
    public bool Required { get; set; }

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        Type = (Type ?? string.Empty).Trim();
        Stability = (Stability ?? string.Empty).Trim();
    }
}

public sealed class AnatomyStructureDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> AllowedCompartments { get; set; } = [];
    public List<string> SupportedModalities { get; set; } = [];
    public AnatomyExpectedPosition ExpectedPosition { get; set; } = new();
    public AnatomyExpectedMaterial ExpectedMaterial { get; set; } = new();
    public List<AnatomyRelationRule> RequiredRelations { get; set; } = [];
    public List<AnatomyRelationRule> ForbiddenRelations { get; set; } = [];

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        AllowedCompartments = AllowedCompartments
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SupportedModalities = SupportedModalities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExpectedPosition ??= new AnatomyExpectedPosition();
        ExpectedPosition.Normalize();
        ExpectedMaterial ??= new AnatomyExpectedMaterial();
        ExpectedMaterial.Normalize();
        RequiredRelations = RequiredRelations
            .Where(static item => item is not null)
            .ToList();
        ForbiddenRelations = ForbiddenRelations
            .Where(static item => item is not null)
            .ToList();

        foreach (AnatomyRelationRule relation in RequiredRelations)
        {
            relation.Normalize();
        }

        foreach (AnatomyRelationRule relation in ForbiddenRelations)
        {
            relation.Normalize();
        }
    }
}

public sealed class AnatomyExpectedPosition
{
    public AnatomyNumericRange LeftRight { get; set; } = new();
    public AnatomyNumericRange AnteriorPosterior { get; set; } = new();
    public AnatomyNumericRange CranialCaudal { get; set; } = new();
    public AnatomyNumericRange DistanceToMidline { get; set; } = new();
    public AnatomyNumericRange DistanceToSkullBase { get; set; } = new();
    public AnatomyNumericRange DistanceToVertex { get; set; } = new();

    public void Normalize()
    {
        LeftRight ??= new AnatomyNumericRange();
        AnteriorPosterior ??= new AnatomyNumericRange();
        CranialCaudal ??= new AnatomyNumericRange();
        DistanceToMidline ??= new AnatomyNumericRange();
        DistanceToSkullBase ??= new AnatomyNumericRange();
        DistanceToVertex ??= new AnatomyNumericRange();
    }
}

public sealed class AnatomyExpectedMaterial
{
    public List<string> CtClass { get; set; } = [];
    public List<string> MrT1 { get; set; } = [];
    public List<string> MrT2 { get; set; } = [];
    public List<string> MrFlair { get; set; } = [];

    public void Normalize()
    {
        CtClass = Normalize(CtClass);
        MrT1 = Normalize(MrT1);
        MrT2 = Normalize(MrT2);
        MrFlair = Normalize(MrFlair);
    }

    private static List<string> Normalize(List<string>? values)
    {
        return values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
    }
}

public sealed class AnatomyNumericRange
{
    public double? Min { get; set; }
    public double? Max { get; set; }
}

public sealed class AnatomyRelationRule
{
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Strength { get; set; } = string.Empty;

    public void Normalize()
    {
        Type = (Type ?? string.Empty).Trim();
        Target = (Target ?? string.Empty).Trim();
        Strength = (Strength ?? string.Empty).Trim();
    }
}

public sealed class AnatomyClassifierWeights
{
    public double CompartmentGate { get; set; } = 1.0;
    public double LandmarkRelations { get; set; } = 0.9;
    public double StructureGraph { get; set; } = 0.85;
    public double MaterialProfile { get; set; } = 0.8;
    public double LocalSignature { get; set; } = 0.45;
    public double LearnedPrior { get; set; } = 0.35;
}

public sealed class AnatomyValidationCase
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExpectedStructureId { get; set; } = string.Empty;
    public List<string> ForbiddenStructureIds { get; set; } = [];

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        Description = (Description ?? string.Empty).Trim();
        ExpectedStructureId = (ExpectedStructureId ?? string.Empty).Trim();
        ForbiddenStructureIds = ForbiddenStructureIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AnatomyPackMigration
{
    public string FromVersion { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public void Normalize()
    {
        FromVersion = (FromVersion ?? string.Empty).Trim();
        Notes = (Notes ?? string.Empty).Trim();
    }
}
