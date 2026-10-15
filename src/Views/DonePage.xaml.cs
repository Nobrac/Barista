using System.Text;
using System.Windows;
using System.Windows.Controls;
using Barista.Services;

namespace Barista.Views;

public partial class DonePage : UserControl, IWizardPage
{
    private SetupState? _state;

    public DonePage() => InitializeComponent();

    public string StepLabel => "Finish";

    public string? FooterNote => _state?.NeedsReboot == true
        ? "Put RunAsPPL back when you are finished with this VM."
        : null;

    public bool CanAdvance => true;

    // CanAdvance never changes on this page, so there is nothing to raise.
    public event EventHandler? AdvanceAvailabilityChanged { add { } remove { } }

    public void OnEntering(SetupState state)
    {
        _state = state;

        // Reaching this page means every stage ran and every post-reboot
        // check was answered yes. The pending-run record has done its job;
        // leaving it behind would make the next launch offer to continue an
        // install that is already finished.
        StateStore.Clear();

        RebootCard.Visibility = state.NeedsReboot ? Visibility.Visible : Visibility.Collapsed;
        GateCard.Visibility = state.Network == NetworkPolicy.RistrettoGate
            ? Visibility.Visible : Visibility.Collapsed;

        // Shown whenever a tile is in play, and emphatically when it is the
        // only one: a lockout from wrong codes is indistinguishable from a
        // broken provider unless somebody has been told about /unlock.
        EnrollCard.Visibility = state.InstallCredentialProvider
            ? Visibility.Visible : Visibility.Collapsed;

        PplButton.IsEnabled = Preflight.RunAsPpl() is null or 0;
    }

    public void OnLeaving(SetupState state) { }

    private void OnRestorePpl(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Setting RunAsPPL back to 1 restores lsass protection at the next reboot.\n\n"
            + "The unsigned DLLs from this project will then no longer load — silently. If any "
            + "of them is still registered, uninstall it first, or the machine boots with an "
            + "Auth0 entry pointing at a filter that never runs.\n\n"
            + "Set RunAsPPL to 1?",
            "Restore lsass protection",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Preflight.SetRunAsPpl(1);
            PplButton.IsEnabled = false;
            MessageBox.Show("RunAsPPL is back to 1. It takes effect at the next reboot.",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not write RunAsPPL:\n\n{ex.Message}",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

        var answer = MessageBox.Show(
            $"This runs {steps.Count} step(s), built from what is actually registered on this "
            + "machine right now:\n\n"
            + string.Join("\n", steps.Select(s => "  • " + s.Title))
            + "\n\nIn that order, deliberately - the things that can stop a machine booting go "
            + "first, then the logon screen, then the user rights.\n\n"
            + "The enrolled secrets are NOT removed; 'enroll.exe /purge' does that.\n\n"
            + "Afterwards: reboot, and only THEN delete the DLLs from System32. lsass holds "
            + "them until it restarts.\n\nContinue?",
            "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        var log = new StringBuilder();
        UninstallLog.Visibility = Visibility.Visible;

        void Append(string line)
        {
            log.AppendLine(line);
            Dispatcher.Invoke(() => UninstallLog.Text = log.ToString());
        }

        foreach (var step in steps)
        {
            Append($"=== {step.Title}");
            try
            {
                var r = await ProcessRunner.RunStepAsync(step, Append);
                Append(r.Ok ? "    Done." : $"    FAILED (exit {r.ExitCode}).");
            }
            catch (Exception ex)
            {
                Append($"    FAILED: {ex.Message}");
            }
        }

        Append("");
        Append("Reboot now. Delete the DLLs from System32 after that, not before.");
    }
}
