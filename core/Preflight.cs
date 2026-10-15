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
/// Everything the install scripts refuse to run with, read before anything is
/// changed.
///
/// The rule this class follows: a condition one of the install scripts throws
/// on must be visible here, BEFORE the risk page and before the RunAsPPL
/// dialog. Discovering a hard refusal halfway through a stage is bad on its
/// own, but the real cost is the order it happens in - the user has by then
/// already turned lsass protection off, which is a lasting downgrade, for an
/// install that was never going to complete.
/// </summary>
public static class Preflight
{
    public const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";
    public const string MsvKey = @"SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0";
    public const string DoppioKey = @"SOFTWARE\TheAdminCafe\2FA";
    public const string RistrettoKey = @"SOFTWARE\TheAdminCafe\Ristretto";
    public const string InstallDir = @"C:\Program Files\TheAdminCafe";

    public const string ProviderRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";
    public const string FilterRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters";

    private const string AuthPackagesValue = "Authentication Packages";

    // --- what each choice needs on disk -------------------------------------
    // One list per component rather than one list per page. The binaries page
    // and this class used to carry their own, slightly different, versions of
    // "what Doppio needs", which meant a folder could satisfy one and not the
    // other.

    /// <summary>Needed for any Doppio install at all.</summary>
    public static readonly string[] DoppioCoreFiles =
        ["TacProvider.dll", "enroll.exe", "register.reg", "install-doppio.ps1"];

    /// <summary>Needed to turn the tile into the only sign-in option.</summary>
    public static readonly string[] DoppioFilterFiles = ["register-filter.reg"];

    /// <summary>Needed to take the provider back out again.</summary>
    public static readonly string[] DoppioRecoveryFiles = ["unregister.reg"];

    public static readonly string[] DoppioSubAuthFiles = ["TacSubAuth.dll", "install-subauth.ps1"];
    public static readonly string[] DoppioSubAuthRecoveryFiles = ["uninstall-subauth.ps1"];

    public static readonly string[] DoppioAuthPackageFiles =
        ["TacAuthPackage.dll", "install-authpackage.ps1"];
    public static readonly string[] DoppioAuthPackageRecoveryFiles = ["uninstall-authpackage.ps1"];

    public static readonly string[] DoppioDenyFiles = ["deny-noninteractive.ps1"];

    public static readonly string[] RistrettoFiles =
        ["RistrettoFilter.dll", "ristretto.exe", "install-ristretto.ps1"];
    public static readonly string[] RistrettoRecoveryFiles = ["uninstall-ristretto.ps1"];

    /// <summary>
    /// The subset of the lists above that a build actually produces. Anything
    /// else missing - a .reg file, a .ps1 - means the folder is not a complete
    /// checkout, and pointing at the Build button would send the user to run a
    /// compiler that was never going to create it.
    /// </summary>
    public static readonly string[] DoppioBuildOutputs =
        ["TacProvider.dll", "enroll.exe", "TacSubAuth.dll", "TacAuthPackage.dll"];

    public static readonly string[] RistrettoBuildOutputs =
        ["RistrettoFilter.dll", "ristretto.exe"];

    /// <summary>Whether any of <paramref name="missing"/> is something a build would create.</summary>
    public static bool AnyIsBuildOutput(IEnumerable<string> missing, IEnumerable<string> buildOutputs)
        => missing.Any(m => buildOutputs.Contains(m, StringComparer.OrdinalIgnoreCase));

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // --- lsass protection ---------------------------------------------------

