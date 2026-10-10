using System.Windows;
using System.Windows.Controls;
using Barista.Services;

namespace Barista.Views;

public partial class ComponentsPage : UserControl, IWizardPage
{
    private SetupState? _state;

    /// <summary>
    /// False until InitializeComponent has finished.
    ///
    /// The XAML sets IsChecked="True" on controls declared near the top, and
    /// the parser raises Checked the moment it does - while the rest of the
    /// file has not been parsed yet, so the x:Name fields for anything below
    /// are still null. A handler that touches them crashes before the window
    /// ever appears.
    /// </summary>
    private bool _ready;

    public ComponentsPage()
    {
        InitializeComponent();
        _ready = true;
    }

    public string StepLabel => "Components";

    public string? FooterNote => RbRistretto.IsChecked == true
        ? "Ristretto needs Doppio installed and an account enrolled before its installer will run."
        : RbDeny.IsChecked == true
            ? "No code in lsass with this choice. Nothing here can stop the machine booting."
            : null;

    public bool CanAdvance
    {
        get
        {
            // Ristretto was not located on the binaries page, so it cannot be chosen.
            if (RbRistretto.IsChecked == true && _state?.RistrettoDir is not { Length: > 0 })
                return false;
            return true;
        }
    }

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;

        ChkProvider.IsChecked = state.InstallCredentialProvider;
        ChkAuthPackage.IsChecked = state.InstallAuthPackage;
        ChkKeepNetwork.IsChecked = state.DenyKeepNetwork;

        RbDeny.IsChecked = state.Network == NetworkPolicy.DenyRights;
        RbDoppioFilter.IsChecked = state.Network == NetworkPolicy.DoppioFilter;
        RbRistretto.IsChecked = state.Network == NetworkPolicy.RistrettoGate;

        var haveRistretto = state.RistrettoDir is { Length: > 0 };
        RbRistretto.IsEnabled = haveRistretto;
        if (!haveRistretto)
        {
            RistrettoNote.Text =
                "Not available: no Ristretto folder was selected on the previous page. "
                + "Go back and tick \"I also want to install Ristretto\".";
            if (RbRistretto.IsChecked == true) RbDeny.IsChecked = true;
        }

        Sync();
    }

    public void OnLeaving(SetupState state)
    {
        state.InstallCredentialProvider = ChkProvider.IsChecked == true;
        state.InstallAuthPackage = ChkAuthPackage.IsChecked == true;
        state.DenyKeepNetwork = ChkKeepNetwork.IsChecked == true;

        state.Network =
            RbDoppioFilter.IsChecked == true ? NetworkPolicy.DoppioFilter
            : RbRistretto.IsChecked == true ? NetworkPolicy.RistrettoGate
            : RbDeny.IsChecked == true ? NetworkPolicy.DenyRights
            : NetworkPolicy.Leave;
    }

    private void Sync()
    {
        if (!_ready) return;        // see _ready
        ChkKeepNetwork.IsEnabled = RbDeny.IsChecked == true;
        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Sync();
}
