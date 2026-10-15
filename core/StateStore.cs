using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Barista.Services;

/// <summary>
/// What survives a reboot.
///
/// An envelope around a <see cref="SetupState"/> plus where the run got to.
/// The choices themselves are not re-declared here: this class used to carry a
/// copy of every field in SetupState, with a From() and a ToSetupState() to
/// move them across and a third copy in the install page's save method. Adding
/// a choice meant remembering four places, and forgetting one of them did not
/// fail to compile - the field simply deserialised to its default, which for a
/// bool is false. That is how a resume can finish an install while quietly
/// skipping the half that enforces anything, which is exactly what the schema
/// version below exists to prevent.
///
/// Holds no secrets: folder paths, account names, which stages are done. The
/// TOTP secrets live where they always did, DPAPI-encrypted under the store's
/// own ACL, and this file never sees one. The risk acknowledgement is not in
/// here either - see the note on those properties in SetupState.
/// </summary>
public sealed class PersistedState
{
    /// <summary>
    /// Bumped whenever the shape changes. A file from an older version is
    /// discarded rather than guessed at - a half-understood resume is worse
    /// than starting over.
    ///
    /// 2: added EnableProviderFilter and the ProviderFilter stage. A version 1
    /// record would deserialise with the flag defaulting to false and silently
    /// skip that stage on resume, finishing an install that never enforces
    /// anything.
    ///
    /// 3: the choices moved into a nested <see cref="Setup"/> object instead of
    /// being repeated as top-level fields. A version 2 file has them at the top
    /// level, so every one of them would read as its default here - the same
    /// failure as above, which is why this is a bump and not a tolerated shape.
    /// </summary>
    public int SchemaVersion { get; set; } = StateStore.CurrentSchema;

    /// <summary>
    /// The choices, exactly as the wizard holds them. One choice, one place.
    /// </summary>
    public SetupState Setup { get; set; } = new();

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

    /// <summary>
    /// The choices to carry on with.
    ///
    /// Returns the deserialised object itself rather than a copy, so the window
    /// and this record go on sharing one <see cref="SetupState"/> and a later
    /// save cannot write back a stale set of answers.
    ///
    /// The risk acknowledgement is granted here, not read from the file: see
    /// the note on those properties.
    /// </summary>
    public SetupState ToSetupState()
    {
        Setup.AcceptedThrowawayMachine = true;
        Setup.AcceptedSnapshot = true;
        Setup.AcceptedRecoveryPlan = true;
        return Setup;
    }

    public static PersistedState From(SetupState s) => new() { Setup = s };
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

    public const int CurrentSchema = 3;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TheAdminCafe", "Barista");

    public static string FilePath => Path.Combine(Folder, "pending-run.json");

    /// <summary>
    /// Creates the folder, or takes an existing one over, with an ACL only
    /// SYSTEM and Administrators can change. See <see cref="ProtectedFolder.Lock"/>:
    /// without it, any user could pre-create the folder and leave a record
    /// here for the elevated wizard to resume from.
    /// </summary>
    public static void EnsureFolder() => ProtectedFolder.Lock(Folder);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static bool HasPendingRun() => File.Exists(FilePath);

    /// <summary>
    /// The record as it goes on disk. Separate from <see cref="Save"/> so the
    /// round-trip can be tested without a registry or a ProgramData folder.
    /// </summary>
    public static string Serialize(PersistedState state)
    {
        state.SchemaVersion = CurrentSchema;
        return JsonSerializer.Serialize(state, Json);
    }

    /// <summary>
    /// The reverse, with the schema gate. Null means "do not act on this" -
    /// unreadable, or written by a version whose shape this one does not know.
    /// </summary>
    public static PersistedState? Deserialize(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(json, Json);
            if (state is null || state.SchemaVersion != CurrentSchema) return null;

            // A file that parsed but carries no choices at all is a truncated
            // or hand-edited record ("Setup": null). Resuming an install into
            // lsass from one would run the plan for an empty SetupState, which
            // is a plan for nothing - better to start over visibly. Through a
            // nullable local because the property's type says it cannot be
            // null and the deserialiser does not care what the type says.
            SetupState? setup = state.Setup;
            return setup is null ? null : state;
        }
        catch
        {
            return null;
        }
    }

    public static PersistedState? Load()
    {
        try
        {
            if (!Directory.Exists(Folder)) return null;
            EnsureFolder();
            if (!File.Exists(FilePath)) return null;

            var state = Deserialize(File.ReadAllText(FilePath));
            if (state is null)
            {
                // Cleared rather than acted on. Resuming an install into lsass
                // from a record we are guessing at is not a risk worth taking
                // to save a few clicks.
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
    /// Writes the record, and arms the RunOnce entry when a reboot is coming.
    ///
    /// Called after every finished stage, not only before a reboot. The
    /// original rule was "never speculatively", on the grounds that a stale
    /// pending run is its own small trap - but the cost of that turned out to
    /// be worse than the trap. A stage with no reboot of its own (enrollment,
    /// the deny rights) was finished in memory and nowhere else, so closing
    /// the window after it lost the fact that it had run. The next start then
    /// offered to continue from the stage before, and re-ran enroll.exe for an
    /// account that already had a secret - which, confirmed at the "already
    /// enrolled, replace?" prompt, issues a NEW secret and silently invalidates
    /// the authenticator entry the user had just set up.
    ///
    /// <paramref name="armRunOnce"/> is what keeps the trap small: the record
    /// is written either way, but nothing opens by itself unless a reboot is
    /// actually pending.
    /// </summary>
    public static void Save(PersistedState state, bool armRunOnce)
    {
        EnsureFolder();
        File.WriteAllText(FilePath, Serialize(state));

        // The exe that is running now, never a path read back from the record:
        // RunOnce under HKLM starts it as administrator at the next logon.
        if (armRunOnce) ArmRunOnce(Environment.ProcessPath);
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
            FileName = SystemTools.Shutdown,
            ArgumentList = { "/r", "/t", "5", "/c", "Barista: continuing after the restart." },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}

/// <summary>
/// The one thing <see cref="StageRunner"/> needs from the store, behind an
/// interface so a test can watch what gets written without a registry or a
/// ProgramData folder. The real implementation is three lines; the point is
/// that "every finished stage is written down, and only a pending reboot arms
/// RunOnce" becomes an assertion instead of a comment.
/// </summary>
public interface IPendingRunStore
{
    void Save(PersistedState state, bool armRunOnce);
}

/// <inheritdoc cref="IPendingRunStore"/>
public sealed class PendingRunStore : IPendingRunStore
{
    public static readonly PendingRunStore Instance = new();

    public void Save(PersistedState state, bool armRunOnce) => StateStore.Save(state, armRunOnce);
}
