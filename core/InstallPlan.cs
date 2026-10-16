using System.IO;

namespace Barista.Services;

/// <summary>One thing to run, with a human sentence saying what it does.</summary>
public sealed record Step(
    string Title,
    string Description,
    string Command,
    IReadOnlyList<string> Arguments,
    StepKind Kind);

public enum StepKind
{
    /// <summary>A PowerShell script, output captured into the log.</summary>
    Script,
    /// <summary>A console tool that needs its own window (enroll.exe).</summary>
    Console,
    /// <summary>
    /// A .reg file, imported with reg.exe.
    ///
    /// Needed because the only way back out of the credential provider is
    /// Doppio's unregister.reg, and a .reg file cannot be run by
    /// "powershell -File". Without this kind, the recovery button told people
    /// about unregister.reg and then did not run it.
    /// </summary>
    RegImport,
}

/// <summary>
/// Which stage of the install we are in. Persisted across reboots, so the
/// numbers are part of the on-disk format: insert new stages at the end or
/// bump the schema version.
/// </summary>
public enum StageId
{
    Provider = 1,
    Enrollment = 2,
    NetworkPolicy = 3,
    AuthPackage = 4,

    /// <summary>
    /// Registering the credential provider filter. Appended rather than
    /// inserted between 2 and 3, per the note above - the plan's order comes
    /// from BuildStages, not from these numbers.
    /// </summary>
    ProviderFilter = 5,
}

/// <summary>
/// One reboot's worth of work.
///
/// Part 2 is explicit about this: "Change one thing per reboot. If you
/// register both packages at once and the machine does not come back, you do
/// not know which one did it." A single flat run of every script violates
/// that, so the plan is cut into stages and each one that touches something
/// LSA reads at boot ends with a reboot and a question.
/// </summary>
public sealed record Stage(
    StageId Id,
    string Title,
    string Why,
    IReadOnlyList<Step> Steps,
    bool RebootAfter,
    /// <summary>
    /// What the user has to confirm after the reboot before the next stage
    /// runs. This is the whole point of staging: if the answer is no, the
    /// machine has exactly one new thing on it and you know what it was.
    /// </summary>
    string? VerifyPrompt,
    string? VerifyDetail,
    /// <summary>
    /// Replaces the generic confirmation text before this stage runs. Used
    /// where something has to be true on the machine BEFORE the stage, and no
    /// registry read can establish it - "have you actually signed in with the
    /// tile yet?" being the one that matters.
    /// </summary>
    string? PreRunPrompt = null);

