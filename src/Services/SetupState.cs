namespace Barista.Services;

/// <summary>
/// What the user has chosen to do. Carried from page to page; the install
/// page turns it into a list of scripts to run.
/// </summary>
public sealed class SetupState
{
    // --- risk acknowledgement (page 1) -------------------------------------
    public bool AcceptedThrowawayMachine { get; set; }
    public bool AcceptedSnapshot { get; set; }
    public bool AcceptedRecoveryPlan { get; set; }
    public bool RisksAccepted =>
        AcceptedThrowawayMachine && AcceptedSnapshot && AcceptedRecoveryPlan;

    // --- where the built bits live (page 2) --------------------------------
    public string? DoppioDir { get; set; }
    public string? RistrettoDir { get; set; }

    // --- what to install (page 3) ------------------------------------------
    public bool InstallCredentialProvider { get; set; } = true;

    /// <summary>
    /// How non-interactive paths are handled. These are genuinely exclusive:
    /// the two filters both want the single MSV1_0 Auth0 slot, and the deny
    /// rights are the supported alternative that runs no code in lsass.
    /// </summary>
    public NetworkPolicy Network { get; set; } = NetworkPolicy.DenyRights;

    /// <summary>
    /// Doppio's authentication package. Part 2 is explicit that it closes
    /// nothing in practice - it is here to be read, not relied on - so it is
    /// off by default and labelled as such in the UI.
    /// </summary>
    public bool InstallAuthPackage { get; set; }

    /// <summary>Accounts to run enroll.exe for, in order.</summary>
    public List<string> AccountsToEnroll { get; } = [];

    /// <summary>Accounts to apply deny-noninteractive.ps1 to.</summary>
    public List<string> AccountsToDeny { get; } = [];

    /// <summary>Keep the network deny right off so RDP with NLA still works.</summary>
    public bool DenyKeepNetwork { get; set; }

    public bool NeedsReboot =>
        InstallCredentialProvider
        || InstallAuthPackage
        || Network is NetworkPolicy.DoppioFilter or NetworkPolicy.RistrettoGate;
}

public enum NetworkPolicy
{
    /// <summary>Change nothing about non-interactive logons.</summary>
    Leave,

    /// <summary>
    /// "Deny log on ..." user rights. Supported, no code in lsass, cannot
    /// bugcheck anything. The right answer for a machine you care about.
    /// </summary>
    DenyRights,

    /// <summary>Doppio's TacSubAuth: refuse every network logon for enrolled accounts.</summary>
    DoppioFilter,

    /// <summary>Ristretto: refuse unless a TOTP gate was opened recently.</summary>
    RistrettoGate,
}
