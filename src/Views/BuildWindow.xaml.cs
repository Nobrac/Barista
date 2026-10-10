using System.IO;
using System.Windows;
using Barista.Services;

namespace Barista.Views;

public partial class BuildWindow : Window
{
    private readonly string _workingDir;
    private readonly string _batchName;
    private bool _running;

    /// <summary>True once a build finished with exit code 0.</summary>
    public bool Succeeded { get; private set; }

    public BuildWindow(string workingDir, string batchName, string label)
    {
        InitializeComponent();
        _workingDir = workingDir;
        _batchName = batchName;

        Heading.Text = $"Build {label}";
        Subheading.Text = $"{batchName} in {workingDir}";
    }

    private void Append(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        if (!File.Exists(Path.Combine(_workingDir, _batchName)))
        {
            Append($"! {_batchName} is not in {_workingDir}.");
            Append("  Pick the folder that holds the build script, not its parent.");
            return;
        }

        var toolchain = BuildRunner.FindToolchain(out var reason);
        if (toolchain is null)
        {
            Append("! " + reason);
            Append("");
            Append("  Without the C++ toolset this cannot build anything. The binaries can still");
            Append("  be built by hand from the x64 Native Tools Command Prompt on a machine that");
            Append("  has it, and copied here.");
            return;
        }

        Append($"Using the toolchain at {toolchain.InstallationPath}");
        Append($"Environment from {toolchain.VcVarsPath}");
        Append("");

        _running = true;
        StartButton.IsEnabled = false;
        CloseButton.IsEnabled = false;

        try
        {
            var code = await BuildRunner.RunBuildAsync(toolchain, _workingDir, _batchName, Append);
            Append("");
            if (code == 0)
            {
                Succeeded = true;
                Append("Build finished.");
                StartButton.Content = "Build again";
            }
            else
            {
                Append($"Build FAILED (exit {code}). The compiler output above says why.");
                StartButton.Content = "Try again";
            }
        }
        catch (Exception ex)
        {
            Append($"! {ex.Message}");
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A compiler run is interruptible without consequence, but a
        // half-written DLL in the output folder is confusing later. Let it
        // finish rather than leaving one behind.
        if (_running)
        {
            e.Cancel = true;
            MessageBox.Show("The build is still running. Let it finish.",
                "Build", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
