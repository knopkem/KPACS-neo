namespace KPACS.Viewer.Models;

public sealed class ShareRelaySettings
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string ApiKey { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string PublicEncryptionKey { get; set; } = string.Empty;
    public string PrivateEncryptionKey { get; set; } = string.Empty;
    public string PublicSigningKey { get; set; } = string.Empty;

    public void Normalize()
    {
        BaseUrl = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        ApiKey = (ApiKey ?? string.Empty).Trim();
        UserEmail = (UserEmail ?? string.Empty).Trim();
        DisplayName = (DisplayName ?? string.Empty).Trim();
        Organization = (Organization ?? string.Empty).Trim();
        DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        PublicEncryptionKey = (PublicEncryptionKey ?? string.Empty).Trim();
        PrivateEncryptionKey = (PrivateEncryptionKey ?? string.Empty).Trim();
        PublicSigningKey = (PublicSigningKey ?? string.Empty).Trim();
    }

    public ShareRelaySettings Clone() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        UserEmail = UserEmail,
        DisplayName = DisplayName,
        Organization = Organization,
        DeviceName = DeviceName,
        UserId = UserId,
        DeviceId = DeviceId,
        PublicEncryptionKey = PublicEncryptionKey,
        PrivateEncryptionKey = PrivateEncryptionKey,
        PublicSigningKey = PublicSigningKey,
    };
}

public sealed record RelayContactItem(Guid UserId, string DisplayName, string Email, string? Organization)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Organization)
        ? $"{DisplayName} <{Email}>"
        : $"{DisplayName} ({Organization}) <{Email}>";

    public override string ToString() => DisplayLabel;
}

public sealed record RelayContactDevice(Guid DeviceId, Guid UserId, string DeviceName, string Platform, string? ViewerVersion, string PublicEncryptionKey, string PublicSigningKey);

public sealed class RelayInboxItem
{
    public required Guid ShareId { get; init; }
    public required string SenderDisplayName { get; init; }
    public required string SenderEmail { get; init; }
    public required string Subject { get; init; }
    public string? Message { get; init; }
    public required string Status { get; init; }
    public required string CipherSuite { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? UploadedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public bool PackageAvailable { get; init; }
    public string? PackageFileName { get; init; }
    public long? PackageSizeBytes { get; init; }
    public bool ContainsPhi { get; init; }
    public bool IsAnonymized { get; init; }

    public string SenderLabel => string.IsNullOrWhiteSpace(SenderDisplayName)
        ? SenderEmail
        : $"{SenderDisplayName} <{SenderEmail}>";

    public string PackageLabel => PackageSizeBytes is > 0
        ? $"{(PackageFileName ?? "Package")} ({FormatFileSize(PackageSizeBytes.Value)})"
        : (PackageFileName ?? "Package pending");

    public string AgeLabel => UploadedUtc?.LocalDateTime.ToString("g") ?? CreatedUtc.LocalDateTime.ToString("g");

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
    }
}

public sealed record RelayImportResult(Guid ShareId, string Subject, ImportResult ImportResult);

public sealed record RelayShareResult(Guid ShareId, int StudyCount, int RecipientCount, int FileCount, long PackageSizeBytes, string Subject);
