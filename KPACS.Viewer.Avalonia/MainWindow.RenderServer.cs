using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class MainWindow
{
    private string? _renderServerDatabaseUrl;
    private ServerCapabilities? _renderServerDatabaseCapabilities;
    private Dictionary<string, StudyDetails> _renderServerStudyDetails = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyDictionary<string, long>> _renderServerSeriesKeysByStudyInstanceUid = new(StringComparer.Ordinal);

    private bool IsRenderServerDatabaseConnected => !string.IsNullOrWhiteSpace(_renderServerDatabaseUrl);
    private bool IsRemoteOnlyDebugMode => _app.IsRemoteOnlyDebugMode;

    private async Task OpenRenderServerDatabaseConnectionAsync()
    {
        if (IsRenderServerDatabaseConnected)
        {
            await DisconnectRenderServerDatabaseAsync();
            return;
        }

        var window = new ConnectRenderServerEndpointWindow(GetPreferredRenderServerDatabaseUrl());
        RenderServerEndpointResult? result = await window.ShowDialog<RenderServerEndpointResult?>(this);
        if (result is null)
        {
            return;
        }

        _renderServerDatabaseUrl = result.ServerUrl;
        _renderServerDatabaseCapabilities = result.Capabilities;
        _renderServerStudyDetails.Clear();
        _renderServerSeriesKeysByStudyInstanceUid.Clear();

        UpdateModeUi();
        _ = RefreshDatabaseInfoPanelAsync();
        await PersistRenderServerSettingsAsync(result.ServerUrl, useRenderServerDatabase: true);
        await RefreshCurrentModeAsync($"Connected to render server {_renderServerDatabaseUrl}.", userInitiated: true);
        ShowToast($"Remote database connected: {_renderServerDatabaseCapabilities?.GpuDeviceName ?? _renderServerDatabaseUrl}", ToastSeverity.Success, TimeSpan.FromSeconds(6));
    }

    private async Task DisconnectRenderServerDatabaseAsync()
    {
        string preferredUrl = _renderServerDatabaseUrl ?? GetPreferredRenderServerDatabaseUrl();
        _renderServerDatabaseUrl = null;
        _renderServerDatabaseCapabilities = null;
        _renderServerStudyDetails.Clear();
        _renderServerSeriesKeysByStudyInstanceUid.Clear();
        UpdateModeUi();
        _ = RefreshDatabaseInfoPanelAsync();
        await PersistRenderServerSettingsAsync(preferredUrl, useRenderServerDatabase: false);

        if (_browserMode == BrowserMode.Database)
        {
            await RefreshCurrentModeAsync(
                IsRemoteOnlyDebugMode
                    ? "Remote database disconnected. Local imagebox fallback is disabled by debug mode."
                    : "Switched back to the local imagebox.",
                userInitiated: true);
        }

        ShowToast(
            IsRemoteOnlyDebugMode
                ? "Remote database disconnected. Local imagebox fallback remains disabled for debugging."
                : "Remote database disconnected. Local imagebox restored.",
            ToastSeverity.Info,
            TimeSpan.FromSeconds(5));
    }

    private async Task LoadRenderServerStudiesAsync(string? statusOverride)
    {
        if (!IsRenderServerDatabaseConnected || string.IsNullOrWhiteSpace(_renderServerDatabaseUrl))
        {
            await LoadDatabaseStudiesLocalAsync(statusOverride);
            return;
        }

        try
        {
            using GrpcChannel channel = RenderServerGrpcClientFactory.CreateChannel(_renderServerDatabaseUrl);
            var studyClient = new StudyBrowserService.StudyBrowserServiceClient(channel);
            StudySearchResponse response = await studyClient.SearchStudiesAsync(ToRenderServerSearchRequest(BuildQuery()));

            _allStudies = response.Studies.Select(MapRenderServerStudySummary).ToList();
            BuildPatientRows();
            ApplyPatientFilter();

            string remoteLabel = GetRenderServerLabel();
            DatabaseStatsText.Text = $"{_allStudies.Count} studies from remote render server {remoteLabel}.";
            StatusText.Text = statusOverride ?? (_allStudies.Count == 0
                ? $"Remote render server {remoteLabel} is connected, but no studies matched the current filter."
                : $"Loaded {_allStudies.Count} studies from remote render server {remoteLabel}.");
        }
        catch (Exception ex)
        {
            _allStudies = [];
            _studies.Clear();
            _patients.Clear();
            _seriesRows.Clear();
            DatabaseStatsText.Text = "Remote render server query failed.";
            StatusText.Text = $"Remote render server query failed: {ex.Message}";
            ShowToast($"Remote render server query failed: {ex.Message}", ToastSeverity.Error, TimeSpan.FromSeconds(8));
        }
    }

    private async Task LoadDatabaseStudiesLocalAsync(string? statusOverride)
    {
        if (IsRemoteOnlyDebugMode)
        {
            _allStudies = [];
            _studies.Clear();
            _patients.Clear();
            _seriesRows.Clear();
            DatabaseStatsText.Text = "Local imagebox disabled for remote-only debugging.";
            StatusText.Text = statusOverride ?? "Remote-only debug mode is active. Connect the render server to browse studies.";
            return;
        }

        _allStudies = await _app.Repository.SearchStudiesAsync(BuildQuery());
        BuildPatientRows();
        ApplyPatientFilter();

        DatabaseStatsText.Text = $"{_allStudies.Count} studies indexed in SQLite.";
        StatusText.Text = statusOverride ?? (_allStudies.Count == 0
            ? "K-PACS imagebox ready — switch to Filesystem mode to scan media before importing."
            : $"Loaded {_allStudies.Count} studies from the K-PACS imagebox and filesystem index.");
    }

    private async Task<StudyDetails?> LoadRenderServerStudyDetailsAsync(StudyListItem selectedStudy)
    {
        if (string.IsNullOrWhiteSpace(_renderServerDatabaseUrl))
        {
            return null;
        }

        if (_renderServerStudyDetails.TryGetValue(selectedStudy.StudyInstanceUid, out StudyDetails? cachedDetails))
        {
            return cachedDetails;
        }

        using GrpcChannel channel = RenderServerGrpcClientFactory.CreateChannel(_renderServerDatabaseUrl);
        var studyClient = new StudyBrowserService.StudyBrowserServiceClient(channel);
        StudyDetailsResponse response = await studyClient.GetStudyDetailsAsync(new GetStudyDetailsRequest
        {
            StudyKey = selectedStudy.StudyKey,
        });

        StudyDetails details = MapRenderServerStudyDetails(response, out IReadOnlyDictionary<string, long> seriesKeysByUid);
        _renderServerStudyDetails[selectedStudy.StudyInstanceUid] = details;
        _renderServerSeriesKeysByStudyInstanceUid[selectedStudy.StudyInstanceUid] = seriesKeysByUid;
        return details;
    }

    private async Task<List<StudyListItem>> SearchRenderServerStudiesAsync(StudyQuery query)
    {
        if (string.IsNullOrWhiteSpace(_renderServerDatabaseUrl))
        {
            return [];
        }

        using GrpcChannel channel = RenderServerGrpcClientFactory.CreateChannel(_renderServerDatabaseUrl);
        var studyClient = new StudyBrowserService.StudyBrowserServiceClient(channel);
        StudySearchResponse response = await studyClient.SearchStudiesAsync(ToRenderServerSearchRequest(query));
        return response.Studies.Select(MapRenderServerStudySummary).ToList();
    }

    private string GetRenderServerLabel()
    {
        if (!string.IsNullOrWhiteSpace(_renderServerDatabaseCapabilities?.GpuDeviceName))
        {
            return _renderServerDatabaseCapabilities.GpuDeviceName;
        }

        return _renderServerDatabaseUrl ?? "remote server";
    }

    private string GetPreferredRenderServerDatabaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_renderServerDatabaseUrl))
        {
            return _renderServerDatabaseUrl;
        }

        string configuredUrl = _app.NetworkSettingsService.CurrentSettings.RenderServerDatabaseUrl;
        return string.IsNullOrWhiteSpace(configuredUrl) ? "http://localhost:5200" : configuredUrl;
    }

    private async Task RestoreRenderServerDatabaseConnectionAsync()
    {
        DicomNetworkSettings settings = _app.NetworkSettingsService.CurrentSettings;
        if (!settings.UseRenderServerDatabase || _browserMode != BrowserMode.Database || string.IsNullOrWhiteSpace(settings.RenderServerDatabaseUrl))
        {
            return;
        }

        try
        {
            _renderServerDatabaseCapabilities = await ProbeRenderServerCapabilitiesAsync(settings.RenderServerDatabaseUrl);
            _renderServerDatabaseUrl = settings.RenderServerDatabaseUrl.Trim();
            _renderServerStudyDetails.Clear();
            _renderServerSeriesKeysByStudyInstanceUid.Clear();
            UpdateModeUi();
            _ = RefreshDatabaseInfoPanelAsync();
            StatusText.Text = $"Reconnected to render server {_renderServerDatabaseUrl}.";
        }
        catch (Exception ex)
        {
            _renderServerDatabaseUrl = null;
            _renderServerDatabaseCapabilities = null;
            _renderServerStudyDetails.Clear();
            _renderServerSeriesKeysByStudyInstanceUid.Clear();
            UpdateModeUi();
            _ = RefreshDatabaseInfoPanelAsync();
            StatusText.Text = IsRemoteOnlyDebugMode
                ? $"Saved render server {settings.RenderServerDatabaseUrl} is unavailable. Local imagebox fallback is disabled by debug mode."
                : $"Saved render server {settings.RenderServerDatabaseUrl} is unavailable. Using the local imagebox.";
            ShowToast(
                IsRemoteOnlyDebugMode
                    ? $"Could not reconnect to saved render server: {ex.Message}. Local fallback is disabled."
                    : $"Could not reconnect to saved render server: {ex.Message}",
                ToastSeverity.Warning,
                TimeSpan.FromSeconds(7));
        }
    }

    private async Task PersistRenderServerSettingsAsync(string? serverUrl, bool useRenderServerDatabase)
    {
        DicomNetworkSettings updated = _app.NetworkSettingsService.CurrentSettings.Clone();
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            updated.RenderServerDatabaseUrl = serverUrl.Trim();
        }

        updated.UseRenderServerDatabase = useRenderServerDatabase;
        await _app.NetworkSettingsService.SaveAsync(updated);
    }

    private static async Task<ServerCapabilities?> ProbeRenderServerCapabilitiesAsync(string serverUrl)
    {
        using GrpcChannel channel = RenderServerGrpcClientFactory.CreateChannel(serverUrl);
        var sessionClient = new SessionService.SessionServiceClient(channel);
        CreateSessionResponse response = await sessionClient.CreateSessionAsync(new CreateSessionRequest
        {
            ClientName = Environment.MachineName,
            MaxViewports = 1,
        });

        try
        {
            await sessionClient.DestroySessionAsync(new DestroySessionRequest { SessionId = response.SessionId });
        }
        catch
        {
        }

        return response.Capabilities;
    }

    private static StudySearchRequest ToRenderServerSearchRequest(StudyQuery query)
    {
        var request = new StudySearchRequest
        {
            PatientId = query.PatientId ?? string.Empty,
            PatientName = query.PatientName ?? string.Empty,
            AccessionNumber = query.AccessionNumber ?? string.Empty,
            StudyDescription = query.StudyDescription ?? string.Empty,
            ReferringPhysician = query.ReferringPhysician ?? string.Empty,
            QuickSearch = query.QuickSearch ?? string.Empty,
            FromStudyDate = query.FromStudyDate?.ToString("yyyyMMdd") ?? string.Empty,
            ToStudyDate = query.ToStudyDate?.ToString("yyyyMMdd") ?? string.Empty,
            MaxResults = 2000,
        };

        request.Modalities.AddRange(query.Modalities ?? []);
        return request;
    }

    private static StudyListItem MapRenderServerStudySummary(StudySummary summary)
    {
        return new StudyListItem
        {
            StudyKey = summary.StudyKey,
            StudyInstanceUid = summary.StudyInstanceUid,
            PatientName = summary.PatientName,
            PatientId = summary.PatientId,
            PatientBirthDate = summary.PatientBirthDate,
            AccessionNumber = summary.AccessionNumber,
            StudyDescription = summary.StudyDescription,
            ReferringPhysician = summary.ReferringPhysician,
            StudyDate = summary.StudyDate,
            Modalities = summary.Modalities,
            SeriesCount = summary.SeriesCount,
            InstanceCount = summary.InstanceCount,
            StoragePath = string.Empty,
            SourcePath = string.Empty,
            Availability = StudyAvailability.Imported,
        };
    }

    private static StudyDetails MapRenderServerStudyDetails(StudyDetailsResponse response, out IReadOnlyDictionary<string, long> seriesKeysByUid)
    {
        StudyDetails details = new()
        {
            Study = MapRenderServerStudySummary(response.Study),
        };

        Dictionary<string, long> map = new(StringComparer.Ordinal);
        foreach (SeriesSummary seriesSummary in response.Series.OrderBy(series => series.SeriesNumber).ThenBy(series => series.SeriesDescription))
        {
            var series = new SeriesRecord
            {
                SeriesKey = seriesSummary.SeriesKey,
                StudyKey = response.Study.StudyKey,
                SeriesInstanceUid = seriesSummary.SeriesInstanceUid,
                Modality = seriesSummary.Modality,
                BodyPart = seriesSummary.BodyPart,
                SeriesDescription = seriesSummary.SeriesDescription,
                SeriesNumber = seriesSummary.SeriesNumber,
                InstanceCount = seriesSummary.InstanceCount,
            };

            for (int index = 0; index < Math.Max(0, seriesSummary.InstanceCount); index++)
            {
                series.Instances.Add(new InstanceRecord
                {
                    SeriesKey = seriesSummary.SeriesKey,
                    SopInstanceUid = $"{seriesSummary.SeriesInstanceUid}.{index + 1}",
                    SopClassUid = string.Empty,
                    FilePath = string.Empty,
                    SourceFilePath = string.Empty,
                    InstanceNumber = index + 1,
                    FrameCount = 1,
                });
            }

            details.Series.Add(series);
            map[series.SeriesInstanceUid] = series.SeriesKey;
        }

        seriesKeysByUid = map;
        return details;
    }
}
