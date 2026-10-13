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

    public string? FooterNote => _busy
        ? "Checking this machine…"
        : _checks.Any(c => c.Level == CheckLevel.Block)
            ? "Something above blocks the install. Fix it and press Re-check."
            : _checks.Any(c => c.Level == CheckLevel.Warn)
                ? "Warnings do not block, but read them."
                : null;

    public bool CanAdvance => _checks.Count > 0 && _checks.All(c => c.Level != CheckLevel.Block);

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;
        _ = RecheckAsync();
    }

    public void OnLeaving(SetupState state) { }

    /// <summary>
    /// Runs the checks off the UI thread.
    ///
    /// Two of them shell out to PowerShell - the WinInit event 12 lookup and
    /// the unenrolled-accounts list - and Get-WinEvent against the System log
    /// is comfortably slow enough to be seen. Run inline, this page froze on
    /// entry, which reads as the tool having hung at exactly the moment it is
    /// asking to be trusted with the logon path.
    /// </summary>
    private async Task RecheckAsync()
    {
        if (_state is null || _busy) return;

        var state = _state;
        SetBusy(true);
        try
        {
            // One background hop for both: Run needs the lsass-protection
            // state for its own check, and Paint needs it to decide whether
            // the "turn it off" button is even honest to show.
            var (checks, ppl) = await Task.Run(() =>
            {
                var p = Preflight.ReadPplState();
                return (Preflight.Run(state, p), p);
            }).ConfigureAwait(true);

            _checks = checks;
            _ppl = ppl;
        }
        catch (Exception ex)
        {
            // A check that throws must not leave the page blank with the
            // Continue button live: an empty _checks list reads as "no
            // blockers", which is the one wrong answer here. CanAdvance treats
            // a Block as final, so this is a refusal, not a warning.
            _checks =
            [
                new Check("Checks could not be run", CheckLevel.Block, ex.Message,
                    "Press Re-check. If it keeps failing, something is wrong with this machine's "
                    + "PowerShell or event log, and none of the guards below can be relied on."),
            ];
        }
        finally
        {
            SetBusy(false);
        }

        Paint();
    }

    private bool _busy;
    private Preflight.PplState? _ppl;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyNote.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecheckButton.IsEnabled = !busy;
        if (busy)
        {
            // While checking, nothing is known - so nothing is advanceable.
            _checks = [];
            AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Paint()
    {
        if (_state is null) return;

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
        //
        // And only when switching it off would actually help. When lsass is
        // running protected despite clear registry values - the Windows 11
        // 22H2+ case - this button writes a 0 that changes nothing, and
        // offering it means the user gives up the protection AND stays
        // blocked. The check above says so in words instead.
        var needsLsa = _state.Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate
                       || _state.InstallAuthPackage;
        var offerPplSwitch = needsLsa
                             && _ppl is { BlocksUnsignedLoad: true, ProtectedDespiteRegistry: false };
        PplCard.Visibility = offerPplSwitch ? Visibility.Visible : Visibility.Collapsed;

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => _ = RecheckAsync();

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
                "RunAsPPL is now 0, and RunAsPPLBoot with it if it was set — the install "
                + "scripts gate on both, so clearing only one would have left you blocked after "
                + "giving up the protection anyway.\n\n"
                + "It takes effect at the next reboot. The checks have been re-run; if lsass is "
                + "still reported as protected after a restart, this machine is enforcing it "
                + "from somewhere the registry does not show.\n\n"
                + "Put it back to 1 when you are done with this project — the last page has a "
                + "button for it.",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not write RunAsPPL:\n\n{ex.Message}",
                "Barista", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _ = RecheckAsync();
    }
}
