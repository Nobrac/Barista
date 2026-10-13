using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;

namespace Barista.Views;

/// <summary>
/// The staged install, on screen.
///
/// Everything this page does is show and ask. What actually runs, in what
/// order, what counts as a failure and what gets written to disk is
/// <see cref="StageRunner"/> - which is in the core assembly, where it can be
/// tested without a window. The dialogs stayed here, because a confirmation is
/// the one part that genuinely needs a person looking at a screen.
/// </summary>
public partial class InstallPage : UserControl, IWizardPage
{
    private SetupState? _state;
    private StageRunner? _runner;

    /// <summary>Set when a run is resumed, so earlier stages are not re-run.</summary>
    private PersistedState? _resumed;

    public bool IsRunning => _runner?.IsRunning == true;

    public InstallPage() => InitializeComponent();

    /// <summary>
    /// Called before the page is first entered, when this is a resumed run.
    /// The record is kept until <see cref="OnEntering"/>, because the runner
    /// needs the state object and that only arrives with the page.
    /// </summary>
    public void AdoptResumedRun(PersistedState persisted) => _resumed = persisted;

    public string StepLabel => "Install";

    public string? FooterNote => IsRunning
        ? "Running. Do not reboot or close this window."
        : _runner?.AwaitingReboot is not null
            ? "This stage is done but not loaded. Restart before the next one."
            : _runner is null or { AllDone: true } ? null
            // Not "nothing has been changed" once a stage has run: it has, and
            // the record on disk now says so, which is what makes closing the
            // window here safe rather than lossy.
            : _runner.Completed.Count > 0
                ? "Finished stages are saved. You can close this and continue later."
                : "Nothing has been changed yet.";

    public bool CanAdvance =>
        _runner is { AllDone: true, IsRunning: false, AwaitingReboot: null };

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;

        if (_runner is null)
        {
            _runner = new StageRunner(state, onLine: AppendLine);
            if (_resumed is not null) _runner.AdoptResumedRun(_resumed);
        }
        else
        {
            // The user may have gone back and changed a component since the
            // plan was built. Finished stages are tracked by id, so rebuilding
            // does not re-offer them.
            _runner.Rebuild();
        }

        LogBox.Text = _runner.Transcript;
        LogBox.ScrollToEnd();

        EmptyNote.Visibility = _runner.Stages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    public void OnLeaving(SetupState state) { }

