using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace Barista.Services;

public enum CheckLevel
{
    /// <summary>Fine. Nothing to do.</summary>
    Pass,
    /// <summary>Worth knowing. Does not stop the install.</summary>
    Warn,
    /// <summary>The install cannot proceed until this is resolved.</summary>
    Block,
}

public sealed record Check(
    string Name,
    CheckLevel Level,
    string Detail,
    string? Remedy = null);

/// <summary>
/// Read-only inspection of the machine. Nothing here changes anything -
/// the one thing in this app that can change machine state outside the
/// install scripts is <see cref="SetRunAsPpl"/>, and that sits behind its own
/// confirmation in the UI.
/// </summary>
public static class Preflight
{
    public const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";
    public const string MsvKey = @"SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0";
    public const string DoppioKey = @"SOFTWARE\TheAdminCafe\2FA";
    public const string RistrettoKey = @"SOFTWARE\TheAdminCafe\Ristretto";
    public const string InstallDir = @"C:\Program Files\TheAdminCafe";

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int? RunAsPpl()
        => Registry.LocalMachine.OpenSubKey(LsaKey)?.GetValue("RunAsPPL") as int?;

    public static string? CurrentAuth0()
        => Registry.LocalMachine.OpenSubKey(MsvKey)?.GetValue("Auth0") as string;

    public static bool DoppioInstalled()
        => File.Exists(Path.Combine(InstallDir, "TacProvider.dll"));

    public static bool DoppioHasEnrollments()
    {
        using var rids = Registry.LocalMachine.OpenSubKey(DoppioKey + @"\Rids");
        return rids is not null && rids.GetValueNames().Length > 0;
    }

    public static bool RistrettoInstalled()
        => File.Exists(Path.Combine(InstallDir, "ristretto.exe"));

    /// <summary>
    /// Best-effort hypervisor sniff from the BIOS keys. Deliberately not WMI:
    /// one less dependency, and this is a hint for a warning, never a gate.
    /// A false negative here is fine; the risk page asks the user anyway.
    /// </summary>
    public static (bool LooksVirtual, string Description) VirtualMachineHint()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var manufacturer = bios?.GetValue("SystemManufacturer") as string ?? "";
        var product = bios?.GetValue("SystemProductName") as string ?? "";
        var combined = $"{manufacturer} {product}".Trim();

        string[] markers =
        [
            "VMware", "VirtualBox", "innotek", "QEMU", "Xen", "Parallels",
            "Virtual Machine", "Hyper-V", "KVM", "Bochs", "Standard PC",
        ];

