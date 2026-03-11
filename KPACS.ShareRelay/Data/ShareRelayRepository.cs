using System.Data;
using System.Text.Json;
using Dapper;
using KPACS.ShareRelay.Contracts;
using KPACS.ShareRelay.Infrastructure;
using Npgsql;

namespace KPACS.ShareRelay.Data;

public sealed class ShareRelayRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ShareRelayRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists relay_users (
                id uuid primary key,
                email text not null,
                display_name text not null,
                organization text null,
                is_active boolean not null default true,
                created_utc timestamptz not null,
                updated_utc timestamptz not null
            );

            create unique index if not exists ux_relay_users_email on relay_users ((lower(email)));

            create table if not exists relay_devices (
                id uuid primary key,
                user_id uuid not null references relay_users(id) on delete cascade,
                device_name text not null,
                platform text not null,
                viewer_version text null,
                public_encryption_key text not null,
                public_signing_key text not null,
                status text not null,
                created_utc timestamptz not null,
                updated_utc timestamptz not null,
                last_seen_utc timestamptz null
            );

            create unique index if not exists ux_relay_devices_user_device_name on relay_devices (user_id, lower(device_name));

            create table if not exists relay_shares (
                id uuid primary key,
                sender_user_id uuid not null references relay_users(id),
                subject text not null,
                message text null,
                package_type text not null,
                contains_phi boolean not null,
                is_anonymized boolean not null,
                status text not null,
                cipher_suite text not null,
                viewer_download_url text null,
                package_available boolean not null default false,
                package_file_name text null,
                package_size_bytes bigint null,
                package_sha256 text null,
                storage_key text null,
                created_utc timestamptz not null,
                expires_utc timestamptz not null,
                uploaded_utc timestamptz null
            );

            create index if not exists ix_relay_shares_sender_user_id on relay_shares (sender_user_id);
            create index if not exists ix_relay_shares_created_utc on relay_shares (created_utc desc);

            create table if not exists relay_share_recipients (
                share_id uuid not null references relay_shares(id) on delete cascade,
                recipient_user_id uuid not null references relay_users(id) on delete cascade,
                status text not null,
                viewed_utc timestamptz null,
                downloaded_utc timestamptz null,
                imported_utc timestamptz null,
                acknowledged_utc timestamptz null,
                primary key (share_id, recipient_user_id)
            );

            create index if not exists ix_relay_share_recipients_recipient_user_id on relay_share_recipients (recipient_user_id);

            create table if not exists relay_share_events (
                id uuid primary key,
                share_id uuid not null references relay_shares(id) on delete cascade,
                actor_user_id uuid null references relay_users(id),
                recipient_user_id uuid null references relay_users(id),
                event_type text not null,
                details_json text null,
                created_utc timestamptz not null
            );

            create index if not exists ix_relay_share_events_share_id on relay_share_events (share_id, created_utc desc);
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<UserResponse> RegisterUserAsync(RegisterUserRequest request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string lookupSql = """
            select id, email, display_name as DisplayName, organization, created_utc as CreatedUtc, updated_utc as UpdatedUtc
            from relay_users
            where lower(email) = lower(@Email);
            """;
        const string insertSql = """
            insert into relay_users (id, email, display_name, organization, created_utc, updated_utc)
            values (@Id, @Email, @DisplayName, @Organization, @CreatedUtc, @UpdatedUtc);
            """;
        const string updateSql = """
            update relay_users
            set display_name = @DisplayName,
                organization = @Organization,
                updated_utc = @UpdatedUtc
            where id = @Id;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        UserResponse? existing = await connection.QuerySingleOrDefaultAsync<UserResponse>(
            new CommandDefinition(lookupSql, new { Email = normalizedEmail }, cancellationToken: cancellationToken));

        if (existing is null)
        {
            UserResponse created = new(Guid.NewGuid(), normalizedEmail, request.DisplayName.Trim(), NormalizeOptional(request.Organization), now, now);
            await connection.ExecuteAsync(new CommandDefinition(insertSql, new
            {
                created.Id,
                created.Email,
                DisplayName = created.DisplayName,
                created.Organization,
                CreatedUtc = created.CreatedUtc,
                UpdatedUtc = created.UpdatedUtc,
            }, cancellationToken: cancellationToken));
            return created;
        }

        UserResponse updated = existing with
        {
            DisplayName = request.DisplayName.Trim(),
            Organization = NormalizeOptional(request.Organization),
            UpdatedUtc = now,
        };
        await connection.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            updated.Id,
            DisplayName = updated.DisplayName,
            updated.Organization,
            UpdatedUtc = updated.UpdatedUtc,
        }, cancellationToken: cancellationToken));
        return updated;
    }

    public async Task<DeviceResponse> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        await EnsureUserExistsAsync(request.UserId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string lookupSql = """
            select id, user_id as UserId, device_name as DeviceName, platform, viewer_version as ViewerVersion,
                   public_encryption_key as PublicEncryptionKey, public_signing_key as PublicSigningKey,
                   created_utc as CreatedUtc, updated_utc as UpdatedUtc, last_seen_utc as LastSeenUtc
            from relay_devices
            where user_id = @UserId and lower(device_name) = lower(@DeviceName);
            """;
        const string insertSql = """
            insert into relay_devices (
                id, user_id, device_name, platform, viewer_version,
                public_encryption_key, public_signing_key, status,
                created_utc, updated_utc, last_seen_utc)
            values (
                @Id, @UserId, @DeviceName, @Platform, @ViewerVersion,
                @PublicEncryptionKey, @PublicSigningKey, 'active',
                @CreatedUtc, @UpdatedUtc, @LastSeenUtc);
            """;
        const string updateSql = """
            update relay_devices
            set platform = @Platform,
                viewer_version = @ViewerVersion,
                public_encryption_key = @PublicEncryptionKey,
                public_signing_key = @PublicSigningKey,
                updated_utc = @UpdatedUtc,
                last_seen_utc = @LastSeenUtc,
                status = 'active'
            where id = @Id;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        DeviceResponse? existing = await connection.QuerySingleOrDefaultAsync<DeviceResponse>(
            new CommandDefinition(lookupSql, new { request.UserId, DeviceName = request.DeviceName.Trim() }, cancellationToken: cancellationToken));

        if (existing is null)
        {
            DeviceResponse created = new(
                Guid.NewGuid(),
                request.UserId,
                request.DeviceName.Trim(),
                request.Platform.Trim(),
                NormalizeOptional(request.ViewerVersion),
                request.PublicEncryptionKey.Trim(),
                request.PublicSigningKey.Trim(),
                now,
                now,
                now);

            await connection.ExecuteAsync(new CommandDefinition(insertSql, new
            {
                created.Id,
                created.UserId,
                DeviceName = created.DeviceName,
                created.Platform,
                ViewerVersion = created.ViewerVersion,
                PublicEncryptionKey = created.PublicEncryptionKey,
                PublicSigningKey = created.PublicSigningKey,
                CreatedUtc = created.CreatedUtc,
                UpdatedUtc = created.UpdatedUtc,
                LastSeenUtc = created.LastSeenUtc,
            }, cancellationToken: cancellationToken));
            return created;
        }

        DeviceResponse updated = existing with
        {
            Platform = request.Platform.Trim(),
            ViewerVersion = NormalizeOptional(request.ViewerVersion),
            PublicEncryptionKey = request.PublicEncryptionKey.Trim(),
            PublicSigningKey = request.PublicSigningKey.Trim(),
            UpdatedUtc = now,
            LastSeenUtc = now,
        };

        await connection.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            updated.Id,
            updated.Platform,
            ViewerVersion = updated.ViewerVersion,
            PublicEncryptionKey = updated.PublicEncryptionKey,
            PublicSigningKey = updated.PublicSigningKey,
            UpdatedUtc = updated.UpdatedUtc,
            LastSeenUtc = updated.LastSeenUtc,
        }, cancellationToken: cancellationToken));

        return updated;
    }

    public async Task<IReadOnlyList<UserResponse>> SearchContactsAsync(string? query, Guid? excludeUserId, CancellationToken cancellationToken)
    {
        string normalizedQuery = string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim();
        const string sql = """
            select id, email, display_name as DisplayName, organization, created_utc as CreatedUtc, updated_utc as UpdatedUtc
            from relay_users
            where is_active = true
              and (@ExcludeUserId is null or id <> @ExcludeUserId)
              and (
                    @Search = ''
                    or lower(email) like lower(@Pattern)
                    or lower(display_name) like lower(@Pattern)
                    or lower(coalesce(organization, '')) like lower(@Pattern)
                  )
            order by display_name, email
            limit 25;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        List<UserResponse> results = (await connection.QueryAsync<UserResponse>(new CommandDefinition(
            sql,
            new
            {
                ExcludeUserId = excludeUserId,
                Search = normalizedQuery,
                Pattern = $"%{normalizedQuery}%",
            },
            cancellationToken: cancellationToken))).ToList();
        return results;
    }

    public async Task<IReadOnlyList<ContactDeviceResponse>> GetDevicesForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureUserExistsAsync(userId, cancellationToken);

        const string sql = """
            select id,
                   user_id as UserId,
                   device_name as DeviceName,
                   platform,
                   viewer_version as ViewerVersion,
                   public_encryption_key as PublicEncryptionKey,
                   public_signing_key as PublicSigningKey,
                   created_utc as CreatedUtc,
                   updated_utc as UpdatedUtc,
                   last_seen_utc as LastSeenUtc
            from relay_devices
            where user_id = @UserId
              and status = 'active'
            order by coalesce(last_seen_utc, created_utc) desc, device_name;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ContactDeviceResponse>(new CommandDefinition(
            sql,
            new { UserId = userId },
            cancellationToken: cancellationToken))).ToList();
    }

    public async Task<CreateShareResponse> CreateShareAsync(CreateShareRequest request, CancellationToken cancellationToken)
    {
        if (request.RecipientUserIds.Count == 0)
        {
            throw new InvalidOperationException("At least one recipient must be specified.");
        }

        await EnsureUserExistsAsync(request.SenderUserId, cancellationToken);
        await EnsureUsersExistAsync(request.RecipientUserIds, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid shareId = Guid.NewGuid();

        const string insertShareSql = """
            insert into relay_shares (
                id, sender_user_id, subject, message, package_type, contains_phi, is_anonymized,
                status, cipher_suite, viewer_download_url, created_utc, expires_utc)
            values (
                @Id, @SenderUserId, @Subject, @Message, @PackageType, @ContainsPhi, @IsAnonymized,
                @Status, @CipherSuite, @ViewerDownloadUrl, @CreatedUtc, @ExpiresUtc);
            """;
        const string insertRecipientSql = """
            insert into relay_share_recipients (share_id, recipient_user_id, status)
            values (@ShareId, @RecipientUserId, 'pending')
            on conflict (share_id, recipient_user_id) do nothing;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(insertShareSql, new
        {
            Id = shareId,
            request.SenderUserId,
            Subject = request.Subject.Trim(),
            Message = NormalizeOptional(request.Message),
            PackageType = request.PackageType.Trim(),
            request.ContainsPhi,
            request.IsAnonymized,
            Status = "awaiting_upload",
            CipherSuite = request.CipherSuite.Trim(),
            ViewerDownloadUrl = NormalizeOptional(request.ViewerDownloadUrl),
            CreatedUtc = now,
            request.ExpiresUtc,
        }, transaction, cancellationToken: cancellationToken));

        foreach (Guid recipientUserId in request.RecipientUserIds.Distinct())
        {
            await connection.ExecuteAsync(new CommandDefinition(insertRecipientSql, new
            {
                ShareId = shareId,
                RecipientUserId = recipientUserId,
            }, transaction, cancellationToken: cancellationToken));
        }

        await AppendEventAsync(connection, transaction, shareId, request.SenderUserId, null, "share-created", new { request.PackageType, request.ContainsPhi, request.IsAnonymized }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreateShareResponse(
            shareId,
            "awaiting_upload",
            $"/api/v1/shares/{shareId}/package?actorUserId={request.SenderUserId}",
            now,
            request.ExpiresUtc);
    }

    public async Task<PackageUploadResponse> AttachPackageAsync(Guid shareId, Guid actorUserId, StoredPackage package, CancellationToken cancellationToken)
    {
        await EnsureShareSenderAsync(shareId, actorUserId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string updateSql = """
            update relay_shares
            set status = 'uploaded',
                package_available = true,
                package_file_name = @PackageFileName,
                package_size_bytes = @PackageSizeBytes,
                package_sha256 = @PackageSha256,
                storage_key = @StorageKey,
                uploaded_utc = @UploadedUtc
            where id = @ShareId;
            """;

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            ShareId = shareId,
            PackageFileName = package.FileName,
            PackageSizeBytes = package.SizeBytes,
            PackageSha256 = package.Sha256,
            StorageKey = package.StorageKey,
            UploadedUtc = now,
        }, transaction, cancellationToken: cancellationToken));
        await AppendEventAsync(connection, transaction, shareId, actorUserId, null, "package-uploaded", new { package.FileName, package.SizeBytes, package.Sha256 }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PackageUploadResponse(shareId, "uploaded", package.StorageKey, package.FileName, package.SizeBytes, package.Sha256, now);
    }

    public async Task<ShareSummaryResponse?> GetShareAsync(Guid shareId, Guid actorUserId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        ShareQueryRow? share = await connection.QuerySingleOrDefaultAsync<ShareQueryRow>(new CommandDefinition(
            ShareBaseSql + " where s.id = @ShareId and (s.sender_user_id = @ActorUserId or exists (select 1 from relay_share_recipients sr where sr.share_id = s.id and sr.recipient_user_id = @ActorUserId));",
            new { ShareId = shareId, ActorUserId = actorUserId }, cancellationToken: cancellationToken));

        if (share is null)
        {
            return null;
        }

        IReadOnlyList<ShareRecipientResponse> recipients = await LoadRecipientsAsync(connection, shareId, cancellationToken);
        return ToSummary(share, recipients);
    }

    public async Task<IReadOnlyList<ShareSummaryResponse>> GetInboxAsync(Guid recipientUserId, CancellationToken cancellationToken)
    {
        await EnsureUserExistsAsync(recipientUserId, cancellationToken);
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        List<ShareQueryRow> rows = (await connection.QueryAsync<ShareQueryRow>(new CommandDefinition(
            ShareBaseSql + " inner join relay_share_recipients inbox on inbox.share_id = s.id where inbox.recipient_user_id = @RecipientUserId order by s.created_utc desc;",
            new { RecipientUserId = recipientUserId }, cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, IReadOnlyList<ShareRecipientResponse>> recipientMap = await LoadRecipientsByShareAsync(connection, rows.Select(row => row.Id).ToArray(), cancellationToken);
        return rows.Select(row => ToSummary(row, recipientMap[row.Id])).ToList();
    }

    public async Task RecordAcknowledgementAsync(Guid shareId, ShareAcknowledgeRequest request, CancellationToken cancellationToken)
    {
        string eventType = request.EventType.Trim().ToLowerInvariant();
        if (!AllowedAcknowledgements.Contains(eventType))
        {
            throw new InvalidOperationException($"Unsupported acknowledgement event '{request.EventType}'.");
        }

        await EnsureRecipientMembershipAsync(shareId, request.ActorUserId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string updateSql = eventType switch
        {
            "viewed" => "update relay_share_recipients set status = 'viewed', viewed_utc = coalesce(viewed_utc, @Now), acknowledged_utc = @Now where share_id = @ShareId and recipient_user_id = @ActorUserId;",
            "downloaded" => "update relay_share_recipients set status = 'downloaded', downloaded_utc = coalesce(downloaded_utc, @Now), acknowledged_utc = @Now where share_id = @ShareId and recipient_user_id = @ActorUserId;",
            "imported" => "update relay_share_recipients set status = 'imported', imported_utc = coalesce(imported_utc, @Now), acknowledged_utc = @Now where share_id = @ShareId and recipient_user_id = @ActorUserId;",
            _ => "update relay_share_recipients set acknowledged_utc = @Now where share_id = @ShareId and recipient_user_id = @ActorUserId;",
        };

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(updateSql, new { ShareId = shareId, ActorUserId = request.ActorUserId, Now = now }, transaction, cancellationToken: cancellationToken));
        await AppendEventAsync(connection, transaction, shareId, request.ActorUserId, request.ActorUserId, $"recipient-{eventType}", NormalizeOptional(request.Details), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(ShareSummaryResponse Share, string StorageKey)> GetDownloadAsync(Guid shareId, Guid actorUserId, CancellationToken cancellationToken)
    {
        ShareSummaryResponse? share = await GetShareAsync(shareId, actorUserId, cancellationToken);
        if (share is null)
        {
            throw new InvalidOperationException("The requested share was not found for this actor.");
        }

        if (!share.PackageAvailable)
        {
            throw new InvalidOperationException("The share package has not been uploaded yet.");
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        string? storageKey = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select storage_key from relay_shares where id = @ShareId;",
            new { ShareId = shareId }, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new InvalidOperationException("The share does not have a package storage key.");
        }

        await RecordAcknowledgementAsync(shareId, new ShareAcknowledgeRequest(actorUserId, "downloaded", "Download endpoint used."), cancellationToken);
        return (share, storageKey);
    }

    private async Task EnsureUserExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from relay_users where id = @UserId;",
            new { UserId = userId }, cancellationToken: cancellationToken));
        if (count == 0)
        {
            throw new InvalidOperationException($"User '{userId}' does not exist.");
        }
    }

    private async Task EnsureUsersExistAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        Guid[] ids = userIds.Distinct().ToArray();
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from relay_users where id = any(@Ids);",
            new { Ids = ids }, cancellationToken: cancellationToken));
        if (count != ids.Length)
        {
            throw new InvalidOperationException("One or more recipients do not exist.");
        }
    }

    private async Task EnsureShareSenderAsync(Guid shareId, Guid senderUserId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from relay_shares where id = @ShareId and sender_user_id = @SenderUserId;",
            new { ShareId = shareId, SenderUserId = senderUserId }, cancellationToken: cancellationToken));
        if (count == 0)
        {
            throw new InvalidOperationException("Only the original sender can upload a package for this share.");
        }
    }

    private async Task EnsureRecipientMembershipAsync(Guid shareId, Guid actorUserId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from relay_share_recipients where share_id = @ShareId and recipient_user_id = @ActorUserId;",
            new { ShareId = shareId, ActorUserId = actorUserId }, cancellationToken: cancellationToken));
        if (count == 0)
        {
            throw new InvalidOperationException("The actor is not a recipient of this share.");
        }
    }

    private async Task<IReadOnlyList<ShareRecipientResponse>> LoadRecipientsAsync(NpgsqlConnection connection, Guid shareId, CancellationToken cancellationToken)
    {
        return (await connection.QueryAsync<ShareRecipientResponse>(new CommandDefinition(
            RecipientSql + " where sr.share_id = @ShareId order by u.display_name, u.email;",
            new { ShareId = shareId }, cancellationToken: cancellationToken))).ToList();
    }

    private async Task<Dictionary<Guid, IReadOnlyList<ShareRecipientResponse>>> LoadRecipientsByShareAsync(NpgsqlConnection connection, Guid[] shareIds, CancellationToken cancellationToken)
    {
        List<RecipientQueryRow> rows = (await connection.QueryAsync<RecipientQueryRow>(new CommandDefinition(
            """
            select sr.share_id as ShareId,
                   sr.recipient_user_id as RecipientUserId,
                   u.email as RecipientEmail,
                   u.display_name as RecipientDisplayName,
                   sr.status,
                   sr.viewed_utc as ViewedUtc,
                   sr.downloaded_utc as DownloadedUtc,
                   sr.imported_utc as ImportedUtc,
                   sr.acknowledged_utc as AcknowledgedUtc
            from relay_share_recipients sr
            inner join relay_users u on u.id = sr.recipient_user_id
            where sr.share_id = any(@ShareIds)
            order by u.display_name, u.email;
            """,
            new { ShareIds = shareIds }, cancellationToken: cancellationToken))).ToList();

        return rows.GroupBy(row => row.ShareId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ShareRecipientResponse>)group.Select(row => new ShareRecipientResponse(
                    row.RecipientUserId,
                    row.RecipientEmail,
                    row.RecipientDisplayName,
                    row.Status,
                    row.ViewedUtc,
                    row.DownloadedUtc,
                    row.ImportedUtc,
                    row.AcknowledgedUtc)).ToList());
    }

    private static ShareSummaryResponse ToSummary(ShareQueryRow row, IReadOnlyList<ShareRecipientResponse> recipients) =>
        new(
            row.Id,
            row.SenderUserId,
            row.SenderEmail,
            row.SenderDisplayName,
            row.Subject,
            row.Message,
            row.PackageType,
            row.ContainsPhi,
            row.IsAnonymized,
            row.Status,
            row.CipherSuite,
            row.CreatedUtc,
            row.ExpiresUtc,
            row.UploadedUtc,
            row.PackageAvailable,
            row.PackageFileName,
            row.PackageSizeBytes,
            row.ViewerDownloadUrl,
            recipients);

    private static async Task AppendEventAsync(NpgsqlConnection connection, IDbTransaction transaction, Guid shareId, Guid? actorUserId, Guid? recipientUserId, string eventType, object? details, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into relay_share_events (id, share_id, actor_user_id, recipient_user_id, event_type, details_json, created_utc)
            values (@Id, @ShareId, @ActorUserId, @RecipientUserId, @EventType, @DetailsJson, @CreatedUtc);
            """;
        string? detailsJson = details switch
        {
            null => null,
            string detailsString when string.IsNullOrWhiteSpace(detailsString) => null,
            string detailsString => JsonSerializer.Serialize(new { message = detailsString }),
            _ => JsonSerializer.Serialize(details),
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = Guid.NewGuid(),
            ShareId = shareId,
            ActorUserId = actorUserId,
            RecipientUserId = recipientUserId,
            EventType = eventType,
            DetailsJson = detailsJson,
            CreatedUtc = DateTimeOffset.UtcNow,
        }, transaction, cancellationToken: cancellationToken));
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string ShareBaseSql = """
        select s.id,
               s.sender_user_id as SenderUserId,
               sender.email as SenderEmail,
               sender.display_name as SenderDisplayName,
               s.subject,
               s.message,
               s.package_type as PackageType,
               s.contains_phi as ContainsPhi,
               s.is_anonymized as IsAnonymized,
               s.status,
               s.cipher_suite as CipherSuite,
               s.created_utc as CreatedUtc,
               s.expires_utc as ExpiresUtc,
               s.uploaded_utc as UploadedUtc,
               s.package_available as PackageAvailable,
               s.package_file_name as PackageFileName,
               s.package_size_bytes as PackageSizeBytes,
               s.viewer_download_url as ViewerDownloadUrl
        from relay_shares s
        inner join relay_users sender on sender.id = s.sender_user_id
        """;

    private const string RecipientSql = """
        select sr.recipient_user_id as RecipientUserId,
               u.email as RecipientEmail,
               u.display_name as RecipientDisplayName,
               sr.status,
               sr.viewed_utc as ViewedUtc,
               sr.downloaded_utc as DownloadedUtc,
               sr.imported_utc as ImportedUtc,
               sr.acknowledged_utc as AcknowledgedUtc
        from relay_share_recipients sr
        inner join relay_users u on u.id = sr.recipient_user_id
        """;

    private static readonly HashSet<string> AllowedAcknowledgements = ["viewed", "downloaded", "imported", "acknowledged"];

    private sealed record ShareQueryRow(
        Guid Id,
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
        string? ViewerDownloadUrl);

    private sealed record RecipientQueryRow(
        Guid ShareId,
        Guid RecipientUserId,
        string RecipientEmail,
        string RecipientDisplayName,
        string Status,
        DateTimeOffset? ViewedUtc,
        DateTimeOffset? DownloadedUtc,
        DateTimeOffset? ImportedUtc,
        DateTimeOffset? AcknowledgedUtc);
}
