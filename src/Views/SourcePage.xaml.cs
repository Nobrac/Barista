using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;
using Microsoft.Win32;

namespace Barista.Views;

public partial class SourcePage : UserControl, IWizardPage
{
    private static readonly string[] DoppioNeeds =
        ["TacProvider.dll", "enroll.exe", "install-doppio.ps1"];

    private static readonly string[] RistrettoNeeds =
        ["RistrettoFilter.dll", "ristretto.exe", "install-ristretto.ps1"];

    private bool _haveToolchain;
    private bool _toolchainChecked;

    /// <summary>
    /// False until InitializeComponent has finished. A TextChanged handler
    /// can fire while the XAML is still being parsed, and the controls it
    /// reads are declared further down - they are null at that point.
    /// </summary>
    private bool _ready;

    public SourcePage()
    {
        InitializeComponent();
        _ready = true;
    }

    public string StepLabel => "Binaries";
    public string? FooterNote => "Nothing is copied yet. This only locates — or builds — the files.";

    public bool CanAdvance
    {
        get
        {
            if (!FolderHas(DoppioBox.Text, DoppioNeeds)) return false;
            if (ChkRistretto.IsChecked == true && !FolderHas(RistrettoBox.Text, RistrettoNeeds))
                return false;
            return true;
        }
    }

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        // Not in the constructor. Every page is constructed when the window
        // opens, and this one shells out to vswhere.exe - doing that at
        // startup, for a page nobody has reached yet, makes the app slower to
        // appear and turns any failure in it into a crash before the first
        // window is on screen.
        if (!_toolchainChecked)
        {
            _toolchainChecked = true;
            DetectToolchain();
        }

        DoppioBox.Text = state.DoppioDir ?? Guess("Doppio", "build.bat") ?? "";

        var ristretto = state.RistrettoDir ?? Guess("Ristretto", "build-ristretto.bat");
        RistrettoBox.Text = ristretto ?? "";

        // Tick it only when the folder is actually there. Finding Ristretto
        // beside Doppio is a good reason to offer it; it is not a reason to
        // decide for the user, so the components page still defaults to the
        // deny rights and this is only a pre-filled path.
        ChkRistretto.IsChecked = state.RistrettoDir is { Length: > 0 }
                                 || ristretto is { Length: > 0 };
        RistrettoRow.IsEnabled = ChkRistretto.IsChecked == true;

        UpdateStatus();
    }

    public void OnLeaving(SetupState state)
    {
        state.DoppioDir = DoppioBox.Text.Trim();
        state.RistrettoDir = ChkRistretto.IsChecked == true ? RistrettoBox.Text.Trim() : null;
    }

    private void DetectToolchain()
    {
        BuildRunner.Toolchain? toolchain;
        string reason;
        try
        {
            toolchain = BuildRunner.FindToolchain(out reason);
        }
        catch (Exception ex)
        {
            // Finding the compiler is a convenience. Whatever goes wrong in
            // there must not stop someone whose binaries are already built.
            toolchain = null;
            reason = $"Could not look for a C++ toolchain: {ex.Message}";
        }

        _haveToolchain = toolchain is not null;

        if (toolchain is not null)
        {
            ToolchainHeading.Text = "C++ toolchain found";
            ToolchainHeading.Foreground = (Brush)FindResource("Good");
            ToolchainDetail.Text =
                $"{toolchain.InstallationPath}\nThe Build buttons below set the x64 environment "
                + "from vcvars64.bat before running the build script, so a plain console is fine.";
        }
        else
        {
            ToolchainHeading.Text = "No C++ toolchain";
            ToolchainHeading.Foreground = (Brush)FindResource("Warning");
            ToolchainDetail.Text = reason
                + "\nYou can still continue if the binaries are already built somewhere else and "
                + "copied here.";
        }

        BuildDoppioButton.IsEnabled = _haveToolchain;
        BuildRistrettoButton.IsEnabled = _haveToolchain;
    }

    /// <summary>
    /// Finds a source folder without asking, for the two layouts that actually
    /// happen: the exe sitting inside the repo it was built from, and the exe
    /// sitting beside the repos (which is where bootstrap.bat puts it).
    ///
    /// Walks up from the exe, and at each level looks both for the marker file
    /// itself and for a subfolder of the expected name holding it.
    /// </summary>
    private static string? Guess(string folderName, string marker)
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 6 && dir is { Length: > 0 }; i++)
        {
            if (File.Exists(Path.Combine(dir, marker)))
                return dir;

            var sibling = Path.Combine(dir, folderName);
            if (File.Exists(Path.Combine(sibling, marker)))
                return sibling;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private static bool FolderHas(string? folder, IEnumerable<string> files)
        => folder is { Length: > 0 }
           && Directory.Exists(folder)
           && files.All(f => File.Exists(Path.Combine(folder, f)));

    private static string[] MissingFrom(string? folder, IEnumerable<string> files)
        => folder is { Length: > 0 } && Directory.Exists(folder)
            ? files.Where(f => !File.Exists(Path.Combine(folder, f))).ToArray()
            : files.ToArray();

    private void UpdateStatus()
    {
        if (!_ready) return;        // see _ready

        Paint(DoppioStatus, DoppioBox.Text, DoppioNeeds, "build.bat");

        if (ChkRistretto.IsChecked == true)
            Paint(RistrettoStatus, RistrettoBox.Text, RistrettoNeeds, "build-ristretto.bat");
        else
            RistrettoStatus.Text = "";

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Paint(TextBlock target, string folder, string[] needs, string buildScript)
    {
        if (folder.Trim().Length == 0)
        {
            target.Text = "No folder selected yet.";
            target.Foreground = (Brush)FindResource("Muted");
            return;
        }

        var missing = MissingFrom(folder.Trim(), needs);
        if (missing.Length == 0)
        {
            target.Text = "Found everything this needs.";
            target.Foreground = (Brush)FindResource("Good");
        }
        else
        {
            var hasScript = File.Exists(Path.Combine(folder.Trim(), buildScript));
            target.Text = $"Missing: {string.Join(", ", missing)}."
                + (hasScript && _haveToolchain ? $"  Press Build… to run {buildScript}." : "");
            target.Foreground = (Brush)FindResource("Danger");
        }
    }

    private void OnPathChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    private void OnRistrettoToggled(object sender, RoutedEventArgs e)
    {
        RistrettoRow.IsEnabled = ChkRistretto.IsChecked == true;
        UpdateStatus();
    }

    private void OnBrowseDoppio(object sender, RoutedEventArgs e)
        => Browse(DoppioBox, "Select the folder with the Doppio sources");

    private void OnBrowseRistretto(object sender, RoutedEventArgs e)
        => Browse(RistrettoBox, "Select the folder with the Ristretto sources");

    private void OnBuildDoppio(object sender, RoutedEventArgs e)
        => Build(DoppioBox.Text.Trim(), "build.bat", "Doppio");

    private void OnBuildRistretto(object sender, RoutedEventArgs e)
        => Build(RistrettoBox.Text.Trim(), "build-ristretto.bat", "Ristretto");

    private void Build(string folder, string batchName, string label)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            MessageBox.Show("Pick the source folder first.",
                "Build", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new BuildWindow(folder, batchName, label) { Owner = Window.GetWindow(this) };
        window.ShowDialog();

        // Re-check either way: a failed build may still have produced some of
        // the outputs, and the status line should show what is actually there.
        UpdateStatus();
    }

    private static void Browse(TextBox target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (target.Text.Trim() is { Length: > 0 } current && Directory.Exists(current))
            dlg.InitialDirectory = current;
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FolderName;
    }
}
