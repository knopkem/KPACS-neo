using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ImageboxBootstrap
{
    private readonly string _applicationRoot;

    public ImageboxBootstrap()
    {
        _applicationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KPACS.Viewer.Avalonia");
    }

    public ImageboxPaths EnsurePaths()
    {
        string imageboxRoot = Path.Combine(_applicationRoot, "Imagebox");
        string studiesDirectory = Path.Combine(imageboxRoot, "Studies");
        string databasePath = Path.Combine(imageboxRoot, "imagebox.db");

        Directory.CreateDirectory(_applicationRoot);
        Directory.CreateDirectory(imageboxRoot);
        Directory.CreateDirectory(studiesDirectory);

        return new ImageboxPaths
        {
            RootDirectory = imageboxRoot,
            DatabasePath = databasePath,
            StudiesDirectory = studiesDirectory,
        };
    }
}
