using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Barista.Services;

namespace Barista;

public partial class App : Application
{
    /// <summary>
    /// Next to the pending-run record, so everything this tool leaves behind
    /// is in one place.
    /// </summary>
    private static string CrashLogPath => Path.Combine(StateStore.Folder, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception in a tool that is halfway through registering
        // an LSA component is the worst possible silent failure. Both hooks:
        // the dispatcher one for the UI thread, the AppDomain one for anything
        // that throws on a worker thread, where the dispatcher never sees it.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, fatal: true);

        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            // A throw before the window exists would otherwise close the
            // process with no window and no message at all.
            Report(ex, fatal: true);
            Shutdown(1);
        }
    }

    /// <summary>
    /// The binaries built into this exe, or null for a developer build that
    /// has none. Loaded once, before any window, so a release whose embedded
    /// manifest is damaged stops here instead of turning into a build that
    /// asks for folders.
    /// </summary>
    public static EmbeddedPayload? Payload { get; private set; }

    private void Start(StartupEventArgs e)
    {
        Payload = EmbeddedPayload.TryLoad(typeof(App).Assembly);

        var launchedByRunOnce = e.Args.Any(a =>
            a.Equals("/resume", StringComparison.OrdinalIgnoreCase));

        var pending = StateStore.Load();

        // Two ways in. /resume is the RunOnce entry firing after the reboot.
        // The other is somebody starting the tool by hand - which happens
        // whenever RunOnce did not fire, and is why the state file, not the
        // registry value, is the thing that actually remembers.
        if (pending is not null && !launchedByRunOnce)
        {
            var answer = MessageBox.Show(
                "There is an install from an earlier session that has not finished.\n\n"
                + $"Started {pending.StartedUtc.ToLocalTime():dd MMM, HH:mm}, "
                + $"{pending.CompletedStages.Count} stage(s) done.\n\n"
                + "Continue it?\n\n"
                + "Choosing No forgets the record. Nothing already installed is removed.",
                "Barista",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
            {
                StateStore.Clear();
                pending = null;
            }
        }
        else if (pending is null && launchedByRunOnce)
        {
            MessageBox.Show(
                "Barista was started to continue an install, but the saved record is "
                + "missing or unreadable.\n\n"
                + "Whatever was already installed is still installed. Check the Auth0 value "
                + "under HKLM\\SYSTEM\\CurrentControlSet\\Control\\Lsa\\MSV1_0 and the LSA "
                + "package list to see where things got to, then start over.",
                "Barista",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        new MainWindow(pending).Show();
        _started = true;
    }

    /// <summary>Set once the first window is up; see below for why it matters.</summary>
    private bool _started;

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Report(args.Exception, fatal: !_started);
        args.Handled = true;

        // Carrying on after an error mid-run is right: the window is up, the
        // log is on screen, and the person can read what happened. Carrying on
        // after one during startup is not - there is no usable window, the
        // process stays alive holding its own exe open, and the next build
        // cannot replace it. That turned every subsequent fix into "nothing
        // changed", because the old binary kept being the one that ran.
        if (!_started) Shutdown(1);
    }

    /// <summary>
    /// Writes the whole exception to a file and shows enough of it on screen
    /// to act on. "Object reference not set to an instance of an object" with
    /// no stack is not a diagnosable error message, and this tool is going to
    /// be run on a machine where attaching a debugger is awkward.
    /// </summary>
    private static void Report(Exception? ex, bool fatal)
    {
        var detail = ex?.ToString() ?? "Unknown error (no exception object).";

        var saved = "";
        try
        {
            StateStore.EnsureFolder();
            var entry = new StringBuilder()
                .AppendLine(new string('-', 70))
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  fatal={fatal}")
                .AppendLine(detail)
                .ToString();
            File.AppendAllText(CrashLogPath, entry);
            saved = $"\n\nThe full details were appended to:\n{CrashLogPath}";
        }
        catch
        {
            // If even the log cannot be written, the dialog is all there is.
        }

        // The first few frames are what identify the fault; the whole trace
        // would not fit in a message box.
        var frames = (ex?.StackTrace ?? "")
            .Split('\n')
            .Take(6)
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0);

        MessageBox.Show(
            $"Barista hit an unexpected error:\n\n"
            + $"{ex?.GetType().Name}: {ex?.Message}\n\n"
            + string.Join("\n", frames)
            + saved
            + "\n\nIf this happened during an install step, the machine may be in a "
            + "half-configured state. Check the log on the install page, and if an LSA "
            + "component was being registered, restore your snapshot rather than rebooting.",
            "Barista",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
