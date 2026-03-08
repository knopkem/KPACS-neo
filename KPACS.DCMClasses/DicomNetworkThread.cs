// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomNetworkThread.cs
// Ported from DCMNetThreadClass.pas (TDCMNetThrdClass)
//
// Provides background DICOM network operations using async/await (replacing Delphi's TThread).
// Orchestrates multi-study retrieve, store, and move operations with progress tracking.
// ------------------------------------------------------------------------------------------------

using KPACS.DCMClasses.Models;

namespace KPACS.DCMClasses;

/// <summary>
/// Process types for network operations.
/// Ported from KPtypes.pas constants.
/// </summary>
public static class NetworkProcessType
{
    public const int Retrieve = 0;
    public const int StoreSCU = 1;
    public const int DicomPrint = 2;
    public const int StoreSCP = 3;
    public const int GetHistory = 4;
    public const int Move = 5;
    public const int Find = 6;
}

/// <summary>
/// Manages background DICOM network operations with progress reporting.
/// Replaces TDCMNetThrdClass (TThread) with async Task-based operations.
/// Ported from DCMNetThreadClass.pas.
/// </summary>
public class DicomNetworkThread
{
    private CancellationTokenSource? _cts;

    /// <summary>IP address of the remote server.</summary>
    public string IP { get; set; } = string.Empty;

    /// <summary>Port of the remote server.</summary>
    public int Port { get; set; } = 104;

    /// <summary>Local AE Title.</summary>
    public string LocalAET { get; set; } = string.Empty;

    /// <summary>Remote AE Title.</summary>
    public string RemoteAET { get; set; } = string.Empty;

    /// <summary>Destination AE Title for C-MOVE operations.</summary>
    public string DestinationAET { get; set; } = string.Empty;

    /// <summary>Database path for finding stored files.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>The process type to execute.</summary>
    public int ProcessType { get; set; }

    /// <summary>Study Instance UID for single-study operations.</summary>
    public string StudyInstanceUID { get; set; } = string.Empty;

    /// <summary>Array of studies to process.</summary>
    public StudyInfo[] StudyInfoArray { get; set; } = Array.Empty<StudyInfo>();

    /// <summary>File paths for direct C-STORE operations.</summary>
    public List<string> FileNames { get; set; } = new();

    /// <summary>Whether to perform series-level moves.</summary>
    public bool SeriesLevelMove { get; set; }

    /// <summary>Whether to skip C-FIND before C-MOVE.</summary>
    public bool NoFindBeforeMove { get; set; }

    /// <summary>Whether to send complete series in one association.</summary>
    public bool SendCompleteSeries { get; set; }

    /// <summary>Whether to always reload even if local.</summary>
    public bool AlwaysReload { get; set; }

    /// <summary>Preferred transfer syntax for C-STORE.</summary>
    public string PreferredTransferSyntax { get; set; } = string.Empty;

    /// <summary>Whether the operation was aborted.</summary>
    public bool Aborted { get; private set; }

    /// <summary>Total number of jobs.</summary>
    public int JobCount { get; private set; }

    /// <summary>Number of completed jobs.</summary>
    public int JobsDone { get; private set; }

    /// <summary>Whether the last operation succeeded.</summary>
    public bool Success { get; private set; }

    /// <summary>Time when the operation started.</summary>
    public DateTime StartTime { get; private set; }

    /// <summary>Current Study Instance UID being processed.</summary>
    public string CurrentStudyUID { get; private set; } = string.Empty;

    /// <summary>Current Series Instance UID being processed.</summary>
    public string CurrentSeriesUID { get; private set; } = string.Empty;

    /// <summary>Number of series in current study.</summary>
    public int CurrentSeriesCount { get; private set; }

    /// <summary>Event raised to report progress.</summary>
    public event Action<int, int>? OnProgress;

