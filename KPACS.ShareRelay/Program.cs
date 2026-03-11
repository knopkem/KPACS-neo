using KPACS.ShareRelay.Contracts;
using KPACS.ShareRelay.Data;
using KPACS.ShareRelay.Infrastructure;
using Npgsql;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<PackageStorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<PrototypeAuthOptions>(builder.Configuration.GetSection(PrototypeAuthOptions.SectionName));
builder.Services.Configure<RelayRateLimitOptions>(builder.Configuration.GetSection(RelayRateLimitOptions.SectionName));

RelayRateLimitOptions rateLimitOptions = builder.Configuration.GetSection(RelayRateLimitOptions.SectionName).Get<RelayRateLimitOptions>() ?? new RelayRateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "Rate limit exceeded. Please retry in a moment.",
        }, cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (!rateLimitOptions.Enabled || !httpContext.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        string partitionKey = httpContext.Connection.RemoteIpAddress?.ToString()
            ?? httpContext.Request.Headers.Host.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, rateLimitOptions.PermitLimit),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = Math.Max(0, rateLimitOptions.QueueLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, rateLimitOptions.WindowSeconds)),
                AutoReplenishment = true,
            });
    });
});

string connectionString = PostgresConnectionStringResolver.Resolve(builder.Configuration);
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
NpgsqlDataSource dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<ShareRelayRepository>();
builder.Services.AddSingleton<FilePackageStorage>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

PrototypeAuthOptions authOptions = app.Configuration.GetSection(PrototypeAuthOptions.SectionName).Get<PrototypeAuthOptions>() ?? new PrototypeAuthOptions();
ILogger auditLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KPACS.ShareRelay.Audit");

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase),
    branch => branch.Use(async (context, next) =>
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long startedTicks = Stopwatch.GetTimestamp();
        string method = context.Request.Method;
        string path = context.Request.Path;
        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string userAgent = context.Request.Headers.UserAgent.ToString();
        string actorUserId = context.Request.Query["actorUserId"].FirstOrDefault()
            ?? context.Request.Query["recipientUserId"].FirstOrDefault()
            ?? context.Request.Query["excludeUserId"].FirstOrDefault()
            ?? string.Empty;

        try
        {
            await next(context);
        }
        finally
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;
            auditLogger.LogInformation(
                "relay_audit method={Method} path={Path} status={StatusCode} actor={ActorUserId} remoteIp={RemoteIp} durationMs={DurationMs} startedUtc={StartedUtc} userAgent={UserAgent}",
                method,
                path,
                context.Response.StatusCode,
                actorUserId,
                remoteIp,
                Math.Round(elapsedMs, 2),
                startedUtc,
                userAgent);
        }
    }));

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase),
    branch => branch.Use(async (context, next) =>
    {
        if (!authOptions.RequireApiKey)
        {
            await next(context);
            return;
        }

        string? presentedKey = TryGetPresentedApiKey(context.Request, authOptions.HeaderName);
        if (string.IsNullOrWhiteSpace(presentedKey) || !authOptions.ApiKeys.Any(configuredKey => AreKeysEqual(configuredKey, presentedKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Missing or invalid relay API key. Send {authOptions.HeaderName} or Authorization: Bearer <key>.",
            });
            return;
        }

        await next(context);
    }));

using (IServiceScope scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<ShareRelayRepository>();
    await repository.InitializeAsync(CancellationToken.None);
}

app.MapGet("/health", () => Results.Ok(new
{
    Status = "ok",
    Service = "KPACS.ShareRelay",
    Utc = DateTimeOffset.UtcNow,
}));

app.MapPost("/api/v1/users/register", async (RegisterUserRequest request, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["email"] = ["Email is required."],
            ["displayName"] = ["Display name is required."],
        });
    }

    UserResponse user = await repository.RegisterUserAsync(request, cancellationToken);
    return Results.Ok(user);
})
.WithName("RegisterUser");

