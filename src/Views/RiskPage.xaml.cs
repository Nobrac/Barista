using System.Windows;
using System.Windows.Controls;
using Barista.Services;

namespace Barista.Views;

public partial class RiskPage : UserControl, IWizardPage
{
    public RiskPage() => InitializeComponent();

    public string StepLabel => "Risks";
    public string? FooterNote => CanAdvance
        ? null
        : "All three have to be true before this tool will go any further.";

    public bool CanAdvance =>
        ChkThrowaway.IsChecked == true
        && ChkSnapshot.IsChecked == true
        && ChkRecovery.IsChecked == true;

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        ChkThrowaway.IsChecked = state.AcceptedThrowawayMachine;
        ChkSnapshot.IsChecked = state.AcceptedSnapshot;
        ChkRecovery.IsChecked = state.AcceptedRecoveryPlan;
    }

    public void OnLeaving(SetupState state)
    {
        state.AcceptedThrowawayMachine = ChkThrowaway.IsChecked == true;
        state.AcceptedSnapshot = ChkSnapshot.IsChecked == true;
        state.AcceptedRecoveryPlan = ChkRecovery.IsChecked == true;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
        => AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
}
