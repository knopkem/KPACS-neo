using FellowOakDicom;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomPseudonymizationService
{
    private readonly ImageboxRepository _repository;

    public DicomPseudonymizationService(ImageboxRepository repository)
    {
        _repository = repository;
    }

    public async Task<int> PseudonymizeStudyAsync(long studyKey, PseudonymizeRequest request, CancellationToken cancellationToken = default)
    {
        StudyDetails? details = await _repository.GetStudyDetailsAsync(studyKey, cancellationToken);
        if (details is null)
        {
            return 0;
        }

        int changedFiles = 0;
        foreach (SeriesRecord series in details.Series)
        {
            foreach (InstanceRecord instance in series.Instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dicomFile = DicomFile.Open(instance.FilePath, FellowOakDicom.FileReadOption.ReadAll);
                var dataset = dicomFile.Dataset;
                dataset.AddOrUpdate(DicomTag.PatientName, request.PatientName ?? string.Empty);
                dataset.AddOrUpdate(DicomTag.PatientID, request.PatientId ?? string.Empty);
                dataset.AddOrUpdate(DicomTag.AccessionNumber, request.AccessionNumber ?? string.Empty);
                dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, request.ReferringPhysician ?? string.Empty);
                dataset.AddOrUpdate(DicomTag.PatientAddress, string.Empty);
                dataset.AddOrUpdate(DicomTag.PatientTelephoneNumbers, string.Empty);
                dataset.AddOrUpdate(DicomTag.PatientBirthDate, request.PatientBirthDate ?? string.Empty);
                dicomFile.Save(instance.FilePath);
                changedFiles++;
            }
        }

        await _repository.UpdateStudyAfterPseudonymizeAsync(studyKey, request, cancellationToken);
        return changedFiles;
    }
}
