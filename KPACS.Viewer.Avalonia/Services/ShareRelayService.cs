using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ShareRelayService
{
    private const string EncryptedPackageCipherSuite = "rsa-oaep-sha256/aes-256-gcm";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient _httpClient = new();
    private readonly string _stagingDirectory;
    private readonly string _viewerVersion;

    public ShareRelayService(string applicationDirectory, string viewerVersion)
    {
        _stagingDirectory = Path.Combine(applicationDirectory, "relay-staging");
        _viewerVersion = viewerVersion;
        Directory.CreateDirectory(_stagingDirectory);
        _httpClient.Timeout = TimeSpan.FromMinutes(15);
    }

    public string BuildSuggestedSubject(IReadOnlyList<StudyDetails> studies)
    {
        if (studies.Count == 0)
        {
            return "KPACS share";
        }

        if (studies.Count == 1)
        {
            StudyListItem study = studies[0].Study;
            string description = string.IsNullOrWhiteSpace(study.StudyDescription) ? "Study" : study.StudyDescription.Trim();
            string patient = string.IsNullOrWhiteSpace(study.PatientName) ? "Unknown patient" : study.PatientName.Trim();
            string date = StudyListItem.FormatDicomDate(study.StudyDate);
            return string.IsNullOrWhiteSpace(date)
                ? $"{patient} - {description}"
                : $"{patient} - {description} ({date})";
        }

        return $"KPACS share: {studies.Count} studies";
    }

    public async Task<ShareRelaySettings> RegisterAsync(ShareRelaySettings settings, CancellationToken cancellationToken)
    {
        ShareRelaySettings normalized = settings.Clone();
        normalized.Normalize();
        ValidateSettings(normalized, requireIdentity: true);
        EnsureDeviceKeyMaterial(normalized);

        UserResponse user = await SendJsonAsync<RegisterUserRequest, UserResponse>(
            normalized,
            HttpMethod.Post,
            "/api/v1/users/register",
            new RegisterUserRequest(normalized.UserEmail, normalized.DisplayName, EmptyToNull(normalized.Organization)),
            cancellationToken);

        DeviceResponse device = await SendJsonAsync<RegisterDeviceRequest, DeviceResponse>(
            normalized,
            HttpMethod.Post,
            "/api/v1/devices/register",
            new RegisterDeviceRequest(
                user.Id,
                normalized.DeviceName,
                GetPlatformLabel(),
                _viewerVersion,
                normalized.PublicEncryptionKey,
                normalized.PublicSigningKey),
            cancellationToken);

        normalized.UserId = user.Id;
        normalized.DeviceId = device.Id;
        return normalized;
    }

    public async Task<(ShareRelaySettings Settings, IReadOnlyList<RelayInboxItem> Items)> GetInboxAsync(ShareRelaySettings settings, CancellationToken cancellationToken)
    {
        ShareRelaySettings normalized = await EnsureRegisteredForInboxAsync(settings, cancellationToken);
        InboxResponse response = await SendAsync<InboxResponse>(
            normalized,
            HttpMethod.Get,
            $"/api/v1/inbox?recipientUserId={normalized.UserId}",
            null,
            cancellationToken);

        List<RelayInboxItem> items = response.Items
            .Select(item => new RelayInboxItem
            {
                ShareId = item.ShareId,
                SenderDisplayName = item.SenderDisplayName,
                SenderEmail = item.SenderEmail,
                Subject = item.Subject,
                Message = item.Message,
                Status = item.Status,
                CipherSuite = item.CipherSuite,
                CreatedUtc = item.CreatedUtc,
                UploadedUtc = item.UploadedUtc,
                ExpiresUtc = item.ExpiresUtc,
                PackageAvailable = item.PackageAvailable,
                PackageFileName = item.PackageFileName,
                PackageSizeBytes = item.PackageSizeBytes,
                ContainsPhi = item.ContainsPhi,
                IsAnonymized = item.IsAnonymized,
            })
            .OrderByDescending(item => item.UploadedUtc ?? item.CreatedUtc)
            .ToList();

        return (normalized, items);
    }

    public async Task<IReadOnlyList<RelayContactItem>> SearchContactsAsync(ShareRelaySettings settings, string query, CancellationToken cancellationToken)
    {
        ShareRelaySettings normalized = settings.Clone();
        normalized.Normalize();
        ValidateSettings(normalized, requireIdentity: false);

        string route = $"/api/v1/contacts/search?query={Uri.EscapeDataString(query ?? string.Empty)}";
        if (normalized.UserId != Guid.Empty)
        {
            route += $"&excludeUserId={normalized.UserId}";
        }

        ContactSearchResponse response = await SendAsync<ContactSearchResponse>(normalized, HttpMethod.Get, route, null, cancellationToken);
        return response.Contacts
            .Select(contact => new RelayContactItem(contact.Id, contact.DisplayName, contact.Email, contact.Organization))
            .ToList();
    }

    public async Task<(ShareRelaySettings Settings, RelayImportResult Result)> DownloadAndImportShareAsync(
        ShareRelaySettings settings,
        RelayInboxItem inboxItem,
        DicomImportService importService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inboxItem);
        ArgumentNullException.ThrowIfNull(importService);

        ShareRelaySettings normalized = await EnsureRegisteredForInboxAsync(settings, cancellationToken);
        EnsureDeviceKeyMaterial(normalized);

        string downloadPath = Path.Combine(_stagingDirectory, $"download-{inboxItem.ShareId:N}.kshare");
        string decryptedZipPath = Path.Combine(_stagingDirectory, $"download-{inboxItem.ShareId:N}.zip");
        string extractDirectory = Path.Combine(_stagingDirectory, $"extract-{inboxItem.ShareId:N}");

        TryDelete(downloadPath);
        TryDelete(decryptedZipPath);
        TryDeleteDirectory(extractDirectory);

        try
        {
            await DownloadPackageAsync(normalized, inboxItem.ShareId, downloadPath, cancellationToken);
            await DecryptRelayPackageAsync(normalized, downloadPath, decryptedZipPath, cancellationToken);

            Directory.CreateDirectory(extractDirectory);
            ZipFile.ExtractToDirectory(decryptedZipPath, extractDirectory, overwriteFiles: true);

            ImportResult importResult = await importService.ImportPathAsync(extractDirectory, cancellationToken);
            await AcknowledgeAsync(normalized, inboxItem.ShareId, "imported", $"Imported {importResult.ImportedStudies} studies / {importResult.ImportedInstances} instances.", cancellationToken);
            return (normalized, new RelayImportResult(inboxItem.ShareId, inboxItem.Subject, importResult));
        }
        finally
        {
            TryDelete(downloadPath);
            TryDelete(decryptedZipPath);
            TryDeleteDirectory(extractDirectory);
        }
    }

    public async Task<(ShareRelaySettings Settings, RelayShareResult Result)> ShareStudiesAsync(
        ShareRelaySettings settings,
        string subject,
        string? message,
        IReadOnlyList<Guid> recipientUserIds,
        IReadOnlyList<StudyDetails> studies,
        CancellationToken cancellationToken)
    {
        if (recipientUserIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one recipient.");
        }

        if (studies.Count == 0)
        {
            throw new InvalidOperationException("Select at least one study to share.");
        }

        ShareRelaySettings normalized = await RegisterAsync(settings, cancellationToken);
        List<PackagedFileEntry> packagedFiles = CollectPackagedFiles(studies);
        if (packagedFiles.Count == 0)
        {
            throw new InvalidOperationException("None of the selected studies has local DICOM files available.");
        }

        string resolvedSubject = string.IsNullOrWhiteSpace(subject) ? BuildSuggestedSubject(studies) : subject.Trim();
        Dictionary<Guid, IReadOnlyList<RelayContactDevice>> recipientDevices = await LoadRecipientDevicesAsync(normalized, recipientUserIds, cancellationToken);
        string plainZipPath = await CreatePlainStudyPackageAsync(studies, packagedFiles, cancellationToken);
        string packagePath = await CreateEncryptedRelayPackageAsync(normalized, resolvedSubject, studies, packagedFiles, recipientDevices, plainZipPath, cancellationToken);
        try
        {
            CreateShareResponse createResponse = await SendJsonAsync<CreateShareRequest, CreateShareResponse>(
                normalized,
                HttpMethod.Post,
                "/api/v1/shares",
                new CreateShareRequest(
                    normalized.UserId,
                    recipientUserIds,
                    resolvedSubject,
                    EmptyToNull(message),
                    "kpacs-relay-package",
                    ContainsPhi: true,
                    IsAnonymized: false,
                    EncryptedPackageCipherSuite,
                    null,
                    DateTimeOffset.UtcNow.AddDays(14)),
                cancellationToken);

            string uploadRoute = $"/api/v1/shares/{createResponse.ShareId}/package?actorUserId={normalized.UserId}&fileName={Uri.EscapeDataString(Path.GetFileName(packagePath))}";
            await UploadPackageAsync(normalized, uploadRoute, packagePath, cancellationToken);

            long packageSize = new FileInfo(packagePath).Length;
            RelayShareResult result = new(
                createResponse.ShareId,
                studies.Count,
                recipientUserIds.Count,
                packagedFiles.Count,
                packageSize,
                resolvedSubject);

            return (normalized, result);
        }
        finally
        {
            TryDelete(plainZipPath);
            TryDelete(packagePath);
        }
    }

    private async Task<ShareRelaySettings> EnsureRegisteredForInboxAsync(ShareRelaySettings settings, CancellationToken cancellationToken)
    {
        ShareRelaySettings normalized = settings.Clone();
        normalized.Normalize();
        ValidateSettings(normalized, requireIdentity: normalized.UserId == Guid.Empty);
        EnsureDeviceKeyMaterial(normalized);

        return normalized.UserId == Guid.Empty || normalized.DeviceId == Guid.Empty
            ? await RegisterAsync(normalized, cancellationToken)
            : normalized;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<RelayContactDevice>>> LoadRecipientDevicesAsync(ShareRelaySettings settings, IReadOnlyList<Guid> recipientUserIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<RelayContactDevice>>();

        foreach (Guid recipientUserId in recipientUserIds.Distinct())
        {
            ContactDeviceDirectoryResponse directory = await SendAsync<ContactDeviceDirectoryResponse>(
                settings,
                HttpMethod.Get,
                $"/api/v1/contacts/{recipientUserId}/devices",
                null,
                cancellationToken);

            IReadOnlyList<RelayContactDevice> devices = directory.Devices
                .Select(device => new RelayContactDevice(device.Id, device.UserId, device.DeviceName, device.Platform, device.ViewerVersion, device.PublicEncryptionKey, device.PublicSigningKey))
                .ToList();

            if (devices.Count == 0)
            {
                throw new InvalidOperationException("One or more recipients do not have any registered relay devices yet.");
            }

            result[recipientUserId] = devices;
        }

        return result;
    }

    private async Task DownloadPackageAsync(ShareRelaySettings settings, Guid shareId, string targetPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings, $"/api/v1/shares/{shareId}/package?actorUserId={settings.UserId}"));
        ApplyAuth(request, settings);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task AcknowledgeAsync(ShareRelaySettings settings, Guid shareId, string eventType, string? details, CancellationToken cancellationToken)
    {
        await SendWithoutResponseAsync(
            settings,
            HttpMethod.Post,
            $"/api/v1/shares/{shareId}/ack",
            JsonContent.Create(new ShareAcknowledgeRequest(settings.UserId, eventType, details), options: SerializerOptions),
            cancellationToken);
    }

    private async Task UploadPackageAsync(ShareRelaySettings settings, string route, string packagePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(settings, route));
        using var stream = File.OpenRead(packagePath);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        ApplyAuth(request, settings);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(ShareRelaySettings settings, HttpMethod method, string route, TRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(settings, route))
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };
        ApplyAuth(request, settings);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        TResponse? payload = await response.Content.ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("The relay returned an empty response.");
    }

    private async Task<TResponse> SendAsync<TResponse>(ShareRelaySettings settings, HttpMethod method, string route, HttpContent? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(settings, route))
        {
            Content = body,
        };
        ApplyAuth(request, settings);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        TResponse? payload = await response.Content.ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("The relay returned an empty response.");
    }

    private async Task SendWithoutResponseAsync(ShareRelaySettings settings, HttpMethod method, string route, HttpContent? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(settings, route))
        {
            Content = body,
        };
        ApplyAuth(request, settings);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(error))
        {
            response.EnsureSuccessStatusCode();
        }

        throw new InvalidOperationException($"Relay call failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
    }

    private string BuildUri(ShareRelaySettings settings, string route) => $"{settings.BaseUrl}{route}";

    private static void ApplyAuth(HttpRequestMessage request, ShareRelaySettings settings)
    {
        request.Headers.Remove("X-Relay-Api-Key");
        request.Headers.TryAddWithoutValidation("X-Relay-Api-Key", settings.ApiKey);
    }

    private async Task<string> CreatePlainStudyPackageAsync(IReadOnlyList<StudyDetails> studies, IReadOnlyList<PackagedFileEntry> packagedFiles, CancellationToken cancellationToken)
    {
        string packagePath = Path.Combine(_stagingDirectory, $"share-{Guid.NewGuid():N}.zip");

        await using FileStream fileStream = new(packagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 64, useAsync: true);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true);

        ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using (Stream manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(manifestStream, BuildManifest(studies, packagedFiles), SerializerOptions, cancellationToken);
        }

        foreach (PackagedFileEntry file in packagedFiles)
        {
            ZipArchiveEntry entry = archive.CreateEntry(file.EntryName, CompressionLevel.Fastest);
            await using Stream target = entry.Open();
            await using FileStream source = new(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
        }

        await fileStream.FlushAsync(cancellationToken);
        return packagePath;
    }

    private async Task<string> CreateEncryptedRelayPackageAsync(
        ShareRelaySettings settings,
        string subject,
        IReadOnlyList<StudyDetails> studies,
        IReadOnlyList<PackagedFileEntry> packagedFiles,
        IReadOnlyDictionary<Guid, IReadOnlyList<RelayContactDevice>> recipientDevices,
        string plainZipPath,
        CancellationToken cancellationToken)
    {
        byte[] plainBytes = await File.ReadAllBytesAsync(plainZipPath, cancellationToken);
        byte[] contentKey = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[plainBytes.Length];
        byte[] tag = new byte[16];

        using (var aes = new AesGcm(contentKey, tag.Length))
        {
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        }

        List<WrappedKeyEnvelope> wrappedKeys = BuildWrappedKeys(recipientDevices, contentKey);
        string packagePath = Path.Combine(_stagingDirectory, $"encrypted-share-{Guid.NewGuid():N}.kshare");

        await using FileStream fileStream = new(packagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 64, useAsync: true);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true);

        RelayPackageEnvelope envelope = new(
            1,
            EncryptedPackageCipherSuite,
            DateTimeOffset.UtcNow,
            settings.UserId,
            settings.DeviceId,
            subject,
            Path.GetFileName(plainZipPath),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            wrappedKeys,
            BuildManifest(studies, packagedFiles));

        ZipArchiveEntry metadataEntry = archive.CreateEntry("relay-package.json", CompressionLevel.Optimal);
        await using (Stream metadataStream = metadataEntry.Open())
        {
            await JsonSerializer.SerializeAsync(metadataStream, envelope, SerializerOptions, cancellationToken);
        }

        ZipArchiveEntry payloadEntry = archive.CreateEntry("payload.bin", CompressionLevel.NoCompression);
        await using (Stream payloadStream = payloadEntry.Open())
        {
            await payloadStream.WriteAsync(ciphertext, cancellationToken);
        }

        await fileStream.FlushAsync(cancellationToken);
        return packagePath;
    }

    private async Task DecryptRelayPackageAsync(ShareRelaySettings settings, string encryptedPackagePath, string decryptedZipPath, CancellationToken cancellationToken)
    {
        EnsureDeviceKeyMaterial(settings);
        using var archive = ZipFile.OpenRead(encryptedPackagePath);
        ZipArchiveEntry? metadataEntry = archive.GetEntry("relay-package.json");
        ZipArchiveEntry? payloadEntry = archive.GetEntry("payload.bin");
        if (metadataEntry is null || payloadEntry is null)
        {
            File.Copy(encryptedPackagePath, decryptedZipPath, overwrite: true);
            return;
        }

        RelayPackageEnvelope? envelope;
        using (Stream metadataStream = metadataEntry.Open())
        {
            envelope = await JsonSerializer.DeserializeAsync<RelayPackageEnvelope>(metadataStream, SerializerOptions, cancellationToken);
        }

        if (envelope is null)
        {
            throw new InvalidOperationException("The relay package metadata could not be read.");
        }

        WrappedKeyEnvelope? wrappedKey = envelope.WrappedKeys.FirstOrDefault(item => item.DeviceId == settings.DeviceId)
            ?? envelope.WrappedKeys.FirstOrDefault(item => item.RecipientUserId == settings.UserId);
        if (wrappedKey is null)
        {
            throw new InvalidOperationException("This relay package is not encrypted for the current device.");
        }

        byte[] encryptedContentKey = Convert.FromBase64String(wrappedKey.WrappedKey);
        byte[] contentKey;
        using (RSA rsa = RSA.Create())
        {
            rsa.ImportFromPem(settings.PrivateEncryptionKey);
            contentKey = rsa.Decrypt(encryptedContentKey, RSAEncryptionPadding.OaepSHA256);
        }

        byte[] ciphertext;
        await using (Stream payloadStream = payloadEntry.Open())
        using (var memory = new MemoryStream())
        {
            await payloadStream.CopyToAsync(memory, cancellationToken);
            ciphertext = memory.ToArray();
        }

        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
        byte[] tag = Convert.FromBase64String(envelope.Tag);
        byte[] plainBytes = new byte[ciphertext.Length];
        using (var aes = new AesGcm(contentKey, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        }

        await File.WriteAllBytesAsync(decryptedZipPath, plainBytes, cancellationToken);
    }

    private static List<WrappedKeyEnvelope> BuildWrappedKeys(IReadOnlyDictionary<Guid, IReadOnlyList<RelayContactDevice>> recipientDevices, byte[] contentKey)
    {
        var wrappedKeys = new List<WrappedKeyEnvelope>();

        foreach ((Guid recipientUserId, IReadOnlyList<RelayContactDevice> devices) in recipientDevices)
        {
            foreach (RelayContactDevice device in devices)
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(device.PublicEncryptionKey);
                byte[] wrappedKey = rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256);
                wrappedKeys.Add(new WrappedKeyEnvelope(
                    recipientUserId,
                    device.DeviceId,
                    device.DeviceName,
                    device.Platform,
                    Convert.ToBase64String(wrappedKey)));
            }
        }

        return wrappedKeys;
    }

    private static object BuildManifest(IReadOnlyList<StudyDetails> studies, IReadOnlyList<PackagedFileEntry> packagedFiles)
    {
        return new
        {
            createdUtc = DateTimeOffset.UtcNow,
            studyCount = studies.Count,
            fileCount = packagedFiles.Count,
            studies = studies.Select(study => new
            {
                studyInstanceUid = study.Study.StudyInstanceUid,
                patientName = study.Study.PatientName,
                patientId = study.Study.PatientId,
                studyDate = study.Study.StudyDate,
                studyDescription = study.Study.StudyDescription,
                modalities = study.Study.Modalities,
                series = study.Series.Select(series => new
                {
                    seriesInstanceUid = series.SeriesInstanceUid,
                    modality = series.Modality,
                    seriesDescription = series.SeriesDescription,
                    instanceCount = series.Instances.Count(instance => !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath)),
                }),
            }),
        };
    }

    private static List<PackagedFileEntry> CollectPackagedFiles(IReadOnlyList<StudyDetails> studies)
    {
        var packagedFiles = new List<PackagedFileEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int studyIndex = 0; studyIndex < studies.Count; studyIndex++)
        {
            StudyDetails study = studies[studyIndex];
            for (int seriesIndex = 0; seriesIndex < study.Series.Count; seriesIndex++)
            {
                SeriesRecord series = study.Series[seriesIndex];
                int entryIndex = 0;
                foreach (InstanceRecord instance in series.Instances)
                {
                    if (string.IsNullOrWhiteSpace(instance.FilePath) || !File.Exists(instance.FilePath) || !seenPaths.Add(instance.FilePath))
                    {
                        continue;
                    }

                    entryIndex++;
                    string extension = Path.GetExtension(instance.FilePath);
                    if (string.IsNullOrWhiteSpace(extension))
                    {
                        extension = ".dcm";
                    }

                    string entryName = $"study-{studyIndex + 1:D2}-{SanitizePathComponent(study.Study.StudyInstanceUid)}/series-{seriesIndex + 1:D2}-{SanitizePathComponent(series.SeriesInstanceUid)}/{entryIndex:D5}-{SanitizePathComponent(instance.SopInstanceUid)}{extension}";
                    packagedFiles.Add(new PackagedFileEntry(instance.FilePath, entryName));
                }
            }
        }

        return packagedFiles;
    }

    private static string SanitizePathComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "item";
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return builder.ToString();
    }

    private static void ValidateSettings(ShareRelaySettings settings, bool requireIdentity)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new InvalidOperationException("Relay URL is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("Relay API key is required.");
        }

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Relay URL must be an absolute URL.");
        }

        if (!requireIdentity)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.UserEmail) || string.IsNullOrWhiteSpace(settings.DisplayName))
        {
            throw new InvalidOperationException("Relay email and display name are required.");
        }
    }

    private static void EnsureDeviceKeyMaterial(ShareRelaySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PublicEncryptionKey) && !string.IsNullOrWhiteSpace(settings.PrivateEncryptionKey))
        {
            if (string.IsNullOrWhiteSpace(settings.PublicSigningKey))
            {
                settings.PublicSigningKey = settings.PublicEncryptionKey;
            }

            return;
        }

        using RSA rsa = RSA.Create(3072);
        settings.PublicEncryptionKey = rsa.ExportRSAPublicKeyPem().Trim();
        settings.PrivateEncryptionKey = rsa.ExportRSAPrivateKeyPem().Trim();
        if (string.IsNullOrWhiteSpace(settings.PublicSigningKey))
        {
            settings.PublicSigningKey = settings.PublicEncryptionKey;
        }
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "unknown";
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record RegisterUserRequest(string Email, string DisplayName, string? Organization);

    private sealed record UserResponse(Guid Id, string Email, string DisplayName, string? Organization, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

    private sealed record RegisterDeviceRequest(Guid UserId, string DeviceName, string Platform, string? ViewerVersion, string PublicEncryptionKey, string PublicSigningKey);

    private sealed record DeviceResponse(Guid Id, Guid UserId, string DeviceName, string Platform, string? ViewerVersion, string PublicEncryptionKey, string PublicSigningKey, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? LastSeenUtc);

    private sealed record ContactSearchResponse(IReadOnlyList<ContactResponse> Contacts);

    private sealed record ContactResponse(Guid Id, string Email, string DisplayName, string? Organization, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

    private sealed record ContactDeviceDirectoryResponse(Guid UserId, IReadOnlyList<ContactDeviceResponse> Devices);

    private sealed record ContactDeviceResponse(Guid Id, Guid UserId, string DeviceName, string Platform, string? ViewerVersion, string PublicEncryptionKey, string PublicSigningKey, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? LastSeenUtc);

    private sealed record ShareAcknowledgeRequest(Guid ActorUserId, string EventType, string? Details);

    private sealed record InboxResponse(Guid RecipientUserId, IReadOnlyList<ShareSummaryResponse> Items);

    private sealed record ShareSummaryResponse(Guid ShareId, Guid SenderUserId, string SenderEmail, string SenderDisplayName, string Subject, string? Message, string PackageType, bool ContainsPhi, bool IsAnonymized, string Status, string CipherSuite, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, DateTimeOffset? UploadedUtc, bool PackageAvailable, string? PackageFileName, long? PackageSizeBytes, string? ViewerDownloadUrl, IReadOnlyList<ShareRecipientResponse> Recipients);

    private sealed record ShareRecipientResponse(Guid RecipientUserId, string RecipientEmail, string RecipientDisplayName, string Status, DateTimeOffset? ViewedUtc, DateTimeOffset? DownloadedUtc, DateTimeOffset? ImportedUtc, DateTimeOffset? AcknowledgedUtc);

    private sealed record CreateShareRequest(Guid SenderUserId, IReadOnlyList<Guid> RecipientUserIds, string Subject, string? Message, string PackageType, bool ContainsPhi, bool IsAnonymized, string CipherSuite, string? ViewerDownloadUrl, DateTimeOffset ExpiresUtc);

    private sealed record CreateShareResponse(Guid ShareId, string Status, string UploadUrl, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

    private sealed record RelayPackageEnvelope(int Version, string CipherSuite, DateTimeOffset CreatedUtc, Guid SenderUserId, Guid SenderDeviceId, string Subject, string InnerPackageFileName, string Nonce, string Tag, IReadOnlyList<WrappedKeyEnvelope> WrappedKeys, object Manifest);

    private sealed record WrappedKeyEnvelope(Guid RecipientUserId, Guid DeviceId, string DeviceName, string Platform, string WrappedKey);

    private sealed record PackagedFileEntry(string SourcePath, string EntryName);
}
