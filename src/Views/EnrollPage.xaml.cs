using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Barista.Services;
using QRCoder;

namespace Barista.Views;

public partial class EnrollPage : UserControl, IWizardPage
{
    /// <summary>
    /// One row. Enrolling and denying are separate flags on purpose.
    ///
    /// They used to be one: whatever was ticked here was written into both
    /// AccountsToEnroll and AccountsToDeny, with a comment saying the lists
    /// were the same and asking twice was redundant. They are not the same.
    /// Doppio's own deny-noninteractive.ps1 says to "keep a second admin
    /// account that still works", and the filter stage requires every enabled
    /// local account to be enrolled - so the break-glass admin has to be
    /// enrolled, and coupling the lists meant it was then also denied. That is
    /// precisely the account that must keep a path in.
    /// </summary>
    public sealed class Account : INotifyPropertyChanged
    {
        private bool _enroll;
        private bool _deny;
        private bool _denyApplies = true;

        public required string Name { get; init; }

        /// <summary>Run enroll.exe for this account.</summary>
        public bool Enroll
        {
            get => _enroll;
            set { _enroll = value; Raise(nameof(Enroll)); }
        }

        /// <summary>Apply deny-noninteractive.ps1 to this account.</summary>
        public bool Deny
        {
            get => _deny;
            set { _deny = value; Raise(nameof(Deny)); }
        }

        /// <summary>
        /// False when the chosen network policy is not the deny rights, in
        /// which case the column is greyed out rather than silently ignored.
        /// </summary>
        public bool DenyApplies
        {
            get => _denyApplies;
            set { _denyApplies = value; Raise(nameof(DenyApplies)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string property)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private readonly List<Account> _accounts = [];

    /// <summary>Held so the lockout warning can be recomputed as boxes are ticked.</summary>
    private SetupState? _state;

    /// <summary>
    /// False until InitializeComponent has finished. The TextChanged handler
    /// on the URI box reads controls declared below it, which do not exist
    /// while the XAML is still being parsed.
    /// </summary>
    private bool _ready;

    public EnrollPage()
    {
        InitializeComponent();
        _ready = true;
    }

    public string StepLabel => "Accounts";

    public string? FooterNote =>
        "enroll.exe opens in its own window for each account. It only stores a secret once a "
        + "code from your app matches.";

    public bool CanAdvance => true;

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        _state = state;

        if (_accounts.Count == 0)
        {
            foreach (var n in Preflight.LocalUserNames())
                _accounts.Add(NewAccount(n));

            NoAccounts.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AccountList.ItemsSource = _accounts;
        }

        var denyApplies = state.Network is NetworkPolicy.DenyRights;

        foreach (var a in _accounts)
        {
            a.Enroll = state.AccountsToEnroll.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
            a.Deny = denyApplies
                     && state.AccountsToDeny.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
            a.DenyApplies = denyApplies;
        }

        ColumnHelp.Text = denyApplies
            ? "Two separate decisions per account. Enrol the break-glass admin so the 2FA tile "
              + "will accept it, and leave its deny column clear so it keeps a path in."
            : "The deny column is off because you chose a filter rather than the deny rights. "
              + "A filter judges accounts by enrollment, so the 2FA column is the only one that "
              + "matters here.";

        UpdateLockoutWarning(state);
        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnLeaving(SetupState state)
    {
        state.AccountsToEnroll.Clear();
        state.AccountsToEnroll.AddRange(_accounts.Where(a => a.Enroll).Select(a => a.Name));

        state.AccountsToDeny.Clear();
        if (state.Network is NetworkPolicy.DenyRights)
            state.AccountsToDeny.AddRange(_accounts.Where(a => a.Deny).Select(a => a.Name));
    }

    /// <summary>
    /// Subscribes as it creates, so the warning below tracks the boxes rather
    /// than only the state of the page when it was opened. Subscribing here
    /// and not in OnEntering also means navigating back and forth does not
    /// attach the handler twice.
    /// </summary>
    private Account NewAccount(string name)
    {
        var account = new Account { Name = name };
        account.PropertyChanged += (_, _) =>
        {
            if (_state is not null) UpdateLockoutWarning(_state);
        };
        return account;
    }

    /// <summary>
    /// Says so when the current selection leaves nothing that can get in
    /// without the tile. Not a block - there are legitimate reasons to lock a
    /// lab VM down completely - but it should never happen by accident.
    /// </summary>
    private void UpdateLockoutWarning(SetupState state)
    {
        if (!_ready) return;

        var denyApplies = state.Network is NetworkPolicy.DenyRights;
        var filterComing = state.InstallCredentialProvider && state.EnableProviderFilter;

        string? warning = null;

        if (denyApplies && _accounts.Count > 0 && _accounts.All(a => a.Deny))
        {
            warning = "Every account listed is set to lose its non-interactive logons. Nothing "
                      + "will be able to reach this machine over the network afterwards, so a "
                      + "broken tile leaves only Safe Mode and the snapshot.";
        }
        else if (filterComing && _accounts.Count > 0 && _accounts.All(a => !a.Enroll))
        {
            warning = "No account is selected for enrollment, but the filter stage is in the "
                      + "plan. It will refuse to run: with the tile as the only sign-in option, "
                      + "an account without a secret cannot sign in at all.";
        }

        LockoutWarning.Text = warning ?? "";
        LockoutWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddManual(object sender, RoutedEventArgs e)
    {
        var name = ManualBox.Text.Trim();
        if (name.Length == 0) return;
        if (_accounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ManualBox.Clear();
            return;
        }

        // Enrolled but not denied: adding an account by hand is overwhelmingly
        // the break-glass case, and denying should never be the default of
        // anything.
        var account = NewAccount(name);
        account.Enroll = true;
        account.DenyApplies = _accounts.FirstOrDefault()?.DenyApplies ?? true;
        _accounts.Add(account);

        AccountList.ItemsSource = null;
        AccountList.ItemsSource = _accounts;
        NoAccounts.Visibility = Visibility.Collapsed;
        ManualBox.Clear();
    }

    private void OnUriChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;        // see _ready

        var text = UriBox.Text.Trim();

        if (text.Length == 0)
        {
            UriStatus.Text = "";
            QrFrame.Visibility = Visibility.Collapsed;
            return;
        }

        if (!text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            UriStatus.Text = "That does not look like an otpauth:// URI.";
            QrFrame.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(8);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            QrImage.Source = bmp;
            QrFrame.Visibility = Visibility.Visible;
            UriStatus.Text = "Rendered locally. Scan it, then press Clear.";
        }
        catch (Exception ex)
        {
            UriStatus.Text = $"Could not render that: {ex.Message}";
            QrFrame.Visibility = Visibility.Collapsed;
        }
    }

    private void OnClearQr(object sender, RoutedEventArgs e)
    {
        UriBox.Clear();
        QrImage.Source = null;
        QrFrame.Visibility = Visibility.Collapsed;
        UriStatus.Text = "Cleared. The secret is still in the enroll.exe console scrollback — "
                         + "close that window or run cls.";
    }
}
