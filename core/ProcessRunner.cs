using System.Diagnostics;
using System.IO;
using System.Text;

namespace Barista.Services;

/// <summary>
/// What running one <see cref="Step"/> came to. Exit code 0 is the only
/// success - every install script in both repositories signals failure that
/// way, and reg.exe writes even "the operation completed successfully" to
/// stderr, so output is never the judge.
/// </summary>
public sealed record StepResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs a step. Behind an interface so <see cref="StageRunner"/> can be tested
/// without starting powershell.exe: the thing worth asserting about a stage is
/// which steps it runs, in what order, and that it stops at the first failure -
/// none of which needs a real process.
/// </summary>
public interface IStepExecutor
{
    Task<StepResult> RunAsync(Step step, Action<string> onLine, CancellationToken ct = default);
}

/// <summary>
/// Runs the repo's existing PowerShell scripts and console tools, streaming
/// their output back line by line.
///
/// Deliberately thin. This installer composes no registry writes of its own,
/// copies nothing into System32 and never touches the Lsa keys directly. Every
/// key, value and ordering lives in install-*.ps1 / uninstall-*.ps1 and the
/// repository's own .reg files, which already carry the guards (RunAsPPL check,
/// "Auth0 belongs to somebody else" check, PrevAuth0 bookkeeping).
/// Reimplementing any of it here would mean two copies of security-sensitive
/// logic that silently drift apart - exactly the failure mode SECURITY-FIXES.md
/// describes for the duplicated registry reads.
///
/// The one thing it does hand to the registry is a .reg file, unchanged, via
/// reg.exe - see <see cref="RunRegImportAsync"/>. The single exception outside
/// this class is Preflight.SetRunAsPpl, which has its own dialog.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs whatever a <see cref="Step"/> is, by its kind.
    ///
    /// The uninstall paths use this rather than assuming every step is a
    /// PowerShell script. They did assume that, which is part of why the
    /// recovery button could not run unregister.reg even once the plan
    /// included it.
    /// </summary>
    public static async Task<StepResult> RunStepAsync(
        Step step, Action<string> onLine, CancellationToken ct = default)
    {
        switch (step.Kind)
        {
            case StepKind.RegImport:
                return await RunRegImportAsync(step.Command, onLine, ct).ConfigureAwait(false);

            case StepKind.Console:
            {
                onLine($"    Opening {Path.GetFileName(step.Command)} in its own window.");
                onLine("    Finish in that window, then come back here.");
                var code = await RunInOwnConsoleAsync(step.Command, step.Arguments, ct)
                    .ConfigureAwait(false);
                return new StepResult(code, "");
            }

            default:
                return await RunScriptAsync(step.Command, step.Arguments, onLine, ct)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Runs a PowerShell script with output redirected to <paramref name="onLine"/>.</summary>
    public static Task<StepResult> RunScriptAsync(
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
            // -ExecutionPolicy Bypass because these scripts are not signed - they
            // arrive either from a git clone or unpacked from a bundle, and
            // neither carries a signature. The alternative is telling every user
            // to loosen the machine-wide policy, which is worse.
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
    /// Imports a .reg file with reg.exe.
    ///
    /// Doppio's unregister.reg is the only way back out of the credential
    /// provider, and a .reg file cannot be handed to "powershell -File". This
    /// is still not Barista writing to HKLM by hand: the keys and the order
    /// they are deleted in live in the repository's own file, exactly as the
    /// registration does.
    ///
    /// reg.exe writes even "The operation completed successfully." to stderr,
    /// so, as in install-doppio.ps1, only the exit code decides. Its stderr is
    /// logged without the "!" prefix the script runner uses, because here a
    /// line on stderr is not a sign of anything.
    /// </summary>
    public static Task<StepResult> RunRegImportAsync(
        string regFilePath,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        if (!File.Exists(regFilePath))
            throw new FileNotFoundException($".reg file not found: {regFilePath}", regFilePath);

        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            WorkingDirectory = Path.GetDirectoryName(regFilePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(regFilePath);

        return RunAsync(psi, onLine, ct, prefixStderr: false);
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

    private static async Task<StepResult> RunAsync(
        ProcessStartInfo psi, Action<string> onLine, CancellationToken ct,
        bool prefixStderr = true)
    {
        var all = new StringBuilder();

        // OutputDataReceived and ErrorDataReceived are raised on threadpool
        // threads, and a script that writes to both at once raises them
        // concurrently. StringBuilder is not thread-safe: two AppendLine calls
        // overlapping can interleave into nonsense or throw out of the
        // internal chunk list. The log is the only evidence a user has of what
        // a stage did to lsass, so it is worth a lock that is never contended
        // for more than the length of one line.
        var gate = new object();

        void Emit(string? line)
        {
            if (line is null) return;
            lock (gate) all.AppendLine(line);
            onLine(line);
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Emit(e.Data);
        p.ErrorDataReceived += (_, e) => Emit(
            e.Data is null ? null : (prefixStderr ? "! " + e.Data : e.Data));

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

        return new StepResult(p.ExitCode, all.ToString());
    }
}

/// <summary>
/// The real executor: <see cref="ProcessRunner"/> behind
/// <see cref="IStepExecutor"/>. Nothing but a cast away from the static class,
/// deliberately - the indirection exists for the tests, not to add a layer.
/// </summary>
public sealed class ProcessStepExecutor : IStepExecutor
{
    public static readonly ProcessStepExecutor Instance = new();

    public Task<StepResult> RunAsync(Step step, Action<string> onLine, CancellationToken ct = default)
        => ProcessRunner.RunStepAsync(step, onLine, ct);
}
