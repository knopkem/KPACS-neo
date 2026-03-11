namespace KPACS.ShareRelay.Contracts;

public sealed record CreateShareRequest(
    Guid SenderUserId,
    IReadOnlyList<Guid> RecipientUserIds,
    string Subject,
    string? Message,
    string PackageType,
    bool ContainsPhi,
    bool IsAnonymized,
    DateTimeOffset ExpiresUtc,
    string CipherSuite,
    string? ViewerDownloadUrl);

public sealed record CreateShareResponse(
    Guid ShareId,
    string Status,
    string UploadUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

public sealed record PackageUploadResponse(
    Guid ShareId,
    string Status,
    string StorageKey,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedUtc);

public sealed record ShareRecipientResponse(
    Guid RecipientUserId,
    string RecipientEmail,
    string RecipientDisplayName,
    string Status,
    DateTimeOffset? ViewedUtc,
    DateTimeOffset? DownloadedUtc,
    DateTimeOffset? ImportedUtc,
    DateTimeOffset? AcknowledgedUtc);

public sealed record ShareSummaryResponse(
    Guid ShareId,
    Guid SenderUserId,
    string SenderEmail,
    string SenderDisplayName,
    string Subject,
    string? Message,
    string PackageType,
    bool ContainsPhi,
    bool IsAnonymized,
    string Status,
    string CipherSuite,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? UploadedUtc,
    bool PackageAvailable,
    string? PackageFileName,
    long? PackageSizeBytes,
    string? ViewerDownloadUrl,
    IReadOnlyList<ShareRecipientResponse> Recipients);

public sealed record InboxResponse(
    Guid RecipientUserId,
    IReadOnlyList<ShareSummaryResponse> Items);

public sealed record ShareAcknowledgeRequest(
    Guid ActorUserId,
    string EventType,
    string? Details);
