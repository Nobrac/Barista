using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Barista.Services;

/// <summary>
/// What survives a reboot.
///
/// Holds no secrets: folder paths, account names, which stages are done. The
/// TOTP secrets live where they always did, DPAPI-encrypted under the store's
/// own ACL, and this file never sees one.
/// </summary>
public sealed class PersistedState
{
    /// <summary>
    /// Bumped whenever the shape changes. A file from an older version is
    /// discarded rather than guessed at - a half-understood resume is worse
    /// than starting over.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public string? DoppioDir { get; set; }
    public string? RistrettoDir { get; set; }
    public bool InstallCredentialProvider { get; set; }
    public bool InstallAuthPackage { get; set; }
    public bool DenyKeepNetwork { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NetworkPolicy Network { get; set; }

    public List<string> AccountsToEnroll { get; set; } = [];
    public List<string> AccountsToDeny { get; set; } = [];

    /// <summary>Stages already finished, by id.</summary>
    public List<StageId> CompletedStages { get; set; } = [];

    /// <summary>
    /// The stage that was just finished and whose reboot we are coming back
    /// from, if any. Drives the verification question on the resume page.
    /// </summary>
    public StageId? AwaitingVerificationOf { get; set; }

    /// <summary>Everything the log said before the reboot, so the picture stays whole.</summary>
    public string Log { get; set; } = "";

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Where the exe was when it saved this, for the RunOnce entry.</summary>
    public string? LauncherPath { get; set; }

    public SetupState ToSetupState()
    {
        var s = new SetupState
        {
            // The risk acknowledgement is not re-asked on resume: it was given
            // once for this machine and the machine has not become less of a
            // throwaway since. Re-asking would train people to click through it.
            AcceptedThrowawayMachine = true,
            AcceptedSnapshot = true,
            AcceptedRecoveryPlan = true,

            DoppioDir = DoppioDir,
            RistrettoDir = RistrettoDir,
            InstallCredentialProvider = InstallCredentialProvider,
            InstallAuthPackage = InstallAuthPackage,
            DenyKeepNetwork = DenyKeepNetwork,
            Network = Network,
        };
        s.AccountsToEnroll.AddRange(AccountsToEnroll);
        s.AccountsToDeny.AddRange(AccountsToDeny);
        return s;
    }

    public static PersistedState From(SetupState s) => new()
    {
        DoppioDir = s.DoppioDir,
        RistrettoDir = s.RistrettoDir,
        InstallCredentialProvider = s.InstallCredentialProvider,
        InstallAuthPackage = s.InstallAuthPackage,
        DenyKeepNetwork = s.DenyKeepNetwork,
        Network = s.Network,
        AccountsToEnroll = [.. s.AccountsToEnroll],
        AccountsToDeny = [.. s.AccountsToDeny],
    };
}

/// <summary>
/// Saves the run across a reboot and arranges for the tool to come back.
///
/// Two independent mechanisms on purpose. The RunOnce entry is the
/// convenience - it opens the window after the next logon. The state file is
/// the actual record: if RunOnce never fires (the exe was on a share that is
/// gone, someone cleared the key, the machine was booted into something
/// else), starting the tool by hand still finds the pending run and offers to
/// continue. A resume that depends on one registry value is a resume that
/// silently does not happen.
/// </summary>
public static class StateStore
{
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "TheAdminCafeBarista";
    private const int CurrentSchema = 1;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TheAdminCafe", "Barista");

    public static string FilePath => Path.Combine(Folder, "pending-run.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static bool HasPendingRun() => File.Exists(FilePath);

    public static PersistedState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(FilePath), Json);

            if (state is null || state.SchemaVersion != CurrentSchema)
            {
                // A file this version does not understand is cleared rather
                // than acted on. Resuming an install into lsass from a record
                // we are guessing at is not a risk worth taking to save a few
                // clicks.
                Clear();
                return null;
            }
            return state;
        }
        catch
        {
            Clear();
            return null;
        }
    }

    /// <summary>
    /// Writes the record and arms the RunOnce entry. Called immediately
    /// before a reboot, never speculatively - a stale pending run that nobody
    /// asked for is its own small trap.
    /// </summary>
    public static void Save(PersistedState state, bool armRunOnce)
    {
        Directory.CreateDirectory(Folder);
        state.SchemaVersion = CurrentSchema;
        state.LauncherPath ??= Environment.ProcessPath;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));

        if (armRunOnce) ArmRunOnce(state.LauncherPath);
    }

    private static void ArmRunOnce(string? exePath)
    {
        if (exePath is not { Length: > 0 } || !File.Exists(exePath)) return;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RunOnceKey, writable: true);
            // RunOnce deletes the value as it runs it, which is the semantic
            // we want: if the user never continues, nothing lingers in the
            // registry of a machine that may outlive this experiment.
            key?.SetValue(RunOnceValue, $"\"{exePath}\" /resume", RegistryValueKind.String);
        }
        catch
        {
            // Not fatal. The state file is the real record and the tool finds
            // it on an ordinary start; the user just has to open it themselves.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { /* best effort */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RunOnceKey, writable: true);
            if (key?.GetValue(RunOnceValue) is not null) key.DeleteValue(RunOnceValue, false);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Reboots, after the record is on disk. The short delay is so the
    /// confirmation is visibly the last thing that happened, rather than the
    /// screen going black mid-click.
    /// </summary>
    public static void Reboot()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "shutdown.exe",
            ArgumentList = { "/r", "/t", "5", "/c", "Barista: continuing after the restart." },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
