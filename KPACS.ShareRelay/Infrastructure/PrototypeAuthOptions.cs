namespace KPACS.ShareRelay.Infrastructure;

public sealed class PrototypeAuthOptions
{
    public const string SectionName = "Auth";

    public bool RequireApiKey { get; set; } = true;

    public string HeaderName { get; set; } = "X-Relay-Api-Key";

    public List<string> ApiKeys { get; set; } = [];
}
