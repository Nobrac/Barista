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

    public event EventHandler? AdvanceAvailabilityChanged;

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
            $"This runs {steps.Count} uninstall script(s): the LSA components are deregistered "
            + "first, because those are the ones that can stop a machine booting.\n\n"
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
    }
}
