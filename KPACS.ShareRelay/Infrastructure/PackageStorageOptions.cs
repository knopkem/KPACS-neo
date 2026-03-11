namespace KPACS.ShareRelay.Infrastructure;

public sealed class PackageStorageOptions
{
    public string PackagesRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "App_Data", "packages");
}
