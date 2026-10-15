using System.Diagnostics;
using System.IO;
using System.Text;

namespace Barista.Services;

/// <summary>
/// Runs build.bat with the MSVC environment already set up.
///
/// Both articles say the same thing: you need the "x64 Native Tools Command
/// Prompt", because a plain cmd has neither cl.exe nor the SDK headers on the
/// PATH. That instruction is also the single most common way to waste twenty
/// minutes on this project - the error you get is a missing compiler, not a
/// missing environment, so people go looking for the wrong thing.
///
/// vswhere.exe knows where Visual Studio or the Build Tools are installed, so
/// the environment can be arranged here instead of being a prerequisite.
/// </summary>
public static class BuildRunner
{
    /// <summary>
    /// vswhere ships with the VS Installer and lives at a fixed path, on both
    /// 64-bit and 32-bit Windows, since VS 2017.
    /// </summary>
    private static string VsWherePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");

    public sealed record Toolchain(string InstallationPath, string VcVarsPath, string DisplayName);

    /// <summary>
    /// Finds a VS or Build Tools install that actually has the C++ toolset.
    /// Returns null with a reason if there is none.
    /// </summary>
    public static Toolchain? FindToolchain(out string reason)
    {
        reason = "";

        if (!File.Exists(VsWherePath))
        {
            reason = "vswhere.exe was not found. Visual Studio or the Build Tools do not appear "
                     + "to be installed on this machine.";
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = VsWherePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            // -products * is what makes this find the standalone Build Tools
            // as well as Community/Professional/Enterprise. Without it,
            // vswhere only reports the full IDE SKUs and a perfectly good
            // Build Tools install looks like nothing at all.
            psi.ArgumentList.Add("-latest");
            psi.ArgumentList.Add("-prerelease");
            psi.ArgumentList.Add("-products");
            psi.ArgumentList.Add("*");
            psi.ArgumentList.Add("-requires");
            psi.ArgumentList.Add("Microsoft.VisualStudio.Component.VC.Tools.x86.x64");
            psi.ArgumentList.Add("-property");
            psi.ArgumentList.Add("installationPath");

            using var p = Process.Start(psi);
            if (p is null)
            {
                reason = "Could not start vswhere.exe.";
                return null;
            }

            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15_000);

            if (path.Length == 0)
            {
                reason = "Visual Studio is installed, but no instance has the C++ toolset "
                         + "(\"Desktop development with C++\"). Add that workload in the Visual "
                         + "Studio Installer.";
                return null;
            }

            // A multi-line answer means several instances; take the first.
            path = path.Split('\n')[0].Trim();

            var vcvars = Path.Combine(path, "VC", "Auxiliary", "Build", "vcvars64.bat");
            if (!File.Exists(vcvars))
            {
                reason = $"Found an install at {path}, but vcvars64.bat is not where it should be. "
                         + "The C++ toolset may be only partly installed.";
                return null;
            }

            return new Toolchain(path, vcvars, Path.GetFileName(path.TrimEnd('\\')));
        }
        catch (Exception ex)
        {
            reason = $"Could not query vswhere.exe: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="batchName"/> in <paramref name="workingDir"/> with
    /// the x64 MSVC environment loaded.
    ///
    /// Goes through a generated .bat rather than cmd.exe /c "... &amp;&amp; ...":
    /// vcvars64.bat lives under a path with spaces, the build script has its
    /// own quoted arguments, and nesting those inside one command line is a
    /// reliable way to produce a command that works on one machine and not on
    /// another. A temp file has no quoting rules at all.
    /// </summary>
    public static async Task<int> RunBuildAsync(
        Toolchain toolchain,
        string workingDir,
        string batchName,
        Action<string> onLine,
        CancellationToken ct = default)
    {
        var script = Path.Combine(Path.GetTempPath(), $"doppio-build-{Guid.NewGuid():N}.bat");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"call \"{toolchain.VcVarsPath}\"");
        sb.AppendLine("if errorlevel 1 (echo Could not initialise the MSVC environment. & exit /b 1)");
        sb.AppendLine($"cd /d \"{workingDir}\"");
        sb.AppendLine("if errorlevel 1 (echo Could not enter the source folder. & exit /b 1)");
        sb.AppendLine($"call \"{batchName}\"");
        sb.AppendLine("exit /b %errorlevel%");

        await File.WriteAllTextAsync(script, sb.ToString(), ct).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SystemTools.Cmd,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine("! " + e.Data); };

            if (!p.Start()) throw new InvalidOperationException("Could not start cmd.exe.");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode;
        }
        finally
        {
            try { File.Delete(script); } catch { /* temp file, best effort */ }
        }
    }
}
