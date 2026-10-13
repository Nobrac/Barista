using System.Text.Json.Serialization;

namespace Barista.Services;

/// <summary>
/// What the user has chosen to do. Carried from page to page; the install
/// page turns it into a list of scripts to run.
///
/// This is also, directly, what gets written to the pending-run record - see
/// <see cref="PersistedState"/>. It used to be copied field by field into a
/// parallel class and back, which meant a new choice had to be added in four
/// places and a forgotten one deserialised silently to false. Serialising this
/// object itself makes one choice one place.
/// </summary>
public sealed class SetupState
{
    // --- risk acknowledgement (page 1) -------------------------------------
    // Not persisted, on purpose. These are a consent given by a person looking
    // at a screen, and a consent restored from a file is not one. The resume
    // path sets them instead, because the question was answered once for this
    // machine and the machine has not become less of a throwaway since -
    // re-asking across every reboot is how people learn to click through it.
    [JsonIgnore] public bool AcceptedThrowawayMachine { get; set; }
    [JsonIgnore] public bool AcceptedSnapshot { get; set; }
    [JsonIgnore] public bool AcceptedRecoveryPlan { get; set; }

    [JsonIgnore]
    public bool RisksAccepted =>
        AcceptedThrowawayMachine && AcceptedSnapshot && AcceptedRecoveryPlan;

    // --- where the built bits live (page 2) --------------------------------
    public string? DoppioDir { get; set; }
    public string? RistrettoDir { get; set; }

    // --- what to install (page 3) ------------------------------------------
    public bool InstallCredentialProvider { get; set; } = true;

    /// <summary>
    /// Whether to register the credential provider FILTER - the thing that
    /// hides every other sign-in option.
    ///
    /// This is a separate choice because it is a separate script invocation
    /// (install-doppio.ps1 -EnableFilter) and a separate risk. Without it the
    /// tile is only ever offered alongside the password tile, so, as Part 1
    /// puts it, "you could choose the normal password tile and skip the code" -
    /// the 2FA is decorative. With it, a tile that does not work is a console
    /// nobody can log in to.
    ///
    /// On by default, because an installer that leaves the enforcement half
    /// out while reporting success is the worse of the two failures.
    /// </summary>
    public bool EnableProviderFilter { get; set; } = true;

    /// <summary>
    /// How non-interactive paths are handled. These are genuinely exclusive:
    /// the two filters both want the single MSV1_0 Auth0 slot, and the deny
    /// rights are the supported alternative that runs no code in lsass.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NetworkPolicy Network { get; set; } = NetworkPolicy.DenyRights;

    /// <summary>
    /// Doppio's authentication package. Part 2 is explicit that it closes
    /// nothing in practice - it is here to be read, not relied on - so it is
    /// off by default and labelled as such in the UI.
    /// </summary>
    public bool InstallAuthPackage { get; set; }

    /// <summary>
    /// Accounts to run enroll.exe for, in order.
    ///
    /// Settable, not get-only: System.Text.Json does not populate a read-only
    /// collection property, so a get-only list would round-trip as empty and a
    /// resumed run would enroll nobody.
    /// </summary>
    public List<string> AccountsToEnroll { get; set; } = [];

    /// <summary>Accounts to apply deny-noninteractive.ps1 to.</summary>
    public List<string> AccountsToDeny { get; set; } = [];

    /// <summary>Keep the network deny right off so RDP with NLA still works.</summary>
    public bool DenyKeepNetwork { get; set; }

    [JsonIgnore]
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