public static class InstallPlan
{
    /// <summary>
    /// Turns a <see cref="SetupState"/> into ordered stages.
    ///
    /// Order inside and between stages is not cosmetic:
    ///
    ///   * The tile is installed before anything denies the other paths.
    ///   * Enrollment happens before any filter, so the accounts a filter will
    ///     judge actually have a secret.
    ///   * The credential provider filter - the thing that removes the
    ///     password tile - goes in BEFORE the network stages, while a network
    ///     logon is still a way back into a machine whose tile turns out to be
    ///     broken. Doing it the other way round means a single bad tile leaves
    ///     no path in at all, which is the locked machine Part 1 describes.
    ///   * The authentication package is last, because it closes nothing.
    /// </summary>
    public static List<Stage> BuildStages(SetupState s)
    {
        var stages = new List<Stage>();
        var doppio = s.DoppioDir ?? "";
        var ristretto = s.RistrettoDir ?? "";

        // --- 1. the tile ----------------------------------------------------
        if (s.InstallCredentialProvider)
        {
            stages.Add(new Stage(
                StageId.Provider,
                "The credential provider",
                "Runs in LogonUI, not lsass. This is the stage that can still be undone from "
                + "Safe Mode, so it goes first and gets its own reboot.",
                [
                    new Step(
                        "Install the credential provider",
                        "install-doppio.ps1, with no switches: copies TacProvider.dll and "
                        + "enroll.exe to Program Files and registers the provider. The other "
                        + "sign-in options deliberately stay visible - the filter that hides them "
                        + "is a separate script run, in its own stage further down.",
                        Path.Combine(doppio, "install-doppio.ps1"),
                        [],
                        StepKind.Script),
                ],
                RebootAfter: true,
                VerifyPrompt: "Did the machine come back, and does the 2FA tile appear on the logon screen?",
                VerifyDetail:
                    "Lock the screen or sign out and look under \"Sign-in options\". The 2FA tile "
                    + "should be there NEXT TO the usual ones - password, PIN, Hello. The other "
                    + "options are supposed to still work at this point; nothing has been hidden "
                    + "yet, and nobody is enrolled yet either, so the tile itself cannot be tested "
                    + "until the next stage.\n\n"
                    + "If the tile is missing altogether, stop here. Nothing has been put into "
                    + "lsass, Safe Mode still gets you in, and the recovery button below imports "
                    + "unregister.reg to remove the registration."));
        }

        // --- 2. enrollment --------------------------------------------------
        // No reboot: enroll.exe is an ordinary user-mode tool and its writes
        // are picked up on the next logon attempt.
        var enrollSteps = new List<Step>();

        foreach (var account in s.AccountsToEnroll)
        {
            enrollSteps.Add(new Step(
                $"Enroll '{account}'",
                "Opens enroll.exe in its own console window. It shows the secret, waits for a "
                + "code from your authenticator app, and only stores anything once that matches.",
                Path.Combine(Preflight.InstallDir, "enroll.exe"),
                [account],
                StepKind.Console));
        }

        if (enrollSteps.Count > 0 && s.Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate)
        {
            enrollSteps.Add(new Step(
                "Rebuild the enrollment index",
                "enroll.exe /reindex. The sub-authentication filter reads the RID index; an "
                + "account enrolled before that index existed would read as not enrolled, and "
                + "its network logons would quietly keep working.",
                Path.Combine(Preflight.InstallDir, "enroll.exe"),
                ["/reindex"],
                StepKind.Console));
        }

        if (enrollSteps.Count > 0)
        {
            stages.Add(new Stage(
                StageId.Enrollment,
                "Enrollment",
                "No reboot needed - enroll.exe is an ordinary user-mode tool. This has to happen "
                + "before any filter, so the accounts a filter will judge actually have a secret.",
                enrollSteps,
                RebootAfter: false,
                VerifyPrompt: null,
                VerifyDetail: null));
        }

        // --- 3. the filter that makes the tile the only option --------------
        // Separately from stage 1 because it is a separate script run with a
        // separate failure mode: stage 1 at worst costs a tile, this one can
        // cost the console.
        if (s.InstallCredentialProvider && s.EnableProviderFilter)
        {
            stages.Add(new Stage(
                StageId.ProviderFilter,
                "Make the 2FA tile the only way in",
                "install-doppio.ps1 -EnableFilter imports register-filter.reg, which hides the "
                + "password, PIN and Hello tiles. Until this runs the tile is only ever offered "
                + "alongside them, so anyone can pick one of those and skip the code entirely. "
                + "It runs before the network stages on purpose: if the tile turns out to be "
                + "broken, a network logon is still a way in.",
                [
                    new Step(
                        "Register the credential provider filter",
                        "The script checks first that every enabled local account has a stored "
                        + "secret, and refuses otherwise - an unenrolled account could not sign in "
                        + "at all once the filter is on, because its tile has nothing to check "
                        + "and the tiles it used before are gone.",
                        Path.Combine(doppio, "install-doppio.ps1"),
                        ["-EnableFilter"],
                        StepKind.Script),
                ],
                RebootAfter: true,
                VerifyPrompt: "Did the machine come back, and can you sign in with only the 2FA tile?",
                VerifyDetail:
                    "NOW the other sign-in options should be gone. The logon and unlock screens "
                    + "should show only 2FA tiles, asking for a password and a one-time code, with "
                    + "no password-only option left anywhere.\n\n"
                    + "If a correct code is refused, the account may be locked from earlier wrong "
                    + "codes. From an elevated prompt: enroll.exe /unlock <user>.\n\n"
                    + "If no tile works at all, this is the locked-console case. The network paths "
                    + "are still open at this point - the network stages come after this one - so "
                    + "SMB, WinRM or RDP may still get you in. Failing that: Safe Mode, or boot "
                    + "WinRE, load the SOFTWARE hive offline and delete the key under "
                    + "Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Provider "
                    + "Filters. That single deletion brings the normal tiles back and leaves "
                    + "everything else alone. Restoring the snapshot also works.",
                PreRunPrompt:
                    "This removes every other way of signing in at the console.\n\n"
                    + "Before you run it, test what is about to become the only option: sign out "
                    + "and sign back in with EACH enrolled account using the 2FA tile. The "
                    + "password tile is still there to catch you if a code does not work - after "
                    + "this stage it is not.\n\n"
                    + "You can close this tool to do that. Every finished stage is written to "
                    + "disk, so starting it again picks up here.\n\n"
                    + "Have you signed in through the 2FA tile with every account that needs to "
                    + "get into this machine?"));
        }

        // --- 4. the non-interactive paths -----------------------------------
        switch (s.Network)
        {
            case NetworkPolicy.DenyRights:
            {
                var steps = new List<Step>();
                foreach (var account in s.AccountsToDeny)
                {
                    var args = new List<string> { account };
                    if (s.DenyKeepNetwork) args.Add("-KeepNetwork");
                    steps.Add(new Step(
                        $"Deny non-interactive logons for '{account}'",
                        s.DenyKeepNetwork
                            ? "Sets the batch and service deny rights. The network right is left "
                              + "alone so RDP with NLA keeps working - SMB and WinRM then still "
                              + "take the password alone for this account."
                            : "Sets the network, batch and service deny rights. No code runs in "
                              + "lsass and nothing here can stop the machine booting.",
                        Path.Combine(doppio, "deny-noninteractive.ps1"),
                        args,
                        StepKind.Script));
                }

                if (steps.Count > 0)
                {
                    stages.Add(new Stage(
                        StageId.NetworkPolicy,
                        "Deny rights",
                        "The supported way. Takes effect at the next logon attempt, no reboot, "
                        + "and nothing of yours runs inside lsass.",
                        steps,
                        RebootAfter: false,
                        VerifyPrompt: null,
                        VerifyDetail: null));
                }
                break;
            }

            case NetworkPolicy.DoppioFilter:
                stages.Add(new Stage(
                    StageId.NetworkPolicy,
                    "Doppio's sub-authentication filter",
                    "First stage that puts an unsigned DLL inside lsass. On its own reboot, so "
                    + "if the machine does not come back you know exactly what caused it.",
                    [
                        new Step(
                            "Install TacSubAuth",
                            "Registers TacSubAuth in the MSV1_0 Auth0 slot. Network logons for "
                            + "enrolled accounts are refused outright, RDP with NLA included.",
                            Path.Combine(doppio, "install-subauth.ps1"),
                            [],
                            StepKind.Script),
                    ],
                    RebootAfter: true,
                    VerifyPrompt: "Did the machine come back, and can you still log on?",
                    VerifyDetail:
                        "This is the reboot that matters. If you are reading this, lsass started "
                        + "with the filter loaded.\n\n"
                        + "Worth checking now, while only one thing has changed: does MSV1_0 "
                        + "actually call the filter on this build of Windows? Microsoft documents "
                        + "Auth0 for domain controllers. Attach a kernel debugger, try a network "
                        + "logon for an enrolled account, and look for a [Doppio-SubAuth] line. No "
                        + "line means the filter is loaded and never asked, and only the deny "
                        + "rights are enforcing anything."));
                break;

            case NetworkPolicy.RistrettoGate:
            {
                var installScript = Path.Combine(ristretto, "install-ristretto.ps1");
                string[] installArgs = AcceptsConfirmedSwitch(installScript) ? ["-Confirmed"] : [];

                stages.Add(new Stage(
                    StageId.NetworkPolicy,
                    "The Ristretto gate",
                    "First stage that puts an unsigned DLL inside lsass. On its own reboot, so "
                    + "if the machine does not come back you know exactly what caused it.",
                    [
                        new Step(
                            "Install RistrettoFilter",
                            "Takes over the Auth0 slot (saving whatever held it) and puts "
                            + "ristretto.exe next to TacProvider.dll. Network logons then need a "
                            + "gate opened with a one-time code within the last few minutes.",
                            installScript,
                            installArgs,
                            StepKind.Script),
                    ],
                    RebootAfter: true,
                    VerifyPrompt: "Did the machine come back, and can you still log on?",
                    VerifyDetail:
                        "This is the reboot that matters. If you are reading this, lsass started "
                        + "with the filter loaded.\n\n"
                        + "Two things to try now, while only one thing has changed. Open a gate "
                        + "with 'ristretto <user>' and check that a share works. Then run "
                        + "'ristretto /close <user>' and check that it stops working. If it keeps "
                        + "working with the gate closed, MSV1_0 is not calling the filter on this "
                        + "build and the gate is decorative."));
                break;
            }
        }

        // --- 5. the authentication package ----------------------------------
        // Last, because it is the one that closes nothing.
        if (s.InstallAuthPackage)
        {
            stages.Add(new Stage(
                StageId.AuthPackage,
                "The authentication package",
                "Closes nothing in practice - it is here to watch the layer load. Last, and on "
                + "its own reboot, because it is the second thing that can stop lsass starting.",
                [
                    new Step(
                        "Install TacAuthPackage",
                        "Appends TacAuthPackage to the LSA package list, leaving msv1_0 in place. "
                        + "It is only ever called by a logon that addresses it by id, which no "
                        + "normal logon does.",
                        Path.Combine(doppio, "install-authpackage.ps1"),
                        [],
                        StepKind.Script),
                ],
                RebootAfter: true,
                VerifyPrompt: "Did the machine come back?",
                VerifyDetail:
                    "If it did, the package loaded. Look for a [Doppio-AP] line in the debugger "
                    + "at boot.\n\n"
                    + "If it did not, this was the cause - which is the entire reason it got its "
                    + "own reboot."));
        }

        return stages;
    }

