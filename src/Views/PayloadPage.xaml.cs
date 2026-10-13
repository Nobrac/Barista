using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barista.Services;

namespace Barista.Views;

/// <summary>
/// Takes the place of the binaries page when this exe carries its own payload.
///
/// There is nothing to locate and nothing to build, so the one question left
/// is whether the files about to be run are the ones that were released. This
/// page answers it before anything else can happen: unpack to a protected
/// folder, read every file back, compare with the manifest, and keep Continue
/// disabled unless every row matches.
/// </summary>
public partial class PayloadPage : UserControl, IWizardPage
{
    private readonly EmbeddedPayload _payload;
    private readonly bool _resuming;
    private readonly ObservableCollection<HashRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndex = new(StringComparer.OrdinalIgnoreCase);

    private PayloadPreparation? _result;
    private SelfCheck.Result? _self;
    private string? _error;
    private bool _checking;
    private bool _checkedOnce;

    /// <param name="resuming">
    /// After a reboot. The Ristretto choice was made before it and is not
    /// offered again; the check itself runs exactly as on a first start.
    /// </param>
    public PayloadPage(EmbeddedPayload payload, bool resuming = false)
    {
        InitializeComponent();
        _payload = payload;
        _resuming = resuming;

        FileList.ItemsSource = _rows;
        SourceList.ItemsSource = payload.Manifest.Sources;
        BuiltFromNote.Text =
            $"Release {payload.Manifest.Version}, built "
            + $"{payload.Manifest.BuiltUtc.ToLocalTime():dd MMM yyyy, HH:mm} on a clean build "
            + "machine from these exact commits.";

        var hasRistretto = payload.Manifest.Has("Ristretto");
        RistrettoCard.Visibility = hasRistretto && !resuming ? Visibility.Visible : Visibility.Collapsed;
    }

    public string StepLabel => "Integrity";

    public string? FooterNote => _checking
        ? "Checking…"
        : _result?.AllMatch == true
            ? null
            : "Continue stays off until every file matches.";

    public bool CanAdvance => !_checking && _error is null && _result?.AllMatch == true;

    public event EventHandler? AdvanceAvailabilityChanged;

    public void OnEntering(SetupState state)
    {
        if (!_resuming && RistrettoCard.Visibility == Visibility.Visible)
            ChkRistretto.IsChecked = state.RistrettoDir is { Length: > 0 };

        // Once per page visit is too often for a 100 MB exe hash, and the
        // files are re-read on every visit anyway, which is the check that
        // matters. The exe does not change while it is running.
        _self ??= SelfCheck.Run();
        PaintSelf();

        if (!_checkedOnce) _ = RunCheckAsync();
    }

    public void OnLeaving(SetupState state)
    {
        // Always the payload folder, never a path typed in or read back from
        // the pending-run file - see MainWindow for the resume half of this.
        state.DoppioDir = _payload.DoppioDir;
        state.RistrettoDir = _resuming
            ? (state.RistrettoDir is { Length: > 0 } ? _payload.RistrettoDir : null)
            : (ChkRistretto.IsChecked == true ? _payload.RistrettoDir : null);
    }

    // --- the check -----------------------------------------------------------

    private async Task RunCheckAsync()
    {
        if (_checking) return;
        _checking = true;
        _checkedOnce = true;
        _error = null;
        _result = null;

        _rows.Clear();
        _rowIndex.Clear();
        foreach (var f in _payload.Manifest.Files)
        {
            _rowIndex[f.Path] = _rows.Count;
            _rows.Add(HashRow.Pending(f, this));
        }

        Progress.Maximum = _rows.Count;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        UnexpectedText.Visibility = Visibility.Collapsed;
        PaintSummary();
        Notify();

        try
        {
            var manifest = _payload.Manifest;
            var root = _payload.Root;

            _result = await Task.Run(() => PayloadExtractor.Prepare(
                manifest,
                _payload.OpenZip,
                root,
                ProtectedFolder.Create,
                row => Dispatcher.Invoke(() => Show(row))));
        }
        catch (Exception ex)
        {
            _error = ex is UnauthorizedAccessException
                ? "Could not write to the payload folder. Barista has to run as administrator."
                : ex.Message;
        }
        finally
        {
            _checking = false;
        }

        if (_result is { Unexpected.Count: > 0 } r)
        {
            UnexpectedText.Text =
                "Files in the payload folder that the release does not contain: "
                + string.Join(", ", r.Unexpected)
                + ". Nothing will run from a folder with extra files in it. Press Check again to "
                + "unpack it afresh.";
            UnexpectedText.Visibility = Visibility.Visible;
        }

        Progress.Visibility = Visibility.Collapsed;
        PaintSummary();
        Notify();
    }

    private void Show(FileVerification v)
    {
        if (!_rowIndex.TryGetValue(v.File.Path, out var i)) return;
        _rows[i] = HashRow.From(v, this);
        Progress.Value = _rows.Count(r => r.Done);
        FilesCount.Text = CountLine();
    }

    private string CountLine()
    {
        var done = _rows.Count(r => r.Done);
        var bad = _rows.Count(r => r.Bad);
        return done < _rows.Count
            ? $"{done} of {_rows.Count} files checked"
            : bad == 0
                ? $"All {_rows.Count} files checked"
                : $"{bad} of {_rows.Count} files do not match";
    }

