// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - StudyViewerWindow.RemoteGpu.cs
// Partial class handling Remote GPU connectivity — connecting to a K-PACS
// Render Server, browsing remote studies, and binding panels with
// RemoteRenderBackend instances.
//
// The user clicks the "Remote GPU" workspace dock button to open a connection
// dialog, selects a series on the server, and the active panel switches to
// remote rendering. Disconnecting returns to local rendering.
// ------------------------------------------------------------------------------------------------

using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    // ==============================================================================================
    //  Remote GPU state
    // ==============================================================================================

    /// <summary>Active gRPC channel to the render server, or null when not connected.</summary>
    private GrpcChannel? _remoteGpuChannel;

    /// <summary>Active session ID on the render server, or null when not connected.</summary>
    private string? _remoteGpuSessionId;

    /// <summary>Server capabilities discovered during connection.</summary>
    private ServerCapabilities? _remoteGpuCapabilities;

    /// <summary>The URL of the currently connected render server.</summary>
    private string? _remoteGpuServerUrl;

    /// <summary>
    /// The remote render backend that is currently bound to the active panel.
    /// Null when operating locally.
    /// </summary>
    private RemoteRenderBackend? _activeRemoteBackend;

    /// <summary>Whether the viewer is connected to a remote render server.</summary>
    private bool IsRemoteGpuConnected => _remoteGpuChannel is not null && _remoteGpuSessionId is not null;

    // ==============================================================================================
    //  Workspace dock button handler
    // ==============================================================================================

    private async void OnWorkspaceRemoteGpuClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();

        if (IsRemoteGpuConnected)
        {
            // Already connected — offer disconnect
            DisconnectRemoteGpu();
            return;
        }

        // Open connection dialog
        string lastUrl = _remoteGpuServerUrl ?? "https://localhost:5200";
        var dialog = new ConnectRenderServerWindow(lastUrl);

        var result = await dialog.ShowDialog<RenderServerConnectionResult?>(this);
        if (result is null)
        {
            return; // User cancelled
        }

        // Store connection
        _remoteGpuChannel = result.Channel;
        _remoteGpuSessionId = result.SessionId;
        _remoteGpuCapabilities = result.Capabilities;
        _remoteGpuServerUrl = result.ServerUrl;

        UpdateRemoteGpuButtonState();

        // Load the selected series on the remote server
        await BindActiveSlotWithRemoteSeriesAsync(result.SelectedSeriesKey);
    }

    // ==============================================================================================
    //  Remote binding
    // ==============================================================================================

    /// <summary>
    /// Creates a <see cref="RemoteRenderBackend"/> for the given series key and
    /// binds it to the currently active viewport slot.
    /// </summary>
    private async Task BindActiveSlotWithRemoteSeriesAsync(long seriesKey)
    {
        if (_activeSlot?.Panel is not DicomViewPanel panel)
        {
            ShowToast("No active viewport to bind.", ToastSeverity.Warning);
            return;
        }

        string serverUrl = _remoteGpuServerUrl ?? "https://localhost:5200";

        try
        {
            ShowToast("Loading volume on render server…", ToastSeverity.Info);

            var backend = await Task.Run(async () =>
                await RemoteRenderBackend.ConnectAsync(
                    serverUrl,
                    seriesKey,
                    Environment.MachineName,
                    CancellationToken.None));

            _activeRemoteBackend?.Dispose();
            _activeRemoteBackend = backend;

            // Sync the current windowing state to the remote backend
            backend.SetWindowing(panel.WindowCenter, panel.WindowWidth);
            backend.SetColorScheme(_selectedColorScheme);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                panel.BindVolumeWithBackend(
                    backend.Volume,
                    backend,
                    Rendering.SliceOrientation.Axial,
                    backend.Volume.SizeZ / 2);

                ShowToast($"Remote GPU active: {_remoteGpuCapabilities?.GpuDeviceName ?? "GPU"}", ToastSeverity.Info);
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Remote load failed: {ex.Message}", ToastSeverity.Error);
            DisconnectRemoteGpu();
        }
    }

    // ==============================================================================================
    //  Disconnect
    // ==============================================================================================

    private void DisconnectRemoteGpu()
    {
        _activeRemoteBackend?.Dispose();
        _activeRemoteBackend = null;

        if (_remoteGpuSessionId is not null && _remoteGpuChannel is not null)
        {
            try
            {
                var sessionClient = new SessionService.SessionServiceClient(_remoteGpuChannel);
                sessionClient.DestroySession(new DestroySessionRequest { SessionId = _remoteGpuSessionId });
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _remoteGpuChannel?.Dispose();
        _remoteGpuChannel = null;
        _remoteGpuSessionId = null;
        _remoteGpuCapabilities = null;

        UpdateRemoteGpuButtonState();

        // If there's an active panel bound with a remote backend, rebind with local
        if (_activeSlot?.Panel is DicomViewPanel panel && panel.IsRemoteRendering)
        {
            // The panel's volume is a proxy — we can't simply rebind locally without
            // the real volume. Show a message instead.
            panel.ClearImage();
            ShowToast("Remote GPU disconnected. Please reload a local study.", ToastSeverity.Info);
        }
        else
        {
            ShowToast("Remote GPU disconnected.", ToastSeverity.Info);
        }
    }

    // ==============================================================================================
    //  UI state
    // ==============================================================================================

    private void UpdateRemoteGpuButtonState()
    {
        if (IsRemoteGpuConnected)
        {
            string gpuName = _remoteGpuCapabilities?.GpuDeviceName ?? "GPU";
            // Truncate long GPU names
            if (gpuName.Length > 20)
            {
                gpuName = gpuName[..20] + "…";
            }

            WorkspaceRemoteGpuLabel.Text = $"🖧 {gpuName}";
        }
        else
        {
            WorkspaceRemoteGpuLabel.Text = "Remote GPU";
        }

        WorkspaceRemoteGpuButton.Background = IsRemoteGpuConnected
            ? new SolidColorBrush(Color.Parse("#FF2E7D32"))  // Green when connected
            : null;
    }
}
