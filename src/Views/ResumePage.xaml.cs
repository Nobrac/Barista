using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;

namespace Barista.Views;

public partial class ResumePage : UserControl, IWizardPage
{
    private readonly PersistedState _persisted;
    private SetupState? _state;
    private bool _confirmed;
    private bool _abandoned;

    public ResumePage(PersistedState persisted)
    {
        InitializeComponent();
        _persisted = persisted;
    }

    public string StepLabel => "Resume";

    public string? FooterNote => _abandoned
        ? "Nothing further will run. Restore the snapshot if the machine is in a bad state."
        : _confirmed ? null : "Answer the question above before continuing.";

    public bool CanAdvance => _confirmed && !_abandoned;

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;

        var stages = InstallPlan.BuildStages(state);
        var done = _persisted.CompletedStages;
        var remaining = stages.Where(s => !done.Contains(s.Id)).ToList();

        Intro.Text = _persisted.StartedUtc == default
            ? "This run was interrupted by a restart."
            : $"Started {_persisted.StartedUtc.ToLocalTime():dd MMM, HH:mm}. "
              + $"{done.Count} stage(s) done, {remaining.Count} to go.";

        OldLog.Text = _persisted.Log;

        // The verification question for whatever stage we rebooted out of.
        var awaiting = _persisted.AwaitingVerificationOf;
        var stage = awaiting is null ? null : stages.FirstOrDefault(s => s.Id == awaiting);

        if (stage?.VerifyPrompt is { Length: > 0 })
        {
            VerifyPrompt.Text = stage.VerifyPrompt;
            VerifyDetail.Text = stage.VerifyDetail ?? "";
            VerifyCard.Visibility = Visibility.Visible;
        }
        else
        {
            // Nothing to verify - a stage with no reboot, or a record from a
            // run that was simply closed. Let it through.
            VerifyCard.Visibility = Visibility.Collapsed;
            _confirmed = true;
        }

        RemainingList.ItemsSource = stages.Select(s => new
        {
            s.Title,
            Glyph = done.Contains(s.Id) ? "✓" : "○",
            Accent = (Brush)FindResource(done.Contains(s.Id) ? "Good" : "Ink"),
            Note = done.Contains(s.Id)
                ? "Done."
                : s.RebootAfter ? "Needs a restart afterwards." : "No restart needed.",
        }).ToList();

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnLeaving(SetupState state) { }

    private void OnConfirmed(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        VerifyCard.IsEnabled = false;
        VerifyPrompt.Text = "Confirmed. " + VerifyPrompt.Text;
        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFailed(object sender, RoutedEventArgs e)
    {
        _abandoned = true;
        _confirmed = false;
        VerifyCard.IsEnabled = false;

        var sb = new StringBuilder();
        sb.AppendLine(
            "Exactly one thing changed before this restart, which is why it got its own. "
            + "That is what to undo.");
        sb.AppendLine();

        var stage = _persisted.AwaitingVerificationOf;
        sb.AppendLine(stage switch
        {
            StageId.Provider =>
                "The credential provider is registered but nothing is in lsass yet. Safe Mode "
                + "still works, and unregister.reg removes it.",
            StageId.NetworkPolicy =>
                "A sub-authentication filter is registered in the Auth0 slot. If the machine is "
                + "usable, the uninstall script below clears it. If it is not booting, restore "
                + "the snapshot, or boot WinRE and delete the Auth0 value from the offline hive.",
            StageId.AuthPackage =>
                "The authentication package is in the LSA package list. If the machine is "
                + "usable, the uninstall script clears it. If it is not booting, restore the "
                + "snapshot, or edit \"Authentication Packages\" offline and leave msv1_0 in it.",
            _ => "Check the log above for what the last stage did.",
        });

        RecoveryText.Text = sb.ToString().TrimEnd();
        RecoveryCard.Visibility = Visibility.Visible;

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;

        var steps = InstallPlan.BuildUninstall(_state);
        if (steps.Count == 0)
        {
            MessageBox.Show("Nothing of this project appears to be registered right now.",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var log = new StringBuilder(OldLog.Text);
        void Append(string line)
        {
            log.AppendLine(line);
            Dispatcher.Invoke(() => { OldLog.Text = log.ToString(); OldLog.ScrollToEnd(); });
        }

        Append("");
        Append("=== Uninstall");

        foreach (var step in steps)
        {
            Append($"--- {step.Title}");
            try
            {
                var r = await ProcessRunner.RunScriptAsync(step.Command, step.Arguments, Append);
                Append(r.Ok ? "    Done." : $"    FAILED (exit {r.ExitCode}).");
            }
            catch (Exception ex)
            {
                Append($"    FAILED: {ex.Message}");
            }
        }

        Append("");
        Append("Reboot now. Delete the DLLs from System32 after that, not before.");
        StateStore.Clear();
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        StateStore.Clear();
        MessageBox.Show(
            "The pending run has been cleared. Nothing was uninstalled - whatever is already "
            + "registered stays registered.",
            "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }
}