    /// <summary>
    /// Whether a script declares a -Confirmed switch.
    ///
    /// install-ristretto.ps1 confirms with Read-Host, which throws rather
    /// than prompting under -NonInteractive - and -NonInteractive is how
    /// ProcessRunner starts every script, because a script that stops to ask
    /// something nobody can answer is a hung install. The script therefore
    /// takes -Confirmed, and this is the caller that passes it: by the time a
    /// stage runs, the question has been put three times already (the risk
    /// checkboxes, the stage confirmation, the restart dialog).
    ///
    /// The check exists because the two repositories move independently.
    /// Passing -Confirmed to a copy of the script that predates the switch
    /// fails with "a parameter cannot be found", which would be a worse error
    /// than the one this fixes. Older script, no switch, same behaviour as
    /// before.
    /// </summary>
    private static bool AcceptsConfirmedSwitch(string scriptPath)
    {
        try
        {
            return File.Exists(scriptPath)
                   && File.ReadAllText(scriptPath)
                           .Contains("$Confirmed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unreadable here means unreadable for powershell.exe too, and
            // the step itself reports that properly. Nothing to add.
            return false;
        }
    }

    /// <summary>
    /// The reverse, for the recovery path.
    ///
    /// Built from what is actually registered on this machine, not from what
    /// the user set out to install. Those are different things the moment a
    /// stage fails, and the earlier version of this method used the plan:
    /// after a failed credential-provider stage it offered to remove the
    /// authentication package - which had never been installed - and offered
    /// nothing for the provider, which had. Every branch here therefore asks
    /// the registry first.
    ///
    /// Order is deregistration order: the things that can stop a machine
    /// booting go first, then the things that can stop a logon, then the user
    /// rights, which cannot do either.
    /// </summary>
    public static List<Step> BuildUninstall(SetupState s)
    {
        var steps = new List<Step>();
        var doppio = s.DoppioDir ?? "";
        var ristretto = s.RistrettoDir ?? "";
        var auth0 = Preflight.CurrentAuth0();

        // --- 1. whatever is in the Auth0 slot -------------------------------
        if (auth0 == "RistrettoFilter" && ristretto.Length > 0)
        {
            steps.Add(new Step(
                "Remove the Ristretto gate",
                "Restores whatever held Auth0 before, clears open gates and removes ristretto.exe.",
                Path.Combine(ristretto, "uninstall-ristretto.ps1"),
                [],
                StepKind.Script));

            // uninstall-ristretto.ps1 puts back whatever held Auth0 before.
            // When that was Doppio's TacSubAuth, the slot is not empty after
            // the step above, and enrolled accounts would stay locked out of
            // network logons until somebody ran the uninstall a second time.
            if (Preflight.RistrettoPrevAuth0() == "TacSubAuth" && doppio.Length > 0)
                steps.Add(SubAuthUninstallStep(doppio));
        }
        else if (auth0 == "TacSubAuth" && doppio.Length > 0)
        {
            steps.Add(SubAuthUninstallStep(doppio));
        }

        // --- 2. the authentication package ----------------------------------
        if (doppio.Length > 0 && Preflight.AuthPackageRegistered())
        {
            steps.Add(new Step(
                "Remove the authentication package",
                "Takes TacAuthPackage out of the LSA package list, leaving msv1_0 in place.",
                Path.Combine(doppio, "uninstall-authpackage.ps1"),
                [],
                StepKind.Script));
        }

        // --- 3. the credential provider and its filter ----------------------
        // One import does both, plus the COM registration and the event
        // source. Safe to run when only some of it is registered: the file
        // deletes keys, and reg.exe does not mind a key that is not there.
        var unregister = Path.Combine(doppio, "unregister.reg");
        var providerPresent = Preflight.ProviderFilterRegistered() || Preflight.ProviderRegistered();
        if (doppio.Length > 0 && (providerPresent || s.InstallCredentialProvider)
            && File.Exists(unregister))
        {
            steps.Add(new Step(
                "Remove the credential provider and its filter",
                "Imports unregister.reg: deletes the filter registration first, so the normal "
                + "password tile comes back, then the provider and its COM registration. The "
                + "enrolled secrets under HKLM\\SOFTWARE\\TheAdminCafe are NOT touched - run "
                + "'enroll.exe /purge' by hand if you want those gone too.",
                unregister,
                [],
                StepKind.RegImport));
        }

        // --- 4. the deny rights ---------------------------------------------
        // Last, and separately, because these are the only changes here that
        // outlive an uninstall silently: no DLL, no registry key anybody
        // notices, just an account that cannot reach a share any more.
        var denyScript = Path.Combine(doppio, "deny-noninteractive.ps1");
        if (doppio.Length > 0 && s.Network is NetworkPolicy.DenyRights && File.Exists(denyScript))
        {
            foreach (var account in s.AccountsToDeny)
            {
                steps.Add(new Step(
                    $"Restore non-interactive logons for '{account}'",
                    "deny-noninteractive.ps1 -Remove. Takes the account back out of the "
                    + "'Deny log on' rights it was added to.",
                    denyScript,
                    [account, "-Remove"],
                    StepKind.Script));
            }
        }

        return steps;
    }

    private static Step SubAuthUninstallStep(string doppio) => new(
        "Remove Doppio's sub-authentication filter",
        "Clears the Auth0 registration.",
        Path.Combine(doppio, "uninstall-subauth.ps1"),
        [],
        StepKind.Script);
}
