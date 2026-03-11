namespace KPACS.ShareRelay.Contracts;

public sealed record RegisterUserRequest(
    string Email,
    string DisplayName,
    string? Organization);

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string? Organization,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record RegisterDeviceRequest(
    Guid UserId,
    string DeviceName,
    string Platform,
    string? ViewerVersion,
    string PublicEncryptionKey,
    string PublicSigningKey);

public sealed record DeviceResponse(
    Guid Id,
    Guid UserId,
    string DeviceName,
    string Platform,
    string? ViewerVersion,
    string PublicEncryptionKey,
    string PublicSigningKey,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastSeenUtc);

public sealed record ContactSearchResponse(
    IReadOnlyList<UserResponse> Contacts);

public sealed record ContactDeviceResponse(
    Guid Id,
    Guid UserId,
    string DeviceName,
    string Platform,
    string? ViewerVersion,
    string PublicEncryptionKey,
    string PublicSigningKey,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastSeenUtc);

public sealed record ContactDeviceDirectoryResponse(
    Guid UserId,
    IReadOnlyList<ContactDeviceResponse> Devices);