    /// <summary>
    /// Whether lsass will load an unsigned DLL, from all three places that
    /// decide it.
    ///
    /// <see cref="RunAsPpl"/> alone is not the answer, and reading only that
    /// was the bug this record exists to fix. Every install script in both
    /// repositories gates on <c>RunAsPPL</c> OR <c>RunAsPPLBoot</c>, and
    /// install-ristretto.ps1 additionally looks for WinInit event 12 since the
    /// last boot - because Windows 11 22H2 and later can turn LSA protection
    /// on for enterprise-joined, HVCI-capable devices without that showing up
    /// in either value.
    /// </summary>
    public sealed record PplState(int? RunAsPpl, int? RunAsPplBoot, bool? ProtectedSinceBoot)
    {
        /// <summary>True when an unsigned DLL would be refused by lsass.</summary>
        public bool BlocksUnsignedLoad =>
            RunAsPpl is 1 or 2 || RunAsPplBoot is 1 or 2 || ProtectedSinceBoot == true;

        /// <summary>
        /// True when the registry alone looks clear but the running kernel
        /// says otherwise. Worth saying out loud, because clearing the
        /// registry value will not fix it and the user will otherwise keep
        /// trying.
        /// </summary>
        public bool ProtectedDespiteRegistry =>
            ProtectedSinceBoot == true && RunAsPpl is null or 0 && RunAsPplBoot is null or 0;

        public string Describe()
        {
            var parts = new List<string>
            {
                RunAsPpl is null ? "RunAsPPL not set" : $"RunAsPPL={RunAsPpl}",
            };
            if (RunAsPplBoot is not null) parts.Add($"RunAsPPLBoot={RunAsPplBoot}");
            parts.Add(ProtectedSinceBoot switch
            {
                true => "lsass is running protected (WinInit event 12)",
                false => "lsass is not running protected",
                null => "could not read the System event log",
            });
            return string.Join(", ", parts);
        }
    }

    public static int? RunAsPpl()
        => Registry.LocalMachine.OpenSubKey(LsaKey)?.GetValue("RunAsPPL") as int?;

    public static int? RunAsPplBoot()
        => Registry.LocalMachine.OpenSubKey(LsaKey)?.GetValue("RunAsPPLBoot") as int?;

    /// <summary>
    /// Reads the full picture, including the event log. The event log read
    /// shells out to PowerShell for the same reason <see cref="LocalUserNames"/>
    /// does: it keeps this assembly free of a System.Diagnostics.EventLog
    /// dependency, and it runs exactly the query install-ristretto.ps1 runs,
    /// so the two cannot disagree about the same machine.
    /// </summary>
    public static PplState ReadPplState()
        => new(RunAsPpl(), RunAsPplBoot(), LsassProtectedSinceBoot());