    private void Refresh()
    {
        if (_runner is null) return;

        var done = _runner.Completed;
        var next = _runner.Next;

        StageList.ItemsSource = _runner.Stages.Select(s =>
        {
            var finished = done.Contains(s.Id);
            var isNext = !finished && s.Id == next?.Id;

            return new
            {
                s.Title,
                s.Why,
                Steps = s.Steps.Select(st => new
                {
                    st.Title,
                    Command = st.Arguments.Count == 0
                        ? st.Command
                        : $"{st.Command} {string.Join(' ', st.Arguments)}",
                }).ToList(),
                Glyph = finished ? "✓" : isNext ? "▶" : "○",
                Accent = (Brush)FindResource(finished ? "Good" : isNext ? "Coffee" : "Muted"),
                Background = (Brush)FindResource(isNext ? "Card" : "Paper"),
                Thickness = isNext ? new Thickness(2) : new Thickness(1),
                Badge = s.RebootAfter ? "restart after" : "no restart",
                BadgeBg = (Brush)FindResource(s.RebootAfter ? "WarnBg" : "Paper"),
                BadgeVisibility = finished ? Visibility.Collapsed : Visibility.Visible,
            };
        }).ToList();

        var waiting = _runner.AwaitingReboot is not null;

        RunButton.Visibility = waiting || next is null ? Visibility.Collapsed : Visibility.Visible;
        RunButton.IsEnabled = !IsRunning && next is not null;
        RunButton.Content = next is null ? "Run this stage" : $"Run: {next.Title}";

        RebootButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;

        LogHeader.Text = IsRunning ? "Log — running"
            : waiting ? "Log — stage finished, restart pending"
            : _runner.AllDone ? "Log — finished" : "Log";

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The runner's output sink. Called from the threadpool threads that read a
    /// script's stdout and stderr, so every touch of the TextBox goes through
    /// the dispatcher.
    /// </summary>
    private void AppendLine(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private async void OnRunStage(object sender, RoutedEventArgs e)
    {
        if (_state is null || _runner is null || _runner.IsRunning) return;
        if (_runner.AwaitingReboot is not null) return;
        if (_runner.Next is not { } stage) return;

        if (!ConfirmStage(stage)) return;
        if (!PayloadStillIntact()) return;

        // Started, then refreshed, then awaited. RunNextStageAsync sets
        // IsRunning before its first await, so by the time the task is handed
        // back the flag is already true and this refresh is the one that
        // disables the button.
        var running = _runner.RunNextStageAsync();
        Refresh();

        StageOutcome outcome;
        try
        {
            outcome = await running;
        }
        catch (Exception ex)
        {
            // The runner only throws when it was asked to do something it
            // refuses to do - a stage while one is running, or past a pending
            // reboot. The guards above mean that should not be reachable, so
            // this is a bug report rather than a user-facing failure.
            Refresh();
            MessageBox.Show(ex.Message, "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Refresh();

        if (!outcome.Ok)
        {
            ReportFailure(outcome);
            return;
        }

        if (outcome.RebootPending)
        {
            MessageBox.Show(
                $"{stage.Title} is registered, but LSA has not read it yet - that happens at boot.\n\n"
                + "Restart when you are ready. This tool saves where it got to and opens again "
                + "after you log on, to ask whether the machine came back properly before it "
                + "runs anything else.",
                "Restart needed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// The confirmation before a stage runs.
    ///
    /// A stage carrying a PreRunPrompt has a precondition no registry read can
    /// establish - "have you actually signed in with the tile yet?" - so that
    /// question replaces the generic blurb rather than being appended under it,
    /// where it would be read as boilerplate.
    /// </summary>
    /// <summary>
    /// Reads the built-in files back once more right before a stage runs them.
    ///
    /// The integrity page checked them already, but a confirmation dialog, an
    /// enrollment and possibly a coffee lie between that and this. A few
    /// hundred kilobytes of hashing is cheap next to running a changed script
    /// elevated.
    /// </summary>
    private bool PayloadStillIntact()
    {
        if (App.Payload is not { } payload) return true;

        var rows = PayloadVerifier.VerifyFolder(payload.Manifest, payload.Root);
        var extra = PayloadVerifier.Unexpected(payload.Manifest, payload.Root);
        var bad = rows.Where(r => !r.Ok).Select(r => $"{r.File.Path}: {r.Status}").Concat(extra).ToList();
        if (bad.Count == 0)
        {
            AppendLine($"Built-in files re-checked: all {rows.Count} SHA-256 values match.");
            return true;
        }

        AppendLine("Built-in files changed since the integrity check - stage not started:");
        foreach (var line in bad) AppendLine("    " + line);

        MessageBox.Show(
            "The built-in files no longer match the release:\n\n"
            + string.Join("\n", bad)
            + "\n\nThe stage was not started. Go back to the Integrity page and press Check again "
            + "to unpack them afresh.",
            "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    private static bool ConfirmStage(Stage stage)
    {
        var body = stage.PreRunPrompt is { Length: > 0 }
            ? $"Stage: {stage.Title}\n\n{stage.PreRunPrompt}\n\n"
              + (stage.RebootAfter ? "A restart follows, and it is the test.\n\nSnapshot taken?\n\n" : "")
            : $"Stage: {stage.Title}\n\n"
              + $"{stage.Steps.Count} step(s).\n\n"
              + (stage.RebootAfter
                  ? "This stage needs a restart afterwards. LSA only reads its registrations at "
                    + "boot, so nothing it installs is loaded until then.\n\nSnapshot taken?\n\n"
                  : "No restart needed for this stage.\n\n");

        return MessageBox.Show(
            body + "Continue?",
            "Barista",
            MessageBoxButton.YesNo,
            stage.RebootAfter ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static void ReportFailure(StageOutcome outcome)
    {
        // Stages are the unit of retry, so re-running one re-runs its earlier
        // steps too. That is harmless for a script that checks before it
        // writes, and not harmless for enrollment: enroll.exe offers to replace
        // an existing secret, and accepting that issues a new one and silently
        // invalidates the authenticator entry.
        var retryNote = outcome.Stage.Id == StageId.Enrollment
            ? "\n\nRunning this stage again will go through every account in it, including "
              + "any that already succeeded. enroll.exe will ask before replacing an "
              + "existing secret - answer no for those, or the code in the app stops working."
            : "";

        MessageBox.Show(
            "A step failed and the rest of this stage was skipped.\n\n"
            + "Because stages run one at a time, only this stage's changes are in play. If "
            + "it was registering an LSA component, prefer restoring the snapshot over "
            + "rebooting."
            + retryNote,
            "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnReboot(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;

        var confirm = MessageBox.Show(
            "This restarts the machine now.\n\n"
            + "If it does not come back, restore the snapshot. Exactly one thing changed since "
            + "the last working boot, which is the entire reason stages exist.\n\nRestart?",
            "Restart", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        _runner.Save(armRunOnce: true);
        StateStore.Reboot();
        Application.Current.Shutdown();
    }

    private void OnRebootLater(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;

        _runner.Save(armRunOnce: true);
        MessageBox.Show(
            "Saved. Restart when you are ready.\n\n"
            + "This tool opens again after the next logon to continue. If it does not - the exe "
            + "moved, or the RunOnce entry was cleared - just start it by hand and it will find "
            + "the pending run.\n\n"
            + $"The record is at:\n{StateStore.FilePath}",
            "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }
}
