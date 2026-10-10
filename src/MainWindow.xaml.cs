using System.Windows;
using System.Windows.Media;
using Barista.Services;
using Barista.Views;

namespace Barista;

/// <summary>
/// A page in the wizard. <see cref="CanAdvance"/> is what gates the Continue
/// button; <see cref="OnLeaving"/> writes the page's answers into the state.
/// </summary>
public interface IWizardPage
{
    string StepLabel { get; }
    string? FooterNote { get; }
    bool CanAdvance { get; }
    event EventHandler? AdvanceAvailabilityChanged;
    void OnEntering(SetupState state);
    void OnLeaving(SetupState state);
}

public partial class MainWindow : Window
{
    private readonly SetupState _state;
    private readonly List<IWizardPage> _pages = [];
    private int _index;

    /// <summary>
    /// <paramref name="resume"/> non-null means we were started after a
    /// reboot in the middle of a staged install. The questions that were
    /// already answered are not asked again - the only thing that matters now
    /// is whether the machine came back in one piece.
    /// </summary>
    public MainWindow(PersistedState? resume = null)
    {
        InitializeComponent();

        if (resume is not null)
        {
            _state = resume.ToSetupState();

            var install = new InstallPage();
            install.AdoptResumedRun(resume);

            _pages.Add(new ResumePage(resume));
            _pages.Add(install);
            _pages.Add(new DonePage());
        }
        else
        {
            _state = new SetupState();

            _pages.Add(new RiskPage());
            _pages.Add(new SourcePage());
            // Components before Preflight on purpose: several checks only
            // apply to some choices, and a check that blocks on something you
            // did not ask for teaches people to ignore checks.
            _pages.Add(new ComponentsPage());
            _pages.Add(new PreflightPage());
            _pages.Add(new EnrollPage());
            _pages.Add(new InstallPage());
            _pages.Add(new DonePage());
        }

        foreach (var p in _pages)
            p.AdvanceAvailabilityChanged += (_, _) => RefreshChrome();

        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        var page = _pages[_index];
        page.OnEntering(_state);
        PageHost.Content = page;
        BuildRail();
        RefreshChrome();
    }

    private void BuildRail()
    {
        var rows = _pages.Select((p, i) => new
        {
            Label = p.StepLabel,
            Dot = i < _index
                ? (Brush)FindResource("Good")
                : i == _index ? (Brush)FindResource("Coffee") : new SolidColorBrush(Color.FromRgb(0xC4, 0xBD, 0xB1)),
            Colour = i == _index ? (Brush)FindResource("Ink") : (Brush)FindResource("Muted"),
            Weight = i == _index ? FontWeights.SemiBold : FontWeights.Normal,
        }).ToList();
        StepRail.ItemsSource = rows;
    }

    private void RefreshChrome()
    {
        var page = _pages[_index];
        BackButton.IsEnabled = _index > 0 && page is not InstallPage { IsRunning: true };
        NextButton.IsEnabled = page.CanAdvance;
        NextButton.Content = _index == _pages.Count - 1 ? "Close" : "Continue";
        FooterNote.Text = page.FooterNote ?? "";
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _pages[_index].OnLeaving(_state);
        ShowPage(_index - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        _pages[_index].OnLeaving(_state);

        if (_index == _pages.Count - 1)
        {
            Close();
            return;
        }

        // Nothing to enroll and no filter wanted: the enrollment page would be
        // an empty screen, so it is skipped rather than shown blank.
        var next = _index + 1;
        if (_pages[next] is EnrollPage && !_state.InstallCredentialProvider
            && _state.Network is NetworkPolicy.Leave)
            next++;

        ShowPage(next);
    }
}
