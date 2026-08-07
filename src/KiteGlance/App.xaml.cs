using System.Threading;
using System.Windows;
using System.Windows.Threading;
using KiteGlance.Services;

namespace KiteGlance;

public partial class App : System.Windows.Application
{
    // Named mutex, not a process scan: this is race-free and survives
    // the exe being launched from two different paths.
    private const string InstanceKey = "KiteGlance.SingleInstance.v1";

    private Mutex? _instance;
    private MainWindow? _widget;
    private WidgetManager? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort crash reporting: anything that escapes the UI thread or a
        // background task lands in the log file the user can send us, instead
        // of vanishing. DispatcherUnhandledException is marked handled so a
        // single bad refresh does not take the whole widget down.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _instance = new Mutex(initiallyOwned: true, InstanceKey, out var isFirst);

        if (!isFirst)
        {
            // Already running. Don't stack a second widget on the desktop;
            // just leave quietly. The tray icon is the way back in.
            Log.Info("Second instance blocked; exiting");
            Shutdown();
            return;
        }

        Log.Info("Startup");

        // Before the first window is built, so it opens already painted in the
        // right palette rather than flashing dark and correcting itself.
        Theme.Apply(State.WidgetState.Load().Theme);

        _widget = new MainWindow();
        _manager = new WidgetManager(_widget);

        _widget.Show();
        _ = _widget.BootAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Shutdown");
        _manager?.Dispose();

        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch { /* not owned */ }
            _instance.Dispose();
        }

        base.OnExit(e);
    }
}
