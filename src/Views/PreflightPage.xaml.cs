using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;

namespace Barista.Views;

public partial class PreflightPage : UserControl, IWizardPage
{
    private SetupState? _state;
    private List<Check> _checks = [];

    public PreflightPage() => InitializeComponent();

    public string StepLabel => "Checks";

    public string? FooterNote => _checks.Any(c => c.Level == CheckLevel.Block)
        ? "Something above blocks the install. Fix it and press Re-check."
        : _checks.Any(c => c.Level == CheckLevel.Warn)
            ? "Warnings do not block, but read them."
            : null;

    public bool CanAdvance => _checks.Count > 0 && _checks.All(c => c.Level != CheckLevel.Block);

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;
        Recheck();
    }

    public void OnLeaving(SetupState state) { }

    private void Recheck()
    {
        if (_state is null) return;
        _checks = Preflight.Run(_state);

        CheckList.ItemsSource = _checks.Select(c => new
        {
            c.Name,
            c.Detail,
            Remedy = c.Remedy ?? "",
            RemedyVisibility = c.Remedy is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed,
            Glyph = c.Level switch
            {
                CheckLevel.Pass => "✓",
                CheckLevel.Warn => "!",
                _ => "✗",
            },
            Accent = (Brush)FindResource(c.Level switch
            {
                CheckLevel.Pass => "Good",
                CheckLevel.Warn => "Warning",
                _ => "Danger",
            }),
            Background = (Brush)FindResource(c.Level switch
            {
                CheckLevel.Pass => "Card",
                CheckLevel.Warn => "WarnBg",
                _ => "DangerBg",
            }),
        }).ToList();

        // Only offered when something that loads into lsass was actually
        // chosen. Showing a "turn off lsass protection" button to someone who
        // picked the deny rights would be inviting a change they do not need.
        var needsLsa = _state.Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate
                       || _state.InstallAuthPackage;
        var ppl = Preflight.RunAsPpl();
        PplCard.Visibility = needsLsa && ppl is > 0 ? Visibility.Visible : Visibility.Collapsed;

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => Recheck();

    private void OnDisablePpl(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "RunAsPPL makes lsass a protected process. It is the thing that stops tools like "
            + "Mimikatz reading passwords and hashes straight out of lsass memory on a machine "
            + "where someone already has administrator rights.\n\n"
            + "Turning it off is what lets Windows load the unsigned DLLs from this project. It "
            + "also removes that protection for everything else on this machine, permanently, "
            + "until you turn it back on and reboot.\n\n"
            + "If there is any chance this machine holds credentials you care about, the answer "
            + "is no — use a different VM.\n\n"
            + "Set RunAsPPL to 0?",
            "Turn off lsass protection",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Preflight.SetRunAsPpl(0);
            MessageBox.Show(
                "RunAsPPL is now 0. It takes effect at the next reboot.\n\n"
                + "Put it back to 1 when you are done with this project — that is a one-line "
                + "change in the same place, and the uninstall page reminds you.",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not write RunAsPPL:\n\n{ex.Message}",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Recheck();
    }
}
