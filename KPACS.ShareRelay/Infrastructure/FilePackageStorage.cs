using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace KPACS.ShareRelay.Infrastructure;

public sealed class FilePackageStorage
{
    private readonly string _rootPath;

    public FilePackageStorage(IOptions<PackageStorageOptions> options)
    {
        _rootPath = options.Value.PackagesRoot;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredPackage> SaveAsync(Guid shareId, string fileName, Stream source, CancellationToken cancellationToken)
    {
        string safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? "package.bin"
            : Path.GetFileName(fileName);
        string shareFolder = Path.Combine(_rootPath, shareId.ToString("N"));
        Directory.CreateDirectory(shareFolder);

        string relativePath = Path.Combine(shareId.ToString("N"), $"{Guid.NewGuid():N}-{safeFileName}");
        string absolutePath = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using var target = File.Create(absolutePath);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 64];
        long totalBytes = 0;

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            sha256.AppendData(buffer, 0, bytesRead);
            totalBytes += bytesRead;
        }

        string digest = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        return new StoredPackage(relativePath.Replace('\\', '/'), safeFileName, totalBytes, digest, absolutePath);
    }

    public Task<StoredPackageReadHandle> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        string normalizedKey = storageKey.Replace('/', Path.DirectorySeparatorChar);
        string absolutePath = Path.GetFullPath(Path.Combine(_rootPath, normalizedKey));
        string normalizedRoot = Path.GetFullPath(_rootPath);
        if (!absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested package storage path is invalid.");
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The requested package does not exist.", absolutePath);
        }

        var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var info = new FileInfo(absolutePath);
        return Task.FromResult(new StoredPackageReadHandle(Path.GetFileName(absolutePath), info.Length, stream));
    }
}

public sealed record StoredPackage(string StorageKey, string FileName, long SizeBytes, string Sha256, string AbsolutePath);

public sealed record StoredPackageReadHandle(string FileName, long Length, Stream Stream);
