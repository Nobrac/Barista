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
    public sealed class Account : INotifyPropertyChanged
    {
        private bool _selected;
        public required string Name { get; init; }
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Account> _accounts = [];

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
        if (_accounts.Count == 0)
        {
            foreach (var n in Preflight.LocalUserNames())
                _accounts.Add(new Account { Name = n });

            NoAccounts.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AccountList.ItemsSource = _accounts;
        }

        foreach (var a in _accounts)
            a.Selected = state.AccountsToEnroll.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
    }

    public void OnLeaving(SetupState state)
    {
        var chosen = _accounts.Where(a => a.Selected).Select(a => a.Name).ToList();

        state.AccountsToEnroll.Clear();
        state.AccountsToEnroll.AddRange(chosen);

        // The deny rights and the filters apply per account, and the account
        // list is the same one. Carrying it over beats asking twice.
        state.AccountsToDeny.Clear();
        state.AccountsToDeny.AddRange(chosen);
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

        _accounts.Add(new Account { Name = name, Selected = true });
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
