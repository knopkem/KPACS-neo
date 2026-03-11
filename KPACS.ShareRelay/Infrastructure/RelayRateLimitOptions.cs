namespace KPACS.ShareRelay.Infrastructure;

public sealed class RelayRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;

    public int QueueLimit { get; set; } = 0;
}
