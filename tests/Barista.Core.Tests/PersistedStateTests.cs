using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// The pending-run record: what goes in it, what deliberately does not, and
/// what happens to a file this version does not understand.
///
/// These go through <see cref="StateStore.Serialize"/> and
/// <see cref="StateStore.Deserialize"/>, which is the same path the real file
/// takes, without needing a ProgramData folder or the registry.
/// </summary>
public class PersistedStateTests
{
    private static SetupState AnInstall()
    {
        var state = new SetupState
        {
            DoppioDir = @"C:\src\Doppio",
            RistrettoDir = @"C:\src\Ristretto",
            InstallCredentialProvider = true,
            EnableProviderFilter = true,
            InstallAuthPackage = true,
            DenyKeepNetwork = true,
            Network = NetworkPolicy.RistrettoGate,
        };
        state.AccountsToEnroll.AddRange(["admin", "breakglass"]);
        state.AccountsToDeny.Add("admin");
        return state;
    }

    private static PersistedState RoundTrip(PersistedState record) =>
        StateStore.Deserialize(StateStore.Serialize(record))
        ?? throw new InvalidOperationException("The record did not survive its own serializer.");

    [Fact]
    public void Every_choice_survives_the_round_trip()
    {
        var original = AnInstall();
        var back = RoundTrip(PersistedState.From(original)).ToSetupState();

        // Deliberately asserted field by field: this is the one place where a
        // choice that fails to persist is invisible, because a bool that was
        // never written reads back as false rather than as an error.
        Assert.Equal(original.DoppioDir, back.DoppioDir);
        Assert.Equal(original.RistrettoDir, back.RistrettoDir);
        Assert.Equal(original.InstallCredentialProvider, back.InstallCredentialProvider);
        Assert.Equal(original.EnableProviderFilter, back.EnableProviderFilter);
        Assert.Equal(original.InstallAuthPackage, back.InstallAuthPackage);
        Assert.Equal(original.DenyKeepNetwork, back.DenyKeepNetwork);
        Assert.Equal(original.Network, back.Network);
        Assert.Equal(original.AccountsToEnroll, back.AccountsToEnroll);
        Assert.Equal(original.AccountsToDeny, back.AccountsToDeny);
    }

    [Fact]
    public void The_plan_built_from_a_resumed_record_is_the_same_plan()
    {
        var original = AnInstall();
        var back = RoundTrip(PersistedState.From(original)).ToSetupState();

        // The real assertion behind the field-by-field one above: a resumed run
        // must install the same things in the same order, or the remaining
        // stages are being run against a different install than the one that
        // started.
        Assert.Equal(
            InstallPlan.BuildStages(original).Select(s => s.Id),
            InstallPlan.BuildStages(back).Select(s => s.Id));
    }

    [Fact]
    public void The_network_policy_is_written_by_name_not_by_number()
    {
        var json = StateStore.Serialize(PersistedState.From(AnInstall()));

        // As a number it would silently change meaning the next time somebody
        // adds a value to the enum in the middle.
        Assert.Contains("RistrettoGate", json);
    }

    [Fact]
    public void The_risk_acknowledgement_is_not_kept_on_disk()
    {
        var state = AnInstall();
        state.AcceptedThrowawayMachine = true;
        state.AcceptedSnapshot = true;
        state.AcceptedRecoveryPlan = true;

        var json = StateStore.Serialize(PersistedState.From(state));

        // A consent is something a person gave looking at a screen. Restoring
        // one from a file is not the same act.
        Assert.DoesNotContain("AcceptedSnapshot", json);
        Assert.DoesNotContain("AcceptedThrowawayMachine", json);
        Assert.DoesNotContain("AcceptedRecoveryPlan", json);
    }

    [Fact]
    public void Resuming_grants_the_risk_acknowledgement_again()
    {
        var back = RoundTrip(PersistedState.From(AnInstall()));

        // It was answered once for this machine, and the machine has not become
        // less of a throwaway since. Re-asking at every reboot is how people
        // learn to click through it.
        Assert.True(back.ToSetupState().RisksAccepted);
    }

    [Fact]
    public void No_secret_goes_anywhere_near_the_record()
    {
        var json = StateStore.Serialize(PersistedState.From(AnInstall()));

        // The TOTP secrets stay DPAPI-encrypted in Doppio's own store. Nothing
        // in SetupState is supposed to be able to carry one, and this is the
        // assertion that notices if something ever does.
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("otpauth", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_record_from_an_older_version_is_refused()
    {
        var json = StateStore.Serialize(PersistedState.From(AnInstall()))
            .Replace($"\"SchemaVersion\": {StateStore.CurrentSchema}", "\"SchemaVersion\": 2");

        // Version 2 kept the choices as top-level fields, so every one of them
        // would read as its default here - which for a bool is false, and a
        // false EnableProviderFilter finishes an install that enforces nothing.
        Assert.Null(StateStore.Deserialize(json));
    }

    [Fact]
    public void A_record_with_no_choices_in_it_is_refused()
    {
        Assert.Null(StateStore.Deserialize(
            $"{{\"SchemaVersion\": {StateStore.CurrentSchema}, \"Setup\": null}}"));
    }

    [Fact]
    public void Nonsense_is_refused_rather_than_thrown()
    {
        // The resume path reads a file that anything could have written to.
        Assert.Null(StateStore.Deserialize("not json at all"));
        Assert.Null(StateStore.Deserialize(""));
    }

    [Fact]
    public void Where_the_run_got_to_survives_the_round_trip()
    {
        var record = PersistedState.From(AnInstall());
        record.CompletedStages = [StageId.Provider, StageId.Enrollment];
        record.AwaitingVerificationOf = StageId.Enrollment;
        record.Log = "line one\nline two";

        var back = RoundTrip(record);

        Assert.Equal(new[] { StageId.Provider, StageId.Enrollment }, back.CompletedStages);
        Assert.Equal(StageId.Enrollment, back.AwaitingVerificationOf);
        Assert.Equal("line one\nline two", back.Log);
        Assert.Equal(record.StartedUtc, back.StartedUtc);
    }

    [Fact]
    public void A_launcher_path_in_an_older_record_is_read_past_and_not_kept()
    {
        // Older versions stored the exe path and armed RunOnce with it on the
        // next save. The record lives in ProgramData, and HKLM RunOnce starts
        // whatever it names as administrator, so the path is no longer read.
        var json = StateStore.Serialize(PersistedState.From(AnInstall()))
            .Replace("\"Log\"", "\"LauncherPath\": \"C:\\\\Users\\\\Public\\\\evil.exe\",\n  \"Log\"");
        Assert.Contains("LauncherPath", json);

        var back = StateStore.Deserialize(json);

        Assert.NotNull(back);
        Assert.DoesNotContain("LauncherPath", StateStore.Serialize(back!));
    }
}
