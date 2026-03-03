using System.Windows;
using System.Windows.Threading;

namespace ArchiveAutomator;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global exception safety net ────────────────────────────────────
        // Without these handlers an unhandled exception silently terminates
        // the process. With them the user sees a clear error dialog and the
        // app stays alive so they can save their work / check the log.

        // UI-thread exceptions (includes exceptions from async-void / Command delegates)
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Background-thread exceptions (non-UI threads, ThreadPool, etc.)
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Unobserved Task exceptions (fire-and-forget async, discarded Tasks)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrashDialog(e.Exception);
        e.Handled = true;   // prevents WPF from terminating the process
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // IsTerminating may already be true here — we show the dialog but cannot
        // always prevent shutdown for truly fatal CLR exceptions.
        ShowCrashDialog(e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ShowCrashDialog(e.Exception);
        e.SetObserved();    // prevents the process from being terminated by the finalizer thread
    }

    private static void ShowCrashDialog(Exception? ex)
    {
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex?.Message ?? "Unknown error"}" +
            $"\n\nCheck the Logs folder in %LocalAppData%\\ArchiveAutomator\\Logs for details.",
            "Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