app.MapPost("/api/v1/devices/register", async (RegisterDeviceRequest request, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty || string.IsNullOrWhiteSpace(request.DeviceName) || string.IsNullOrWhiteSpace(request.PublicEncryptionKey) || string.IsNullOrWhiteSpace(request.PublicSigningKey))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["device"] = ["UserId, device name, and both public keys are required."],
        });
    }

    try
    {
        DeviceResponse device = await repository.RegisterDeviceAsync(request, cancellationToken);
        return Results.Ok(device);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("RegisterDevice");

app.MapGet("/api/v1/contacts/search", async (string? query, Guid? excludeUserId, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    IReadOnlyList<UserResponse> contacts = await repository.SearchContactsAsync(query, excludeUserId, cancellationToken);
    return Results.Ok(new ContactSearchResponse(contacts));
})
.WithName("SearchContacts");

app.MapGet("/api/v1/contacts/{userId:guid}/devices", async (Guid userId, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    try
    {
        IReadOnlyList<ContactDeviceResponse> devices = await repository.GetDevicesForUserAsync(userId, cancellationToken);
        return Results.Ok(new ContactDeviceDirectoryResponse(userId, devices));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("GetContactDevices");

app.MapPost("/api/v1/shares", async (CreateShareRequest request, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (request.SenderUserId == Guid.Empty || request.RecipientUserIds.Count == 0 || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.PackageType) || string.IsNullOrWhiteSpace(request.CipherSuite))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["share"] = ["Sender, recipients, subject, package type, and cipher suite are required."],
        });
    }

    try
    {
        CreateShareResponse response = await repository.CreateShareAsync(request, cancellationToken);
        return Results.Created($"/api/v1/shares/{response.ShareId}", response);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("CreateShare");

app.MapPut("/api/v1/shares/{shareId:guid}/package", async (Guid shareId, Guid actorUserId, string? fileName, HttpRequest request, ShareRelayRepository repository, FilePackageStorage storage, CancellationToken cancellationToken) =>
{
    if (actorUserId == Guid.Empty)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["actorUserId"] = ["actorUserId query parameter is required."],
        });
    }

    if (request.ContentLength is null or <= 0)
    {
        return Results.BadRequest(new { error = "Package upload body is empty." });
    }

    try
    {
        StoredPackage storedPackage = await storage.SaveAsync(shareId, fileName ?? request.Headers.ContentDisposition.ToString(), request.Body, cancellationToken);
        PackageUploadResponse response = await repository.AttachPackageAsync(shareId, actorUserId, storedPackage, cancellationToken);
        return Results.Ok(response);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.DisableAntiforgery()
.WithName("UploadSharePackage");

app.MapGet("/api/v1/inbox", async (Guid recipientUserId, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (recipientUserId == Guid.Empty)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["recipientUserId"] = ["recipientUserId query parameter is required."],
        });
    }

    try
    {
        IReadOnlyList<ShareSummaryResponse> inbox = await repository.GetInboxAsync(recipientUserId, cancellationToken);
        return Results.Ok(new InboxResponse(recipientUserId, inbox));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("GetInbox");

app.MapGet("/api/v1/shares/{shareId:guid}", async (Guid shareId, Guid actorUserId, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (actorUserId == Guid.Empty)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["actorUserId"] = ["actorUserId query parameter is required."],
        });
    }

    ShareSummaryResponse? share = await repository.GetShareAsync(shareId, actorUserId, cancellationToken);
    return share is null ? Results.NotFound() : Results.Ok(share);
})
.WithName("GetShare");

app.MapGet("/api/v1/shares/{shareId:guid}/package", async (Guid shareId, Guid actorUserId, ShareRelayRepository repository, FilePackageStorage storage, CancellationToken cancellationToken) =>
{
    if (actorUserId == Guid.Empty)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["actorUserId"] = ["actorUserId query parameter is required."],
        });
    }

    try
    {
        (ShareSummaryResponse share, string storageKey) = await repository.GetDownloadAsync(shareId, actorUserId, cancellationToken);
        StoredPackageReadHandle handle = await storage.OpenReadAsync(storageKey, cancellationToken);
        string downloadName = share.PackageFileName ?? handle.FileName;
        return Results.File(handle.Stream, "application/octet-stream", downloadName, enableRangeProcessing: true);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
})
.WithName("DownloadSharePackage");

app.MapPost("/api/v1/shares/{shareId:guid}/ack", async (Guid shareId, ShareAcknowledgeRequest request, ShareRelayRepository repository, CancellationToken cancellationToken) =>
{
    if (request.ActorUserId == Guid.Empty || string.IsNullOrWhiteSpace(request.EventType))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["ack"] = ["ActorUserId and EventType are required."],
        });
    }

    try
    {
        await repository.RecordAcknowledgementAsync(shareId, request, cancellationToken);
        return Results.Accepted($"/api/v1/shares/{shareId}");
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("AcknowledgeShare");

app.Run();

static string? TryGetPresentedApiKey(HttpRequest request, string headerName)
{
    if (request.Headers.TryGetValue(headerName, out var headerValues))
    {
        string? headerKey = headerValues.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.Trim();
        }
    }

    string? authorizationHeader = request.Headers.Authorization.FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(authorizationHeader) && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return authorizationHeader[7..].Trim();
    }

    return null;
}

static bool AreKeysEqual(string expected, string actual)
{
    byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
    byte[] actualBytes = Encoding.UTF8.GetBytes(actual);

    return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
}