    private void PaintSummary()
    {
        FilesCount.Text = CountLine();
        RecheckButton.IsEnabled = !_checking;
        CopyButton.IsEnabled = !_checking && _result is not null;
        OpenButton.IsEnabled = !_checking && Directory.Exists(_payload.Root);

        if (_checking)
        {
            Summary("…", "Muted", "Card",
                "Checking the built-in files…",
                $"Unpacking to {_payload.Root} and reading every file back.");
            return;
        }

        if (_error is not null)
        {
            Summary("✕", "Danger", "DangerBg",
                "The check could not be completed",
                _error + "\n\nNothing has been installed. Continue stays off.");
            return;
        }

        if (_result is { AllMatch: true } ok)
        {
            Summary("✓", "Good", "GoodBg",
                $"All {ok.Files.Count} files match",
                (ok.Unpacked ? "Unpacked to " : "Already in place in ")
                + $"{ok.Root}, a folder only administrators can change. Every file was read back "
                + "from there and its SHA-256 is the one recorded when the release was built.");
            return;
        }

        var bad = _result?.Files.Count(f => !f.Ok) ?? 0;
        Summary("✕", "Danger", "DangerBg",
            bad > 0 ? $"{bad} file(s) do not match - installation blocked" : "Unexpected files - installation blocked",
            "Something changed the files between unpacking and checking, or this copy of Barista is "
            + "damaged. Nothing has been installed. Press Check again to unpack afresh; if it "
            + "happens again, download Barista again and compare it with SHA256SUMS.");
    }

    private void Summary(string glyph, string accent, string background, string heading, string detail)
    {
        SummaryGlyph.Text = glyph;
        SummaryDot.Fill = (Brush)FindResource(accent);
        SummaryCard.Background = (Brush)FindResource(background);
        SummaryCard.BorderBrush = accent == "Muted" ? (Brush)FindResource("Line") : (Brush)FindResource(accent);
        SummaryHeading.Text = heading;
        SummaryHeading.Foreground = accent == "Muted" ? (Brush)FindResource("Ink") : (Brush)FindResource(accent);
        SummaryDetail.Text = detail;
    }

    private void PaintSelf()
    {
        if (_self is null) return;

        SelfPath.Text = _self.Path;
        SelfHash.Text = _self.Sha256 ?? "(could not be read)";
        SignatureText.Text = _self.Detail;
        SignatureDot.Fill = (Brush)FindResource(_self.Signature switch
        {
            SelfCheck.SignatureState.Valid => "Good",
            SelfCheck.SignatureState.Tampered => "Danger",
            _ => "Warning",
        });
    }

    private void Notify() => AdvanceAvailabilityChanged?.Invoke(this, EventArgs.Empty);

    // --- buttons -------------------------------------------------------------

    private async void OnRecheck(object sender, RoutedEventArgs e)
    {
        // A failed check leaves the folder as it was found, and Prepare would
        // only reuse it if it matched - so a recheck after a failure always
        // unpacks afresh.
        await RunCheckAsync();
    }

    /// <summary>
    /// The same "hash  path" layout as sha256sum and the release's SHA256SUMS,
    /// so the two can be compared line by line.
    /// </summary>
    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Barista {_payload.Manifest.Version}, payload {_payload.Manifest.Id}");
        foreach (var s in _payload.Manifest.Sources)
            sb.AppendLine($"# {s.Name} {s.Repository} @ {s.Commit}");
        if (_self?.Sha256 is { } selfHash)
            sb.AppendLine($"{selfHash}  Barista.exe");
        sb.AppendLine("# SHA-256 as read back from disk:");
        foreach (var row in _result?.Files ?? [])
        {
            if (!row.Ok)
                sb.AppendLine($"# {row.Status.ToString().ToUpperInvariant()}, expected {row.File.Sha256}");
            sb.AppendLine($"{row.ActualSha256 ?? new string('-', 64)}  {row.File.Path}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            CopyButton.Content = "Copied";
        }
        catch
        {
            // The clipboard is occasionally held by another process. Not worth
            // more than leaving the button as it was.
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_payload.Root)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                ArgumentList = { _payload.Root },
                UseShellExecute = false,
            });
        }
        catch { /* convenience only */ }
    }

    // --- one row ---------------------------------------------------------------

    public sealed record HashRow(
        string FileName,
        string Component,
        string SizeText,
        string Expected,
        string Actual,
        string Glyph,
        string StatusText,
        Brush Accent,
        Brush PillBackground,
        Brush ActualBrush,
        bool Done,
        bool Bad)
    {
        public string AccessibleName => $"{FileName}, {Component}: {StatusText}";

        public static HashRow Pending(PayloadFile f, FrameworkElement res) => new(
            f.FileName, f.Component, Size(f.Size), f.Sha256, "…",
            "·", "Checking", B(res, "Muted"), B(res, "Paper"), B(res, "Muted"),
            Done: false, Bad: false);

        public static HashRow From(FileVerification v, FrameworkElement res) => v.Status switch
        {
            HashStatus.Match => new(
                v.File.FileName, v.File.Component, Size(v.File.Size), v.File.Sha256, v.ActualSha256!,
                "✓", "Match", B(res, "Good"), B(res, "GoodBg"), B(res, "Good"),
                Done: true, Bad: false),
            HashStatus.Missing => new(
                v.File.FileName, v.File.Component, Size(v.File.Size), v.File.Sha256, "(file not found)",
                "!", "Missing", B(res, "Danger"), B(res, "DangerBg"), B(res, "Danger"),
                Done: true, Bad: true),
            _ => new(
                v.File.FileName, v.File.Component,
                v.ActualSize is { } s && s != v.File.Size ? $"{Size(s)}, expected {Size(v.File.Size)}" : Size(v.File.Size),
                v.File.Sha256, v.ActualSha256 ?? "",
                "✕", "Mismatch", B(res, "Danger"), B(res, "DangerBg"), B(res, "Danger"),
                Done: true, Bad: true),
        };

        private static Brush B(FrameworkElement res, string key) => (Brush)res.FindResource(key);

        private static string Size(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024):0.#} MB",
        };
    }
}
