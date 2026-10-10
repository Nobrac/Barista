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
    string? VerifyDetail);

public static class InstallPlan
{
    /// <summary>
    /// Turns a <see cref="SetupState"/> into ordered stages.
    ///
    /// Order inside and between stages is not cosmetic. The tile goes in
    /// before anything denies the other paths, and enrollment happens before
    /// a filter starts refusing enrolled accounts - otherwise the first
    /// reboot can leave an account with no way in. Part 1 says it plainly: a
    /// broken tile plus a denied network logon is a locked machine.
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
                        "Copies TacProvider.dll and enroll.exe to Program Files and registers the "
                        + "provider and its filter.",
                        Path.Combine(doppio, "install-doppio.ps1"),
                        [],
                        StepKind.Script),
                ],
                RebootAfter: true,
                VerifyPrompt: "Did the machine come back, and does the 2FA tile appear on the logon screen?",
                VerifyDetail:
                    "Lock the screen or sign out and look at the tile before continuing. It should "
                    + "ask for a password and a one-time code, and the other sign-in options should "
                    + "be gone.\n\n"
                    + "If it does not look right, stop here. Nothing has been put into lsass yet, so "
                    + "Safe Mode still gets you in and the registration can be removed with "
                    + "unregister.reg."));
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
                "No reboot needed. This has to happen before any filter, so the accounts the "
                + "filter will judge actually have a secret.",
                enrollSteps,
                RebootAfter: false,
                VerifyPrompt: null,
                VerifyDetail: null));
        }

        // --- 3. the non-interactive paths -----------------------------------
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
                            Path.Combine(ristretto, "install-ristretto.ps1"),
                            [],
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

        // --- 4. the authentication package ----------------------------------
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

    /// <summary>The reverse, for the recovery path.</summary>
    public static List<Step> BuildUninstall(SetupState s)
    {
        var steps = new List<Step>();
        var doppio = s.DoppioDir ?? "";
        var ristretto = s.RistrettoDir ?? "";
        var auth0 = Preflight.CurrentAuth0();

        // Deregister in the opposite order, LSA components first: those are
        // the ones that can stop a machine booting, so they go away earliest.
        if (auth0 == "RistrettoFilter" && ristretto.Length > 0)
        {
            steps.Add(new Step(
                "Remove the Ristretto gate",
                "Restores whatever held Auth0 before, clears open gates and removes ristretto.exe.",
                Path.Combine(ristretto, "uninstall-ristretto.ps1"),
                [],
                StepKind.Script));
        }
        else if (auth0 == "TacSubAuth" && doppio.Length > 0)
        {
            steps.Add(new Step(
                "Remove Doppio's sub-authentication filter",
                "Clears the Auth0 registration.",
                Path.Combine(doppio, "uninstall-subauth.ps1"),
                [],
                StepKind.Script));
        }

        if (doppio.Length > 0)
        {
            steps.Add(new Step(
                "Remove the authentication package",
                "Takes TacAuthPackage out of the LSA package list, leaving msv1_0 in place.",
                Path.Combine(doppio, "uninstall-authpackage.ps1"),
                [],
                StepKind.Script));
        }

        return steps;
    }
}
