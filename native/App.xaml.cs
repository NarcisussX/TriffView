using System.Windows;
using System.Threading;

namespace TriffView;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (IsTriffHudRunning())
        {
            System.Windows.MessageBox.Show(
                "TriffHUD is already running. Quit TriffHUD before starting standalone TriffView so both apps do not compete for previews, hotkeys, and shared settings.",
                "TriffView",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "TriffView.Standalone.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "TriffView is already running.",
                "TriffView",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown();
            return;
        }

        var window = new MainWindow(e.Args);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        base.OnExit(e);
    }

    internal static bool IsTriffHudRunning()
    {
        return System.Diagnostics.Process.GetProcessesByName("TriffHud")
            .Any(process =>
            {
                try
                {
                    return process.Id != Environment.ProcessId;
                }
                catch
                {
                    return false;
                }
            });
    }
}
