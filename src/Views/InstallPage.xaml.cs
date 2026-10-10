using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;

namespace Barista.Views;

public partial class InstallPage : UserControl, IWizardPage
{
    private SetupState? _state;
    private List<Stage> _stages = [];
    private readonly List<StageId> _done = [];
    private readonly StringBuilder _log = new();

    /// <summary>Set when a run is resumed, so earlier stages are not re-run.</summary>
    private PersistedState? _resumed;

    /// <summary>The stage that just finished and needs a reboot before the next one.</summary>
    private Stage? _awaitingReboot;

    public bool IsRunning { get; private set; }

    public InstallPage() => InitializeComponent();

    /// <summary>Called before the page is first entered, when this is a resumed run.</summary>
    public void AdoptResumedRun(PersistedState persisted)
    {
        _resumed = persisted;
        _done.Clear();
        _done.AddRange(persisted.CompletedStages);
        _log.Clear();
        if (persisted.Log.Length > 0)
        {
            _log.Append(persisted.Log);
            if (!persisted.Log.EndsWith('\n')) _log.AppendLine();
            _log.AppendLine("=== Resumed after restart");
            _log.AppendLine();
        }
    }

    public string StepLabel => "Install";

    public string? FooterNote => IsRunning
        ? "Running. Do not reboot or close this window."
        : _awaitingReboot is not null
            ? "This stage is done but not loaded. Restart before the next one."
            : AllDone ? null : "Nothing has been changed yet.";

    public bool CanAdvance => AllDone && !IsRunning && _awaitingReboot is null;

    private bool AllDone => _stages.Count > 0 && _stages.All(s => _done.Contains(s.Id));

    private Stage? NextStage => _stages.FirstOrDefault(s => !_done.Contains(s.Id));

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;
        _stages = InstallPlan.BuildStages(state);

        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();

        EmptyNote.Visibility = _stages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    public void OnLeaving(SetupState state) { }

    private void Refresh()
    {
        var next = NextStage;

        StageList.ItemsSource = _stages.Select(s =>
        {
            var finished = _done.Contains(s.Id);
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

        var waiting = _awaitingReboot is not null;

        RunButton.Visibility = waiting || next is null ? Visibility.Collapsed : Visibility.Visible;
        RunButton.IsEnabled = !IsRunning && next is not null;
        RunButton.Content = next is null ? "Run this stage" : $"Run: {next.Title}";

        RebootButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;

        LogHeader.Text = IsRunning ? "Log — running"
            : waiting ? "Log — stage finished, restart pending"
            : AllDone ? "Log — finished" : "Log";

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Log(string line)
    {
        Dispatcher.Invoke(() =>
        {
            _log.AppendLine(line);
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private async void OnRunStage(object sender, RoutedEventArgs e)
    {
        if (_state is null || IsRunning) return;
        if (NextStage is not { } stage) return;

        var confirm = MessageBox.Show(
            $"Stage: {stage.Title}\n\n"
            + $"{stage.Steps.Count} step(s).\n\n"
            + (stage.RebootAfter
                ? "This stage needs a restart afterwards. LSA only reads its registrations at "
                  + "boot, so nothing it installs is loaded until then.\n\nSnapshot taken?\n\n"
                : "No restart needed for this stage.\n\n")
            + "Continue?",
            "Barista",
            MessageBoxButton.YesNo,
            stage.RebootAfter ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        IsRunning = true;
        Refresh();

        Log($"=== {stage.Title}");
        var failed = false;

        for (var i = 0; i < stage.Steps.Count; i++)
        {
            var step = stage.Steps[i];
            Log($"--- [{i + 1}/{stage.Steps.Count}] {step.Title}");

            try
            {
                if (step.Kind == StepKind.Console)
                {
                    Log($"    Opening {Path.GetFileName(step.Command)} in its own window.");
                    Log("    Finish in that window, then come back here.");
                    var code = await ProcessRunner.RunInOwnConsoleAsync(step.Command, step.Arguments);
                    Log(code == 0
                        ? "    Done."
                        : $"    Exited with code {code}. Nothing was stored if it reported a failure.");
                    if (code != 0) failed = true;
                }
                else
                {
                    var result = await ProcessRunner.RunScriptAsync(step.Command, step.Arguments, Log);
                    Log(result.Ok ? "    Done." : $"    FAILED (exit {result.ExitCode}).");
                    if (!result.Ok) failed = true;
                }
            }
            catch (Exception ex)
            {
                Log($"    FAILED: {ex.Message}");
                failed = true;
            }

            if (failed)
            {
                // Stopping here matters more than usual: the steps are ordered
                // so that nothing starts refusing logons before there is a way
                // in. Carrying on past a failure is how that ordering breaks.
                Log("");
                Log("Stopped. A later step would have assumed this one worked.");
                Log("Read the error above, fix it, and run the stage again - or restore the snapshot.");
                break;
            }
        }

        IsRunning = false;

        if (failed)
        {
            Log("");
            Refresh();
            MessageBox.Show(
                "A step failed and the rest of this stage was skipped.\n\n"
                + "Because stages run one at a time, only this stage's changes are in play. If "
                + "it was registering an LSA component, prefer restoring the snapshot over "
                + "rebooting.",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _done.Add(stage.Id);
        Log($"=== {stage.Title}: done.");
        Log("");

        if (stage.RebootAfter)
        {
            _awaitingReboot = stage;
            Refresh();
            MessageBox.Show(
                $"{stage.Title} is registered, but LSA has not read it yet - that happens at boot.\n\n"
                + "Restart when you are ready. This tool saves where it got to and opens again "
                + "after you log on, to ask whether the machine came back properly before it "
                + "runs anything else.",
                "Restart needed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            Refresh();
        }
    }

    private void SavePendingRun(bool armRunOnce)
    {
        if (_state is null) return;

        var persisted = _resumed ?? PersistedState.From(_state);
        persisted.DoppioDir = _state.DoppioDir;
        persisted.RistrettoDir = _state.RistrettoDir;
        persisted.InstallCredentialProvider = _state.InstallCredentialProvider;
        persisted.InstallAuthPackage = _state.InstallAuthPackage;
        persisted.DenyKeepNetwork = _state.DenyKeepNetwork;
        persisted.Network = _state.Network;
        persisted.AccountsToEnroll = [.. _state.AccountsToEnroll];
        persisted.AccountsToDeny = [.. _state.AccountsToDeny];
        persisted.CompletedStages = [.. _done];
        persisted.AwaitingVerificationOf = _awaitingReboot?.Id;
        persisted.Log = _log.ToString();

        StateStore.Save(persisted, armRunOnce);
    }

    private void OnReboot(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This restarts the machine now.\n\n"
            + "If it does not come back, restore the snapshot. Exactly one thing changed since "
            + "the last working boot, which is the entire reason stages exist.\n\nRestart?",
            "Restart", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        SavePendingRun(armRunOnce: true);
        StateStore.Reboot();
        Application.Current.Shutdown();
    }

    private void OnRebootLater(object sender, RoutedEventArgs e)
    {
        SavePendingRun(armRunOnce: true);
        MessageBox.Show(
            $"Saved. Restart when you are ready.\n\n"
            + "This tool opens again after the next logon to continue. If it does not - the exe "
            + "moved, or the RunOnce entry was cleared - just start it by hand and it will find "
            + "the pending run.\n\n"
            + $"The record is at:\n{StateStore.FilePath}",
            "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }
}
