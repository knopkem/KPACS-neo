using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomStudyDeletionService
{
    private readonly ImageboxPaths _paths;
    private readonly ImageboxRepository _repository;

    public DicomStudyDeletionService(ImageboxPaths paths, ImageboxRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public async Task DeleteStudyAsync(StudyListItem study, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(study);

        string imageboxRoot = EnsureTrailingSeparator(Path.GetFullPath(_paths.RootDirectory));
        string? originalPath = NormalizeStudyPath(study.StoragePath);
        string? stagedPath = null;

        if (!string.IsNullOrWhiteSpace(originalPath) && Directory.Exists(originalPath))
        {
            if (!IsSubdirectoryOf(originalPath, imageboxRoot))
            {
                throw new InvalidOperationException("Study storage path is outside the K-PACS imagebox.");
            }

            string pendingRoot = Path.Combine(_paths.RootDirectory, ".delete-pending");
            Directory.CreateDirectory(pendingRoot);

            stagedPath = Path.Combine(
                pendingRoot,
                $"study_{study.StudyKey}_{Guid.NewGuid():N}");

            Directory.Move(originalPath, stagedPath);
        }

        try
        {
            int affectedRows = await _repository.DeleteStudyAsync(study.StudyKey, cancellationToken);
            if (affectedRows == 0)
            {
                throw new InvalidOperationException("Study was not found in the database.");
            }

            if (!string.IsNullOrWhiteSpace(stagedPath) && Directory.Exists(stagedPath))
            {
                Directory.Delete(stagedPath, true);
            }

            DeleteEmptyParentDirectories(originalPath, imageboxRoot);
            DeleteEmptyParentDirectories(Path.Combine(_paths.RootDirectory, ".delete-pending"), imageboxRoot);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(stagedPath) && Directory.Exists(stagedPath) && !string.IsNullOrWhiteSpace(originalPath))
            {
                Directory.Move(stagedPath, originalPath);
            }

            throw;
        }
    }

    private static string? NormalizeStudyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }

    private static bool IsSubdirectoryOf(string candidatePath, string rootPath)
    {
        string normalizedCandidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));
        return normalizedCandidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static void DeleteEmptyParentDirectories(string? startPath, string stopRoot)
    {
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
        {
            return;
        }

        string currentPath = Path.GetFullPath(startPath);
        while (currentPath.StartsWith(stopRoot, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(currentPath, stopRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(currentPath).Any())
            {
                break;
            }

            Directory.Delete(currentPath);
            string? parent = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            currentPath = Path.GetFullPath(parent);
        }
    }
}
