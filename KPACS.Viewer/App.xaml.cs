using System.Windows;
using FellowOakDicom;

namespace KPACS.Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new DicomSetupBuilder().Build();
    }
}