        var hit = markers.Any(m => combined.Contains(m, StringComparison.OrdinalIgnoreCase));
        return (hit, combined.Length == 0 ? "unknown hardware" : combined);
    }

    /// <summary>
    /// The one state-changing call in this class, and the only reason it is
    /// here at all is that the alternative is telling people to paste a reg
    /// command they do not understand.
    ///
    /// It is never called as part of "Install". The UI puts it behind its own
    /// dialog that explains what RunAsPPL protects, because turning it off is
    /// a real and lasting reduction in the machine's security - not a
    /// checkbox on the way to somewhere else.
    /// </summary>
    public static void SetRunAsPpl(int value)
    {
        using var lsa = Registry.LocalMachine.OpenSubKey(LsaKey, writable: true)
            ?? throw new InvalidOperationException($@"Cannot open HKLM\{LsaKey} for writing.");
        if (value == 0)
            lsa.SetValue("RunAsPPL", 0, RegistryValueKind.DWord);
        else
            lsa.SetValue("RunAsPPL", value, RegistryValueKind.DWord);
    }

    /// <summary>Local, enabled user accounts, for the enrollment page.</summary>
    public static IReadOnlyList<string> LocalUserNames()
    {
        // Enumerated from the SAM profile list would miss never-logged-on
        // accounts, so this shells out to the same place enroll.exe looks.
        var names = new List<string>();
        try
        {
            using var searcher = new System.Diagnostics.Process();
            searcher.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            searcher.StartInfo.ArgumentList.Add("-NoProfile");
            searcher.StartInfo.ArgumentList.Add("-NonInteractive");
            searcher.StartInfo.ArgumentList.Add("-Command");
            searcher.StartInfo.ArgumentList.Add(
                "Get-LocalUser | Where-Object Enabled | Select-Object -ExpandProperty Name");
            searcher.Start();
            while (searcher.StandardOutput.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) names.Add(trimmed);
            }
            searcher.WaitForExit(10_000);
        }
        catch
        {
            // A missing user list is a cosmetic loss: the page falls back to a
            // free-text box, and enroll.exe validates the name properly anyway.
        }
        return names;
    }

    /// <summary>
    /// Every check, in the order a reader should see them.
    ///
    /// Takes the chosen components, because several checks only matter for
    /// some of them. RunAsPPL is the clearest case: it decides whether an
    /// unsigned DLL loads into lsass, and it is irrelevant to someone who
    /// picked the deny rights and no filter. Blocking that person on it would
    /// be telling them to turn off a real protection for no reason at all -
    /// which is how people learn to switch things off without reading.
    /// </summary>
    public static List<Check> Run(SetupState state)
    {
        var sourceDir = state.DoppioDir;
        var ristrettoDir = state.RistrettoDir;
        var needsLsa = state.Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate
                       || state.InstallAuthPackage;

        var checks = new List<Check>();

        checks.Add(IsElevated()
            ? new Check("Elevated", CheckLevel.Pass, "Running as administrator.")
            : new Check("Elevated", CheckLevel.Block, "Not running as administrator.",
                "Close this and start it again with 'Run as administrator'."));

        checks.Add(Environment.Is64BitProcess
            ? new Check("Architecture", CheckLevel.Pass, "64-bit process.")
            : new Check("Architecture", CheckLevel.Block,
                "Running as a 32-bit process. Registry writes would be redirected to Wow6432Node.",
                "Build and run the x64 configuration."));

        var os = Environment.OSVersion.Version;
        checks.Add(os.Major >= 10
            ? new Check("Windows version", CheckLevel.Pass, $"Windows {os.Major}.{os.Minor} build {os.Build}.")
            : new Check("Windows version", CheckLevel.Block,
                $"Windows {os.Major}.{os.Minor} is older than this project targets.",
                "Use a Windows 10/11 or Server 2016+ VM."));

        var (looksVirtual, hw) = VirtualMachineHint();
        checks.Add(looksVirtual
            ? new Check("Virtual machine", CheckLevel.Pass, $"Looks like a VM ({hw}).")
            : new Check("Virtual machine", CheckLevel.Warn,
                $"This does not look like a VM ({hw}).",
                needsLsa
                    ? "You have chosen components that load into lsass. A fault there is a "
                      + "bugcheck on the logon path, and that can leave a machine that will not "
                      + "boot. Strongly consider moving to a VM you can roll back."
                    : "Nothing you have chosen runs inside lsass, so this is less pressing than "
                      + "usual - but the credential provider can still cost you the logon tile."));

        var ppl = RunAsPpl();
        if (!needsLsa)
        {
            checks.Add(new Check("RunAsPPL", CheckLevel.Pass,
                ppl is > 0
                    ? $"Set to {ppl}, and it can stay that way."
                    : "Not enabled.",
                ppl is > 0
                    ? "Nothing you have chosen loads into lsass, so lsass protection does not "
                      + "need to be touched. Leave it on."
                    : null));
        }
        else
        {
            checks.Add(ppl is null or 0
                ? new Check("RunAsPPL", CheckLevel.Pass,
                    ppl is null ? "Not set (lsass is not a protected process)." : "Set to 0.")
                : new Check("RunAsPPL", CheckLevel.Block,
                    $"RunAsPPL is {ppl}. lsass will refuse to load the unsigned DLLs, silently.",
                    "This has to be off for the components you picked. It is a real downgrade - "
                    + "see the button below, which explains what you are giving up. If you would "
                    + "rather not, go back and choose the deny rights instead."));
        }

        var auth0 = CurrentAuth0();
        var slotWanted = state.Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate;
        checks.Add(auth0 switch
        {
            null => new Check("MSV1_0 Auth0 slot", CheckLevel.Pass, "Free (no sub-authentication filter registered)."),
            "TacSubAuth" => new Check("MSV1_0 Auth0 slot", CheckLevel.Pass, "Held by Doppio's TacSubAuth."),
            "RistrettoFilter" => new Check("MSV1_0 Auth0 slot", CheckLevel.Pass, "Held by RistrettoFilter."),
            _ when !slotWanted => new Check("MSV1_0 Auth0 slot", CheckLevel.Warn,
                $"Auth0 belongs to '{auth0}'.",
                "Not a problem for what you picked - nothing here will touch that slot."),
            _ => new Check("MSV1_0 Auth0 slot", CheckLevel.Block,
                $"Auth0 belongs to '{auth0}'.",
                "Another product owns this slot and only one filter can hold it. Remove that "
                + "product first, or choose the deny rights instead."),
        });

        checks.Add(DoppioInstalled()
            ? new Check("Doppio installed", CheckLevel.Pass, $"TacProvider.dll found in {InstallDir}.")
            : new Check("Doppio installed", CheckLevel.Warn, "TacProvider.dll not found.",
                "Fine if you are installing Doppio now. Ristretto needs it already in place."));

        checks.Add(DoppioHasEnrollments()
            ? new Check("Enrollments", CheckLevel.Pass, "At least one account is enrolled and indexed by RID.")
            : new Check("Enrollments", CheckLevel.Warn, "No RID-indexed enrollments found.",
                state.AccountsToEnroll.Count > 0
                    ? "Fine - the enrollment stage runs before any filter is registered."
                    : "Enroll at least one account before installing a filter, or every "
                      + "enrolled-account check has nothing to match. Ristretto's installer "
                      + "refuses without this."));

        if (sourceDir is { Length: > 0 })
        {
            string[] doppioNeeds = ["TacProvider.dll", "enroll.exe", "register.reg"];
            var missing = doppioNeeds.Where(f => !File.Exists(Path.Combine(sourceDir, f))).ToArray();
            checks.Add(missing.Length == 0
                ? new Check("Doppio build output", CheckLevel.Pass, $"Found in {sourceDir}.")
                : new Check("Doppio build output", CheckLevel.Block,
                    "Missing: " + string.Join(", ", missing),
                    "Run build.bat from the x64 Native Tools Command Prompt, then point this at that folder."));
        }
        else
        {
            checks.Add(new Check("Doppio build output", CheckLevel.Block, "No folder selected.",
                "Pick the folder holding the built Doppio binaries and install scripts."));
        }

        if (ristrettoDir is { Length: > 0 })
        {
            string[] ristrettoNeeds = ["RistrettoFilter.dll", "ristretto.exe", "install-ristretto.ps1"];
            var missing = ristrettoNeeds.Where(f => !File.Exists(Path.Combine(ristrettoDir, f))).ToArray();
            checks.Add(missing.Length == 0
                ? new Check("Ristretto build output", CheckLevel.Pass, $"Found in {ristrettoDir}.")
                : new Check("Ristretto build output", CheckLevel.Block,
                    "Missing: " + string.Join(", ", missing),
                    "Run build-ristretto.bat, then point this at that folder."));
        }

        return checks;
    }
}
