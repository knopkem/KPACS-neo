using Avalonia;
using OpenCL.Net;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace KPACS.Viewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        RegisterOpenClNativeLibraryResolver();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Maps the Windows-style "opencl.dll" DllImport name used by OpenCL.Net to the
    /// correct native library name on each platform, because on Linux/macOS the
    /// library is named libOpenCL.so / OpenCL.framework — not opencl.dll.
    /// </summary>
    private static void RegisterOpenClNativeLibraryResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(Cl).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (!libraryName.Equals("opencl.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero; // fall through to default resolution

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return NativeLibrary.Load("OpenCL.dll", assembly, searchPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return NativeLibrary.Load("/System/Library/Frameworks/OpenCL.framework/OpenCL", assembly, searchPath);

            // Linux and other Unix-like systems
            foreach (string candidate in (string[])["libOpenCL.so.1", "libOpenCL.so"])
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                    return handle;
            }

            return IntPtr.Zero;
        });
    }
}
