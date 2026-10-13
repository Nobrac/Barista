using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;
using Microsoft.Win32;

namespace Barista.Views;

public partial class SourcePage : UserControl, IWizardPage
{
    // Shared with Preflight rather than restated. The two used to carry their
    // own, slightly different ideas of what a Doppio folder needs - this page
    // wanted install-doppio.ps1 and did not care about register.reg, Preflight
    // the other way round - so a folder could satisfy one page and be blocked
    // by the next.
    private static readonly string[] DoppioNeeds = Preflight.DoppioCoreFiles;

    private static readonly string[] RistrettoNeeds = Preflight.RistrettoFiles;

    private bool _haveToolchain;
    private bool _toolchainChecked;
    private BuildRunner.Toolchain? _toolchain;
    private string _toolchainReason = "";

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

        DoppioBox.Text = state.DoppioDir
                         ?? Guess("Doppio", "install-doppio.ps1", "build.bat") ?? "";

        var ristretto = state.RistrettoDir
                        ?? Guess("Ristretto", "install-ristretto.ps1", "build-ristretto.bat");
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
        try
        {
            _toolchain = BuildRunner.FindToolchain(out var reason);
            _toolchainReason = reason;
        }
        catch (Exception ex)
        {
            // Finding the compiler is a convenience. Whatever goes wrong in
            // there must not stop someone whose binaries are already built.
            _toolchain = null;
            _toolchainReason = $"Could not look for a C++ toolchain: {ex.Message}";
        }

        _haveToolchain = _toolchain is not null;

        BuildDoppioButton.IsEnabled = _haveToolchain;
        BuildRistrettoButton.IsEnabled = _haveToolchain;
    }

    /// <summary>
    /// The toolchain block, which depends on the folders as much as on the
    /// machine.
    ///
    /// A packaged bundle - Barista.exe beside folders of already-built DLLs,
    /// which is what tools\package-release.ps1 writes - lands on a machine that
    /// has no compiler and needs none. Reporting "No C++ toolchain" in warning
    /// colours there is a red mark against a machine that is set up exactly
    /// right, and the only thing it can teach somebody is to go and install
    /// several gigabytes of Visual Studio for nothing.
    /// </summary>
    private void PaintToolchain()
    {
        if (_toolchain is { } toolchain)
        {
            ToolchainHeading.Text = "C++ toolchain found";
            ToolchainHeading.Foreground = (Brush)FindResource("Good");
            ToolchainDetail.Text =
                $"{toolchain.InstallationPath}\nThe Build buttons below set the x64 environment "
                + "from vcvars64.bat before running the build script, so a plain console is fine.";
            return;
        }

        if (NothingLeftToBuild())
        {
            ToolchainHeading.Text = "Nothing to build";
            ToolchainHeading.Foreground = (Brush)FindResource("Good");
            ToolchainDetail.Text =
                "This machine has no C++ compiler and does not need one: everything the install "
                + "uses is already compiled and sitting in the folders below.";
            return;
        }

        ToolchainHeading.Text = "No C++ toolchain";
        ToolchainHeading.Foreground = (Brush)FindResource("Warning");
        ToolchainDetail.Text = _toolchainReason
            + "\nYou can still continue if the binaries are already built somewhere else and "
            + "copied here.";
    }

    /// <summary>Every folder the current choices need is complete.</summary>
    private bool NothingLeftToBuild()
    {
        if (!FolderHas(DoppioBox.Text.Trim(), DoppioNeeds)) return false;
        return ChkRistretto.IsChecked != true
               || FolderHas(RistrettoBox.Text.Trim(), RistrettoNeeds);
    }

    /// <summary>
    /// Finds a folder without asking, for the layouts that actually happen: the
    /// exe inside the repo it was built from, the exe beside the repos (where
    /// bootstrap.bat puts it), and the exe beside folders of already-built
    /// binaries (what a packaged bundle is).
    ///
    /// Walks up from the exe, and at each level looks both for a marker file
    /// itself and for a subfolder of the expected name holding one.
    ///
    /// Several markers, and the install script before the build script: a
    /// bundle has no build.bat in it at all - deliberately, because a build
    /// script on a machine with no compiler is an invitation to a dead end -
    /// and probing only for that one made the wizard fail to find a folder
    /// sitting right next to it.
    /// </summary>
    private static string? Guess(string folderName, params string[] markers)
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 6 && dir is { Length: > 0 }; i++)
        {
            if (markers.Any(m => File.Exists(Path.Combine(dir, m))))
                return dir;

            var sibling = Path.Combine(dir, folderName);
            if (markers.Any(m => File.Exists(Path.Combine(sibling, m))))
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

        // Before the folder lines: what it says depends on whether anything
        // still needs building, which the lines below work out.
        PaintToolchain();

        Paint(DoppioStatus, DoppioBox.Text, DoppioNeeds, "build.bat",
            Preflight.DoppioBuildOutputs);

        if (ChkRistretto.IsChecked == true)
            Paint(RistrettoStatus, RistrettoBox.Text, RistrettoNeeds, "build-ristretto.bat",
                Preflight.RistrettoBuildOutputs);
        else
            RistrettoStatus.Text = "";

        AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Paint(
        TextBlock target, string folder, string[] needs, string buildScript,
        string[] buildOutputs)
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
            // Only point at the Build button for things a build creates. The
            // lists also contain .reg and .ps1 files that ship with the
            // repository, and suggesting a compile run for a missing
            // install-doppio.ps1 just sends people round a loop.
            var hasScript = File.Exists(Path.Combine(folder.Trim(), buildScript));
            var buildWouldHelp = Preflight.AnyIsBuildOutput(missing, buildOutputs);

            // A folder with no build script in it is a packaged bundle, not a
            // checkout, and the machine it is on probably has no compiler. The
            // honest answer there is that the bundle is incomplete - not a
            // silent blank, which is what this used to leave.
            var advice = (buildWouldHelp, hasScript, _haveToolchain) switch
            {
                (true, true, true) => $"  Press Build… to run {buildScript}.",
                (true, true, false) => $"  {buildScript} is here, but there is no C++ compiler on "
                                       + "this machine to run it with.",
                (true, false, _) => "  There is no build script here either, so this is a packaged "
                                    + "copy and the file should have been in it. Ask for a "
                                    + "complete one.",
                (false, _, _) => "  Those ship with the repository rather than being built - this "
                                 + "looks like an incomplete copy of it.",
            };

            target.Text = $"Missing: {string.Join(", ", missing)}." + advice;
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
