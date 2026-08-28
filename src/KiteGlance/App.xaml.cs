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
    private bool _ownsMutex;
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
        _ownsMutex = isFirst;

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

        // The widget's Closing is cancelled, so its Closed never fires. Reach
        // into MainWindow directly to release the SystemParameters and
        // SystemEvents subscriptions and stop its timers, all of which would
        // otherwise live for the lifetime of the process.
        KiteGlance.MainWindow.ShutdownAll();

        if (_instance is not null)
        {
            // Only the first instance ever owned the mutex -- `initiallyOwned:
            // true` does not give ownership to the opener of an existing named
            // mutex, even briefly. Releasing one we did not own throws
            // ApplicationException, which we treat as harmless.
            if (_ownsMutex)
            {
                try { _instance.ReleaseMutex(); }
                catch { /* race against another releaser */ }
            }
            _instance.Dispose();
        }

        base.OnExit(e);
    }
}
