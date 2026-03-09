using System.Globalization;
using Avalonia;
using FellowOakDicom;
using KPACS.DCMClasses;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const string SecondaryCaptureSeriesDescription = "Secondary Capture Sequence";
    private readonly Dictionary<string, SecondaryCaptureLinkInfo> _secondaryCaptureLinksBySourceSop = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SecondaryCaptureLinkInfo> _secondaryCaptureLinksByTargetSop = new(StringComparer.OrdinalIgnoreCase);
    private bool _secondaryCaptureBusy;

    private void InitializeSecondaryCaptureUi()
    {
        _ = LoadSecondaryCaptureLinksAsync();
    }

    private async Task LoadSecondaryCaptureLinksAsync()
    {
        _secondaryCaptureLinksBySourceSop.Clear();
        _secondaryCaptureLinksByTargetSop.Clear();

        foreach (SeriesRecord series in _context.StudyDetails.Series)
        {
            foreach (InstanceRecord instance in series.Instances)
            {
                if (!IsSecondaryCaptureSeries(series, instance))
                {
                    continue;
                }

                try
                {
                    DicomDataset dataset = DicomFile.Open(instance.FilePath, FellowOakDicom.FileReadOption.ReadAll).Dataset;
                    if (!TryGetSourceSopInstanceUid(dataset, out string sourceSopInstanceUid))
                    {
                        continue;
                    }

                    var link = new SecondaryCaptureLinkInfo(
                        sourceSopInstanceUid,
                        instance.SopInstanceUid,
                        series.SeriesInstanceUid,
                        instance.FilePath);

                    _secondaryCaptureLinksBySourceSop[sourceSopInstanceUid] = link;
                    _secondaryCaptureLinksByTargetSop[instance.SopInstanceUid] = link;
                }
                catch
                {
                }
            }
        }

        RefreshSecondaryCaptureIndicators();
    }

    private void ConfigureSecondaryCapturePanel(ViewportSlot slot, DicomViewPanel panel)
    {
        panel.SecondaryCaptureToggleRequested += () => OnSecondaryCaptureToggleRequested(slot);
        UpdateSecondaryCaptureIndicator(slot);
    }

    private void RefreshSecondaryCaptureIndicators()
    {
        foreach (ViewportSlot slot in _slots)
        {
            UpdateSecondaryCaptureIndicator(slot);
        }
    }

    private void UpdateSecondaryCaptureIndicator(ViewportSlot slot)
    {
        InstanceRecord? instance = GetCurrentInstance(slot);
        bool isVisible = instance is not null && slot.Series is not null && CanToggleSecondaryCapture(slot.Series, instance);
        bool hasCapture = instance is not null && _secondaryCaptureLinksBySourceSop.ContainsKey(instance.SopInstanceUid);
        slot.Panel.SetSecondaryCaptureState(isVisible, hasCapture, !_secondaryCaptureBusy);
    }

    private async void OnSecondaryCaptureToggleRequested(ViewportSlot slot)
    {
        if (_secondaryCaptureBusy || slot.Series is null)
        {
            return;
        }

        InstanceRecord? sourceInstance = GetCurrentInstance(slot);
        if (sourceInstance is null || !CanToggleSecondaryCapture(slot.Series, sourceInstance))
        {
            return;
        }

        _secondaryCaptureBusy = true;
        RefreshSecondaryCaptureIndicators();

        try
        {
            if (_secondaryCaptureLinksBySourceSop.TryGetValue(sourceInstance.SopInstanceUid, out SecondaryCaptureLinkInfo? existing))
            {
                await DeleteSecondaryCaptureAsync(existing);
            }
            else
            {
                await CreateSecondaryCaptureAsync(slot, slot.Series, sourceInstance);
            }
        }
        catch (Exception ex)
        {
            ViewerStatusText.Text = $"Secondary capture failed: {ex.Message}";
        }
        finally
        {
            _secondaryCaptureBusy = false;
            RefreshSecondaryCaptureIndicators();
            UpdateStatus();
        }
    }

    private async Task CreateSecondaryCaptureAsync(ViewportSlot slot, SeriesRecord sourceSeries, InstanceRecord sourceInstance)
    {
        DicomViewPanel.SecondaryCaptureSnapshot? snapshot = slot.Panel.CaptureSecondaryCaptureSnapshot();
        if (snapshot is null)
        {
            throw new InvalidOperationException("No rendered image is available for Secondary Capture creation.");
        }

        App app = GetViewerApp();
        SeriesRecord secondaryCaptureSeries = await EnsureSecondaryCaptureSeriesAsync(app);
        int instanceNumber = secondaryCaptureSeries.Instances.Count == 0
            ? 1
            : secondaryCaptureSeries.Instances.Max(item => item.InstanceNumber) + 1;

        string sopInstanceUid = DicomFunctions.CreateUniqueUid();
        string outputDirectory = Path.Combine(_context.StudyDetails.Study.StoragePath, SanitizePathComponent(secondaryCaptureSeries.SeriesInstanceUid));
        Directory.CreateDirectory(outputDirectory);
        string outputFile = Path.Combine(outputDirectory, SanitizePathComponent(sopInstanceUid) + ".dcm");

        DicomDataset sourceDataset = DicomFile.Open(sourceInstance.FilePath, FellowOakDicom.FileReadOption.ReadAll).Dataset;
        string sourceSopClassUid = sourceDataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, sourceInstance.SopClassUid);
        string sourceCharacterSet = sourceDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 100");
        var studyInfo = _context.StudyDetails.LegacyStudy ?? _context.StudyDetails.ToLegacyStudyInfo();

        using (var secondaryCapture = new DicomSecondaryCapture())
        {
            secondaryCapture.LoadPixelData(snapshot.RgbPixels, snapshot.Height, snapshot.Width, SecondaryCaptureBitDepth.Bit24);
            secondaryCapture.Dataset.AddTag(DicomTag.SpecificCharacterSet, string.IsNullOrWhiteSpace(sourceCharacterSet) ? "ISO_IR 100" : sourceCharacterSet);
            secondaryCapture.Dataset.AddTag(DicomTag.PatientName, DicomFunctions.PersonNameVTCompatible(studyInfo.PatientName));
            secondaryCapture.Dataset.AddTag(DicomTag.PatientID, studyInfo.PatientId);
            secondaryCapture.Dataset.AddTag(DicomTag.PatientBirthDate, studyInfo.PatientBD);
            secondaryCapture.Dataset.AddTag(DicomTag.PatientSex, studyInfo.PatientSex);
            secondaryCapture.Dataset.AddTag(DicomTag.StudyDate, studyInfo.StudyDate);
            secondaryCapture.Dataset.AddTag(DicomTag.StudyTime, studyInfo.StudyTime);
            secondaryCapture.Dataset.AddTag(DicomTag.StudyID, studyInfo.StudyId);
            secondaryCapture.Dataset.AddTag(DicomTag.StudyDescription, studyInfo.StudyDescription);
            secondaryCapture.Dataset.AddTag(DicomTag.StudyInstanceUID, studyInfo.StudyInstanceUid);
            secondaryCapture.Dataset.AddTag(DicomTag.InstitutionName, studyInfo.InstitutionName);
            secondaryCapture.Dataset.AddTag(DicomTag.ReferringPhysicianName, DicomFunctions.PersonNameVTCompatible(studyInfo.PhysiciansName));
            secondaryCapture.Dataset.AddTag(DicomTag.AccessionNumber, studyInfo.AccessionNumber);
            secondaryCapture.Dataset.AddTag(DicomTag.SeriesInstanceUID, secondaryCaptureSeries.SeriesInstanceUid);
            secondaryCapture.Dataset.AddTag(DicomTag.SeriesNumber, secondaryCaptureSeries.SeriesNumber.ToString(CultureInfo.InvariantCulture));
            secondaryCapture.Dataset.AddTag(DicomTag.SOPInstanceUID, sopInstanceUid);
            secondaryCapture.Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, sopInstanceUid);
            secondaryCapture.Dataset.AddTag(DicomTag.InstanceNumber, instanceNumber.ToString(CultureInfo.InvariantCulture));
            secondaryCapture.Dataset.AddTag(DicomTag.SeriesDescription, SecondaryCaptureSeriesDescription);
            secondaryCapture.Dataset.AddTag(DicomTag.ImageType, @"DERIVED\SECONDARY\KEY IMAGE");
            secondaryCapture.Dataset.AddTag(DicomTag.BurnedInAnnotation, "YES");
            secondaryCapture.Dataset.AddTag(DicomTag.InstanceCreationDate, DicomFunctions.DateToDcmDate(DateTime.Now));
            secondaryCapture.Dataset.AddTag(DicomTag.InstanceCreationTime, DicomFunctions.TimeToDcmTime(DateTime.Now));
            secondaryCapture.Dataset.AddTag(DicomTag.ContentDate, DicomFunctions.DateToDcmDate(DateTime.Now));
            secondaryCapture.Dataset.AddTag(DicomTag.ContentTime, DicomFunctions.TimeToDcmTime(DateTime.Now));

            var sourceReference = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, sourceSopClassUid },
                { DicomTag.ReferencedSOPInstanceUID, sourceInstance.SopInstanceUid },
            };

            secondaryCapture.Dataset.Dataset.AddOrUpdate(new DicomSequence(DicomTag.SourceImageSequence, sourceReference));
            secondaryCapture.Dataset.Dataset.AddOrUpdate(new DicomSequence(new DicomTag(0x0008, 0x1140), sourceReference.Clone()));
            secondaryCapture.SaveSCObject(outputFile);
        }

        var secondaryCaptureInstance = new InstanceRecord
        {
            SeriesKey = secondaryCaptureSeries.SeriesKey,
            SopInstanceUid = sopInstanceUid,
            SopClassUid = DicomTagConstants.UID_SecondaryCaptureImageStorage,
            FilePath = outputFile,
            InstanceNumber = instanceNumber,
            FrameCount = 1,
        };

        secondaryCaptureSeries.Instances.Add(secondaryCaptureInstance);
        secondaryCaptureSeries.InstanceCount = secondaryCaptureSeries.Instances.Count;
        await app.Repository.UpsertSeriesAsync(_context.StudyDetails.Study.StudyKey, secondaryCaptureSeries);
        await app.Repository.UpsertInstanceAsync(secondaryCaptureSeries.SeriesKey, secondaryCaptureInstance);

        SecondaryCaptureLinkInfo link = new(sourceInstance.SopInstanceUid, sopInstanceUid, secondaryCaptureSeries.SeriesInstanceUid, outputFile);
        _secondaryCaptureLinksBySourceSop[sourceInstance.SopInstanceUid] = link;
        _secondaryCaptureLinksByTargetSop[sopInstanceUid] = link;

        _context.StudyDetails.PopulateLegacyStudyInfo();
        RefreshThumbnailStrip(_activeSlot?.Series);
        RefreshSecondaryCaptureIndicators();
    }

    private async Task DeleteSecondaryCaptureAsync(SecondaryCaptureLinkInfo link)
    {
        App app = GetViewerApp();
        SeriesRecord? targetSeries = _context.StudyDetails.Series.FirstOrDefault(series =>
            string.Equals(series.SeriesInstanceUid, link.TargetSeriesInstanceUid, StringComparison.OrdinalIgnoreCase));
        InstanceRecord? targetInstance = targetSeries?.Instances.FirstOrDefault(instance =>
            string.Equals(instance.SopInstanceUid, link.TargetSopInstanceUid, StringComparison.OrdinalIgnoreCase));

        await app.Repository.DeleteInstanceBySopInstanceUidAsync(link.TargetSopInstanceUid);

        if (targetSeries is not null && targetInstance is not null)
        {
            targetSeries.Instances.Remove(targetInstance);
            targetSeries.InstanceCount = targetSeries.Instances.Count;

            if (File.Exists(targetInstance.FilePath))
            {
                File.Delete(targetInstance.FilePath);
            }

            if (targetSeries.Instances.Count == 0)
            {
                _context.StudyDetails.Series.Remove(targetSeries);
                await app.Repository.DeleteSeriesIfEmptyAsync(targetSeries.SeriesInstanceUid);

                string? directory = Path.GetDirectoryName(targetInstance.FilePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            else
            {
                await app.Repository.UpsertSeriesAsync(_context.StudyDetails.Study.StudyKey, targetSeries);
            }

            RefreshSlotsForSeriesMutation(targetSeries);
        }

        _secondaryCaptureLinksBySourceSop.Remove(link.SourceSopInstanceUid);
        _secondaryCaptureLinksByTargetSop.Remove(link.TargetSopInstanceUid);
        _context.StudyDetails.PopulateLegacyStudyInfo();
        RefreshThumbnailStrip(_activeSlot?.Series);
        RefreshSecondaryCaptureIndicators();
    }

    private async Task<SeriesRecord> EnsureSecondaryCaptureSeriesAsync(App app)
    {
        SeriesRecord? existingSeries = _context.StudyDetails.Series.FirstOrDefault(series =>
            string.Equals(series.Modality, "OT", StringComparison.OrdinalIgnoreCase)
            && string.Equals(series.SeriesDescription, SecondaryCaptureSeriesDescription, StringComparison.OrdinalIgnoreCase));

        if (existingSeries is not null)
        {
            return existingSeries;
        }

        int nextSeriesNumber = _context.StudyDetails.Series.Count == 0
            ? 1
            : _context.StudyDetails.Series.Max(series => series.SeriesNumber) + 1;

        var newSeries = new SeriesRecord
        {
            StudyKey = _context.StudyDetails.Study.StudyKey,
            SeriesInstanceUid = DicomFunctions.CreateUniqueUid(),
            Modality = "OT",
            SeriesDescription = SecondaryCaptureSeriesDescription,
            SeriesNumber = nextSeriesNumber,
            InstanceCount = 0,
        };

        long seriesKey = await app.Repository.UpsertSeriesAsync(_context.StudyDetails.Study.StudyKey, newSeries);
        var persistedSeries = new SeriesRecord
        {
            SeriesKey = seriesKey,
            StudyKey = newSeries.StudyKey,
            SeriesInstanceUid = newSeries.SeriesInstanceUid,
            Modality = newSeries.Modality,
            SeriesDescription = newSeries.SeriesDescription,
            SeriesNumber = newSeries.SeriesNumber,
            InstanceCount = 0,
        };
        _context.StudyDetails.Series.Add(persistedSeries);
        _context.StudyDetails.PopulateLegacyStudyInfo();
        return persistedSeries;
    }

    private void RefreshSlotsForSeriesMutation(SeriesRecord affectedSeries)
    {
        foreach (ViewportSlot slot in _slots.Where(slot => slot.Series is not null && string.Equals(slot.Series.SeriesInstanceUid, affectedSeries.SeriesInstanceUid, StringComparison.OrdinalIgnoreCase)))
        {
            if (affectedSeries.Instances.Count == 0)
            {
                slot.Series = null;
                slot.InstanceIndex = 0;
                slot.ViewState = null;
                slot.Panel.ClearImage();
                continue;
            }

            slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, affectedSeries.Instances.Count - 1);
            LoadSlot(slot);
        }

        if (_activeSlot?.Series is not null && !_context.StudyDetails.Series.Contains(_activeSlot.Series))
        {
            SetActiveSlot(_slots.FirstOrDefault(slot => slot.Series is not null));
        }
    }

    private InstanceRecord? GetCurrentInstance(ViewportSlot slot)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return null;
        }

        int index = Math.Clamp(slot.InstanceIndex, 0, slot.Series.Instances.Count - 1);
        return slot.Series.Instances[index];
    }

    private static bool TryGetSourceSopInstanceUid(DicomDataset dataset, out string sourceSopInstanceUid)
    {
        sourceSopInstanceUid = string.Empty;

        foreach (DicomTag tag in new[] { DicomTag.SourceImageSequence, new DicomTag(0x0008, 0x1140) })
        {
            if (!dataset.Contains(tag))
            {
                continue;
            }

            DicomSequence sequence = dataset.GetSequence(tag);
            DicomDataset? item = sequence.Items.FirstOrDefault();
            if (item is null)
            {
                continue;
            }

            sourceSopInstanceUid = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            if (!string.IsNullOrWhiteSpace(sourceSopInstanceUid))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanToggleSecondaryCapture(SeriesRecord series, InstanceRecord instance)
    {
        if (IsSecondaryCaptureSeries(series, instance))
        {
            return false;
        }

        return !_secondaryCaptureLinksByTargetSop.ContainsKey(instance.SopInstanceUid);
    }

    private static bool IsSecondaryCaptureSeries(SeriesRecord series, InstanceRecord instance)
    {
        return string.Equals(instance.SopClassUid, DicomTagConstants.UID_SecondaryCaptureImageStorage, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(series.Modality, "OT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(series.SeriesDescription, SecondaryCaptureSeriesDescription, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizePathComponent(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static App GetViewerApp() =>
        Application.Current as App ?? throw new InvalidOperationException("Viewer services are not available.");

    private sealed record SecondaryCaptureLinkInfo(string SourceSopInstanceUid, string TargetSopInstanceUid, string TargetSeriesInstanceUid, string TargetFilePath);
}