    /// <summary>
    /// Null means "could not tell" - a missing or unreadable System log, or a
    /// PowerShell that did not start. Null is deliberately not treated as
    /// protected: it would block the install on a machine that is probably
    /// fine, and the install script performs the same check again anyway.
    /// </summary>
    private static bool? LsassProtectedSinceBoot()
    {
        // The provider filter matters: event ID 12 is used by several sources.
        // The message match on top of it is this tool's own addition - WinInit
        // logs other ID 12 events, and a false "protected" here would block an
        // install for no reason.
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
            if (-not $boot) { 'UNKNOWN'; exit }
            $e = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 12; StartTime = $boot } |
                 Where-Object { $_.ProviderName -like '*Wininit*' -and $_.Message -like '*protected process*' } |
                 Select-Object -First 1
            if ($e) { 'PROTECTED' } else { 'CLEAR' }
            """;

        foreach (var line in RunPowerShell(script))
        {
            if (line.Equals("PROTECTED", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.Equals("CLEAR", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
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
    ///
    /// Both values are written, not just <c>RunAsPPL</c>. Writing one and
    /// leaving the other is what made the button report success on a machine
    /// where the install then still could not proceed.
    /// </summary>
    public static void SetRunAsPpl(int value)
    {
        using var lsa = Registry.LocalMachine.OpenSubKey(LsaKey, writable: true)
            ?? throw new InvalidOperationException($@"Cannot open HKLM\{LsaKey} for writing.");

        lsa.SetValue("RunAsPPL", value, RegistryValueKind.DWord);

        if (value == 0)
        {
            // Only written when it is actually in the way. Creating the value
            // on a machine that never had it would be this tool leaving a mark
            // of its own on the Lsa key.
            if (lsa.GetValue("RunAsPPLBoot") is int boot && boot != 0)
                lsa.SetValue("RunAsPPLBoot", 0, RegistryValueKind.DWord);
        }
        else
        {
            // Hand the boot-time decision back to Windows rather than pinning
            // it to the 0 written above.
            if (lsa.GetValue("RunAsPPLBoot") is int boot && boot == 0)
            {
                try { lsa.DeleteValue("RunAsPPLBoot", throwOnMissingValue: false); }
                catch { /* leaving it at 0 is not worth failing the restore for */ }
            }
        }
    }

    // --- what is registered right now ---------------------------------------

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
    /// Whether TacAuthPackage is in the LSA package list.
    ///
    /// Used so the uninstall offers the authentication package only when it is
    /// actually registered. Offering it unconditionally meant that the
    /// recovery button after a failed credential-provider stage ran one script
    /// for something that had never been installed, and nothing at all for the
    /// thing that had.
    /// </summary>
    public static bool AuthPackageRegistered()
    {
        using var lsa = Registry.LocalMachine.OpenSubKey(LsaKey);
        return lsa?.GetValue(AuthPackagesValue) is string[] packages
               && packages.Any(p => p.Equals("TacAuthPackage", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether this project's credential provider is registered.</summary>
    public static bool ProviderRegistered() => OwnedKeyExistsUnder(ProviderRoot);

    /// <summary>
    /// Whether the filter that hides every other sign-in option is registered.
    /// This is the one that can lock a console out.
    /// </summary>
    public static bool ProviderFilterRegistered() => OwnedKeyExistsUnder(FilterRoot);

    /// <summary>
    /// Looks for a CLSID under <paramref name="root"/> whose default value
    /// names this project.
    ///
    /// Deliberately not a hard-coded GUID: new-guids.ps1 exists precisely so
    /// that people do not ship someone else's CLSIDs, and a check keyed to the
    /// GUIDs in the repository would quietly stop working the moment it was
    /// used as intended. The ownership strings are the same ones
    /// install-doppio.ps1 matches on.
    /// </summary>
    private static bool OwnedKeyExistsUnder(string root)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) return false;

            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue(null) is string owner
                    && (owner.Contains("AdminCafe", StringComparison.OrdinalIgnoreCase)
                        || owner.Contains("Doppio", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch
        {
            // Unreadable means "do not claim it is there". The uninstall falls
            // back on what the user asked to install.
        }
        return false;
    }

    /// <summary>
    /// Workstation, member server or domain controller, from the registry
    /// rather than WMI - one less dependency, and it is the same answer
    /// Win32_ComputerSystem.DomainRole gives for the only distinction that
    /// matters here.
    /// </summary>
    public static bool IsDomainController()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return key?.GetValue("ProductType") is string type
               && type.Equals("LanmanNT", StringComparison.OrdinalIgnoreCase);
    }

    // --- accounts -----------------------------------------------------------

    /// <summary>Local, enabled user accounts, for the enrollment page.</summary>
    public static IReadOnlyList<string> LocalUserNames()
        => RunPowerShell("Get-LocalUser | Where-Object Enabled | Select-Object -ExpandProperty Name");

    /// <summary>
    /// Enabled local accounts with no stored secret.
    ///
    /// This is the exact check install-doppio.ps1 -EnableFilter refuses on, run
    /// early so the user hears about it before the filter stage rather than
    /// from a throw in the middle of it. Same source of truth too: the
    /// REG_BINARY values named by SID under the store, resolved against
    /// Get-LocalUser.
    /// </summary>
    public static IReadOnlyList<string> UnenrolledEnabledAccounts()
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $store = 'HKLM:\SOFTWARE\TheAdminCafe\2FA'
            $enrolled = @()
            if (Test-Path $store) {
                $item = Get-Item -Path $store
                $enrolled = @($item.Property | Where-Object {
                    $_ -like 'S-1-*' -and
                    $item.GetValueKind($_) -eq [Microsoft.Win32.RegistryValueKind]::Binary
                })
            }
            Get-LocalUser |
                Where-Object { $_.Enabled -and ($enrolled -notcontains $_.SID.Value) } |
                Select-Object -ExpandProperty Name
            """;
        return RunPowerShell(script);
    }

    /// <summary>
    /// Runs a snippet and returns its non-empty stdout lines. Any failure is a
    /// cosmetic loss: every caller here degrades to a weaker check or a
    /// free-text box, and the install scripts validate properly regardless.
    /// </summary>
    private static List<string> RunPowerShell(string script)
    {
        var lines = new List<string>();
        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = SystemTools.PowerShell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            p.StartInfo.ArgumentList.Add("-NoProfile");
            p.StartInfo.ArgumentList.Add("-NonInteractive");
            p.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            p.StartInfo.ArgumentList.Add("Bypass");
            p.StartInfo.ArgumentList.Add("-Command");
            p.StartInfo.ArgumentList.Add(script);

            p.Start();
            while (p.StandardOutput.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) lines.Add(trimmed);
            }
            p.WaitForExit(20_000);
        }
        catch
        {
            // See the summary.
        }
        return lines;
    }

    private static string[] MissingFrom(string? folder, IEnumerable<string> files)
        => folder is { Length: > 0 } && Directory.Exists(folder)
            ? files.Where(f => !File.Exists(Path.Combine(folder, f))).ToArray()
            : files.ToArray();

    // --- the checks ---------------------------------------------------------

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
    /// <param name="ppl">
    /// A pre-read lsass-protection state. The caller passes one when it has
    /// already read it on a background thread, so the Get-WinEvent lookup
    /// inside <see cref="ReadPplState"/> happens once per check rather than
    /// once here and again for whoever needs the same answer to paint a
    /// button.
    /// </param>
    public static List<Check> Run(SetupState state, PplState? ppl = null)
    {
        var sourceDir = state.DoppioDir;
        var ristrettoDir = state.RistrettoDir;
        var wantsSubAuth = state.Network is NetworkPolicy.DoppioFilter;
        var wantsRistretto = state.Network is NetworkPolicy.RistrettoGate;
        var slotWanted = wantsSubAuth || wantsRistretto;
        var needsLsa = slotWanted || state.InstallAuthPackage;

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

        // Both filter installers refuse outright on a domain controller: the
        // RID index only knows local accounts. Finding that out here costs a
        // page; finding it out at stage time costs a page AND the RunAsPPL
        // downgrade that was taken to get there.
        if (slotWanted)
        {
            checks.Add(IsDomainController()
                ? new Check("Machine role", CheckLevel.Block,
                    "This is a domain controller.",
                    "Both filters refuse to install here - on a DC the Auth0 slot sees domain "
                    + "accounts, whose RIDs this project's local index knows nothing about. Use a "
                    + "workstation or member server, or choose the deny rights.")
                : new Check("Machine role", CheckLevel.Pass,
                    "Not a domain controller, which is what the filters require."));
        }

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

        ppl ??= ReadPplState();
        if (!needsLsa)
        {
            checks.Add(new Check("lsass protection", CheckLevel.Pass,
                ppl.BlocksUnsignedLoad
                    ? $"On, and it can stay on ({ppl.Describe()})."
                    : $"Off ({ppl.Describe()}).",
                ppl.BlocksUnsignedLoad
                    ? "Nothing you have chosen loads into lsass, so lsass protection does not "
                      + "need to be touched. Leave it on."
                    : null));
        }
        else if (!ppl.BlocksUnsignedLoad)
        {
            checks.Add(new Check("lsass protection", CheckLevel.Pass,
                $"Off, so lsass will load an unsigned DLL ({ppl.Describe()})."));
        }
        else if (ppl.ProtectedDespiteRegistry)
        {
            // The case the blog warns about and the registry does not show.
            checks.Add(new Check("lsass protection", CheckLevel.Block,
                "lsass is running as a protected process even though the registry values look "
                + $"clear ({ppl.Describe()}).",
                "This is the Windows 11 22H2+ case: LSA protection can be on for an "
                + "enterprise-joined, HVCI-capable device without appearing in RunAsPPL. Clearing "
                + "the registry value will not help on its own. If RunAsPPL was UEFI-locked you "
                + "need Microsoft's LSA Protected Process Opt-out tool (LsaPplConfig.efi); "
                + "otherwise use a plain, non-joined test VM - or choose the deny rights, which "
                + "need none of this."));
        }
        else
        {
            checks.Add(new Check("lsass protection", CheckLevel.Block,
                $"On, so lsass will silently refuse the unsigned DLLs ({ppl.Describe()}).",
                "This has to be off for the components you picked. It is a real downgrade - "
                + "see the button below, which explains what you are giving up. If you would "
                + "rather not, go back and choose the deny rights instead."));
        }

        var auth0 = CurrentAuth0();
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

        // The filter stage refuses while an enabled local account has no
        // secret, because that account could not sign in at all afterwards.
        // Said here, with names, while it is still cheap to fix.
        if (state.InstallCredentialProvider && state.EnableProviderFilter)
        {
            var unenrolled = UnenrolledEnabledAccounts()
                .Where(n => !state.AccountsToEnroll.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            checks.Add(unenrolled.Length == 0
                ? new Check("Accounts that could be locked out", CheckLevel.Pass,
                    "Every enabled local account is enrolled or selected for enrollment.")
                : new Check("Accounts that could be locked out", CheckLevel.Warn,
                    "Enabled local accounts with no second factor and none selected: "
                    + string.Join(", ", unenrolled),
                    "Once the tile is the only sign-in option, these accounts cannot sign in at "
                    + "the console or over RDP. install-doppio.ps1 -EnableFilter refuses while "
                    + "that is true, so the filter stage will stop. Select them on the accounts "
                    + "page, disable them, or leave the filter off."));
        }

        // --- build output -----------------------------------------------------
        // Per component, not one list for everything: someone installing only
        // the deny rights should not be blocked on an LSA DLL they will never
        // load.
        if (sourceDir is { Length: > 0 })
        {
            AddFileCheck(checks, "Doppio build output", sourceDir, DoppioCoreFiles, CheckLevel.Block,
                Remedy(sourceDir, "build.bat",
                    "Run build.bat from the x64 Native Tools Command Prompt, then point this at "
                    + "that folder.",
                    IncompleteBundle));

            if (state.InstallCredentialProvider && state.EnableProviderFilter)
                AddFileCheck(checks, "Filter registration file", sourceDir, DoppioFilterFiles,
                    CheckLevel.Block,
                    "register-filter.reg ships with Doppio and is what makes the tile the only "
                    + "sign-in option. Without it the filter stage cannot run.");

            if (state.InstallCredentialProvider)
                AddFileCheck(checks, "Provider rollback file", sourceDir, DoppioRecoveryFiles,
                    CheckLevel.Warn,
                    "unregister.reg is what this tool imports to take the provider and its filter "
                    + "back out. Without it, a bad tile has to be undone by hand from Safe Mode.");

            if (wantsSubAuth)
            {
                AddFileCheck(checks, "TacSubAuth build output", sourceDir, DoppioSubAuthFiles,
                    CheckLevel.Block,
                    Remedy(sourceDir, "build.bat",
                        "build.bat builds TacSubAuth.dll alongside the provider. Run it from the "
                        + "x64 Native Tools Command Prompt, or use build-lsa.bat for just the LSA "
                        + "pieces.",
                        IncompleteBundle));
                AddFileCheck(checks, "TacSubAuth rollback script", sourceDir,
                    DoppioSubAuthRecoveryFiles, CheckLevel.Warn,
                    "Without uninstall-subauth.ps1 the Auth0 registration has to be cleared by "
                    + "hand - offline from WinRE if the machine stops booting.");
            }

            if (state.InstallAuthPackage)
            {
                AddFileCheck(checks, "TacAuthPackage build output", sourceDir,
                    DoppioAuthPackageFiles, CheckLevel.Block,
                    Remedy(sourceDir, "build.bat",
                        "build.bat builds TacAuthPackage.dll alongside the provider. Run it from "
                        + "the x64 Native Tools Command Prompt, or use build-lsa.bat.",
                        IncompleteBundle));
                AddFileCheck(checks, "TacAuthPackage rollback script", sourceDir,
                    DoppioAuthPackageRecoveryFiles, CheckLevel.Warn,
                    "Without uninstall-authpackage.ps1 the package list has to be edited by hand.");
            }

            if (state.Network is NetworkPolicy.DenyRights)
                AddFileCheck(checks, "Deny-rights script", sourceDir, DoppioDenyFiles,
                    CheckLevel.Block,
                    "deny-noninteractive.ps1 ships with Doppio and is what sets and removes the "
                    + "'Deny log on' rights.");
        }
        else
        {
            checks.Add(new Check("Doppio build output", CheckLevel.Block, "No folder selected.",
                "Pick the folder holding the built Doppio binaries and install scripts."));
        }

        if (wantsRistretto)
        {
            if (ristrettoDir is { Length: > 0 })
            {
                AddFileCheck(checks, "Ristretto build output", ristrettoDir, RistrettoFiles,
                    CheckLevel.Block,
                    Remedy(ristrettoDir, "build-ristretto.bat",
                        "Run build-ristretto.bat, then point this at that folder.",
                        IncompleteBundle));
                AddFileCheck(checks, "Ristretto rollback script", ristrettoDir,
                    RistrettoRecoveryFiles, CheckLevel.Warn,
                    "uninstall-ristretto.ps1 is what restores the previous Auth0 holder. Without "
                    + "it, the slot has to be put back by hand.");
            }
            else
            {
                checks.Add(new Check("Ristretto build output", CheckLevel.Block,
                    "Ristretto is selected but no folder was chosen.",
                    "Go back to the binaries page and point this at the Ristretto folder."));
            }
        }
        else if (ristrettoDir is { Length: > 0 })
        {
            AddFileCheck(checks, "Ristretto build output", ristrettoDir, RistrettoFiles,
                CheckLevel.Warn,
                "Not needed for what you picked - Ristretto's own stage is not in the plan.");
        }

        return checks;
    }

    /// <summary>
    /// Advice for a missing binary, which is not the same sentence in both
    /// places this tool runs.
    ///
    /// In a source checkout the answer is "build it", and the build script is
    /// right there. In a packaged bundle - Barista.exe beside folders of
    /// already-built binaries - there is no build script on purpose, and the
    /// machine it is running on usually has no compiler either. Telling that
    /// person to open the x64 Native Tools Command Prompt sends them off to
    /// install several gigabytes of Visual Studio to work around somebody
    /// else's packaging mistake. The folder itself says which case this is.
    /// </summary>
    private static string Remedy(string? folder, string buildScript, string ifSource, string ifBundle)
        => folder is { Length: > 0 } && File.Exists(Path.Combine(folder, buildScript))
            ? ifSource
            : ifBundle;

    private const string IncompleteBundle =
        "This folder has no build script in it, so it is a packaged copy rather than a checkout - "
        + "the file should have been in it. Ask whoever packaged it for a complete one; nothing "
        + "here can be rebuilt without a C++ compiler.";

    private static void AddFileCheck(
        List<Check> checks, string name, string folder, string[] files,
        CheckLevel levelIfMissing, string remedy)
    {
        var missing = MissingFrom(folder, files);
        checks.Add(missing.Length == 0
            ? new Check(name, CheckLevel.Pass, $"Found in {folder}.")
            : new Check(name, levelIfMissing, "Missing: " + string.Join(", ", missing), remedy));
    }

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
}