    /// <summary>Event raised for log messages.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Whether this is a retrieval thread (Retrieve or GetHistory/StoreSCP).
    /// </summary>
    public bool IsRetrieveThread =>
        ProcessType == NetworkProcessType.Retrieve ||
        ProcessType == NetworkProcessType.GetHistory ||
        ProcessType == NetworkProcessType.StoreSCP;

    /// <summary>
    /// Executes the configured network task asynchronously.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken externalToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        Aborted = false;
        Success = true;
        StartTime = DateTime.Now;
        CurrentSeriesCount = 0;

        try
        {
            await ProcessTaskAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Aborted = true;
            Log("Task aborted.");
        }
        catch (Exception ex)
        {
            Success = false;
            Log($"Task failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Aborts the current operation.
    /// </summary>
    public void Abort()
    {
        Aborted = true;
        _cts?.Cancel();
    }

    // ==============================================================================================
    // Task Processing
    // ==============================================================================================

    private async Task ProcessTaskAsync(CancellationToken ct)
    {
        var net = new DicomNetworkClient
        {
            IP = IP,
            Port = Port,
            LocalAET = LocalAET,
            RemoteAET = RemoteAET
        };

        // StoreSCP placeholder - just creates the thread context without action
        if (ProcessType == NetworkProcessType.StoreSCP)
            return;

        // GetHistory - single study move
        if (ProcessType == NetworkProcessType.GetHistory)
        {
            JobCount = 1;
            CurrentStudyUID = StudyInstanceUID;
            var dest = string.IsNullOrEmpty(DestinationAET) ? null : DestinationAET;
            Success = await net.MoveStudyAsync(StudyInstanceUID, dest, cancellationToken: ct);
            JobsDone = 1;
            return;
        }

        Log(">>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>");

        if (ProcessType == NetworkProcessType.StoreSCU)
        {
            await StoreFilesAsync(net, ct);
            return;
        }

        // Retrieve / Move operations
        await RetrieveOrMoveAsync(net, ct);

        if (Aborted)
            Log("Closed. Task aborted.");
        else if (Success)
            Log("Closed. Task processed successfully.");
        else
            Log("Closed. Task processed with errors.");

        Log("<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<");
    }

    private async Task StoreFilesAsync(DicomNetworkClient net, CancellationToken ct)
    {
        if (FileNames.Count > 0)
        {
            JobCount = FileNames.Count;
            JobsDone = 0;

            var progress = new Progress<(int completed, int total)>(p =>
            {
                JobsDone = p.completed;
                OnProgress?.Invoke(p.completed, p.total);
            });

            Success = await net.StoreSCUAsync(FileNames, progress, ct);
        }
        else
        {
            // Store from study info array (files on disk)
            await StoreStudiesToArchiveAsync(net, ct);
        }
    }

    private async Task StoreStudiesToArchiveAsync(DicomNetworkClient net, CancellationToken ct)
    {
        var allFiles = new List<string>();

        foreach (var study in StudyInfoArray)
        {
            if (ct.IsCancellationRequested) break;
            if (study.Checked != TriStateCheck.Checked) continue;

            var studyPath = Path.Combine(DatabasePath, study.StudyInstanceUid);
            if (Directory.Exists(studyPath))
            {
                var files = System.IO.Directory.GetFiles(studyPath, "*.dcm",
                    SearchOption.AllDirectories);
                allFiles.AddRange(files);
            }
        }

        if (allFiles.Count > 0)
        {
            JobCount = allFiles.Count;
            var progress = new Progress<(int completed, int total)>(p =>
            {
                JobsDone = p.completed;
                OnProgress?.Invoke(p.completed, p.total);
            });

            Success = await net.StoreSCUAsync(allFiles, progress, ct);
        }
    }

    private async Task RetrieveOrMoveAsync(DicomNetworkClient net, CancellationToken ct)
    {
        // First pass: count jobs
        int totalJobs = 0;
        foreach (var study in StudyInfoArray)
        {
            if (study.Checked == TriStateCheck.Checked)
                totalJobs++;
            else if (study.Checked == TriStateCheck.Mix)
                totalJobs += study.Series?.Count(s => s.Checked) ?? 0;
        }

        JobCount = totalJobs;
        JobsDone = 0;

        // Second pass: execute
        foreach (var study in StudyInfoArray)
        {
            if (ct.IsCancellationRequested) break;

            if (study.Checked == TriStateCheck.Checked)
            {
                CurrentStudyUID = study.StudyInstanceUid;

                if (string.IsNullOrWhiteSpace(CurrentStudyUID))
                {
                    Log("Error: empty StudyInstanceUID, skipping.");
                    Success = false;
                    continue;
                }

                // Configure server from study info
                net.IP = !string.IsNullOrEmpty(study.ServerIp) ? study.ServerIp : IP;
                net.Port = int.TryParse(study.ServerPort, out var p) ? p : Port;
                net.RemoteAET = !string.IsNullOrEmpty(study.ServerAet) ? study.ServerAet : RemoteAET;

                if (ProcessType == NetworkProcessType.Retrieve && !study.Local || AlwaysReload)
                {
                    if (SeriesLevelMove && study.Series != null)
                    {
                        CurrentSeriesCount = study.Series.Count;
                        foreach (var series in study.Series)
                        {
                            if (ct.IsCancellationRequested) break;
                            CurrentSeriesUID = series.SerInstUid;

                            var moveSuccess = await net.MoveSeriesAsync(
                                study.StudyInstanceUid, series.SerInstUid,
                                DestinationAET, cancellationToken: ct);

                            if (!moveSuccess)
                            {
                                Success = false;
                                Log($"Retrieve failed: {study.PatientName}, Series: {series.SeriesNumber}");
                            }

                            JobsDone++;
                            OnProgress?.Invoke(JobsDone, JobCount);
                        }
                    }
                    else
                    {
                        var moveSuccess = await net.MoveStudyAsync(
                            study.StudyInstanceUid, DestinationAET, cancellationToken: ct);

                        if (!moveSuccess)
                        {
                            Success = false;
                            Log($"Retrieve failed: {study.PatientName}");
                        }

                        JobsDone++;
                        OnProgress?.Invoke(JobsDone, JobCount);
                    }
                }
                else if (ProcessType == NetworkProcessType.Move)
                {
                    var moveSuccess = await net.MoveStudyAsync(
                        study.StudyInstanceUid, DestinationAET, cancellationToken: ct);

                    if (!moveSuccess)
                    {
                        Success = false;
                        Log($"Move failed: {study.PatientName}");
                    }

                    JobsDone++;
                    OnProgress?.Invoke(JobsDone, JobCount);
                }
            }
            else if (study.Checked == TriStateCheck.Mix &&
                     (ProcessType == NetworkProcessType.Retrieve ||
                      ProcessType == NetworkProcessType.Move))
            {
                // Mixed selection: move only checked series
                net.IP = !string.IsNullOrEmpty(study.ServerIp) ? study.ServerIp : IP;
                net.Port = int.TryParse(study.ServerPort, out var p) ? p : Port;
                net.RemoteAET = !string.IsNullOrEmpty(study.ServerAet) ? study.ServerAet : RemoteAET;

                if (study.Series != null)
                {
                    foreach (var series in study.Series.Where(s => s.Checked))
                    {
                        if (ct.IsCancellationRequested) break;
                        CurrentSeriesUID = series.SerInstUid;

                        var moveSuccess = await net.MoveSeriesAsync(
                            study.StudyInstanceUid, series.SerInstUid,
                            DestinationAET, cancellationToken: ct);

                        if (!moveSuccess)
                        {
                            Success = false;
                            Log($"Move failed: {study.PatientName}, Series: {series.SeriesNumber}");
                        }

                        JobsDone++;
                        OnProgress?.Invoke(JobsDone, JobCount);
                    }
                }
            }
        }
    }

    private void Log(string message)
    {
        OnLog?.Invoke(message);
        System.Diagnostics.Debug.WriteLine(message);
    }
}
