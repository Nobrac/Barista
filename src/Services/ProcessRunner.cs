using System.Diagnostics;
using System.IO;
using System.Text;

namespace Barista.Services;

/// <summary>
/// Runs the repo's existing PowerShell scripts and console tools, streaming
/// their output back line by line.
///
/// Deliberately thin. This installer does not write to HKLM, does not copy
/// into System32 and does not touch the Lsa keys itself. All of that lives in
/// install-*.ps1 / uninstall-*.ps1, which already carry the guards (RunAsPPL
/// check, "Auth0 belongs to somebody else" check, PrevAuth0 bookkeeping).
/// Reimplementing any of it here would mean two copies of security-sensitive
/// logic that silently drift apart - exactly the failure mode SECURITY-FIXES.md
/// describes for the duplicated registry reads.
/// </summary>
public sealed class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Runs a PowerShell script with output redirected to <paramref name="onLine"/>.</summary>
    public static Task<Result> RunScriptAsync(
        string scriptPath,
        IEnumerable<string>? arguments,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Script not found: {scriptPath}", scriptPath);

        var psi = new ProcessStartInfo
        {
            // -NoProfile so a user's profile cannot change how the script behaves.
            // -ExecutionPolicy Bypass because the scripts come out of a git clone
            // and are not signed; the alternative is telling every user to loosen
            // the machine-wide policy, which is worse.
            FileName = "powershell.exe",
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in arguments ?? [])
            psi.ArgumentList.Add(a);

        return RunAsync(psi, onLine, ct);
    }

    /// <summary>
    /// Launches a console tool in its OWN visible console window and waits.
    ///
    /// This is for enroll.exe. It cannot be driven through redirected pipes:
    /// the y/N prompts use _getwch(), which reads the console directly and
    /// never looks at stdin. Trying to automate it would mean changing
    /// enroll.cpp, and the enrollment flow is the one place where the secret
    /// is generated and confirmed - not somewhere to add a second code path
    /// for the sake of a prettier window.
    /// </summary>
    public static async Task<int> RunInOwnConsoleAsync(
        string exePath,
        IEnumerable<string>? arguments,
        CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found: {exePath}", exePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true,   // gives it a real console of its own
        };
        foreach (var a in arguments ?? [])
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {exePath}.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    private static async Task<Result> RunAsync(
        ProcessStartInfo psi, Action<string> onLine, CancellationToken ct)
    {
        var all = new StringBuilder();

        void Emit(string? line)
        {
            if (line is null) return;
            all.AppendLine(line);
            onLine(line);
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Emit(e.Data);
        p.ErrorDataReceived += (_, e) => Emit(e.Data is null ? null : "! " + e.Data);

        if (!p.Start())
            throw new InvalidOperationException($"Could not start {psi.FileName}.");

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A half-finished install-*.ps1 is worse than a finished one, so the
            // child is never killed mid-run. Cancellation only stops us waiting.
            Emit("! Cancellation requested. The script is still running and will finish on its own.");
            throw;
        }

        return new Result(p.ExitCode, all.ToString());
    }
}
