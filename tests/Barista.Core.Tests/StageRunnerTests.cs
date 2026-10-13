using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// How the staged install behaves when a step fails, when a stage needs a
/// restart, and what ends up on disk. None of this used to be reachable: it
/// lived in a WPF code-behind next to the MessageBox calls.
/// </summary>
public class StageRunnerTests
{
    /// <summary>
    /// Stands in for powershell.exe and enroll.exe. Records what it was asked
    /// to run, and fails whatever the test tells it to.
    /// </summary>
    private sealed class FakeExecutor : IStepExecutor
    {
        public List<Step> Ran { get; } = [];

        /// <summary>Step title to exit code. Anything not named succeeds.</summary>
        public Dictionary<string, int> Failures { get; } = new();

        public Task<StepResult> RunAsync(Step step, Action<string> onLine, CancellationToken ct = default)
        {
            Ran.Add(step);
            onLine($"[fake] {step.Title}");
            return Task.FromResult(new StepResult(
                Failures.TryGetValue(step.Title, out var code) ? code : 0,
                ""));
        }
    }

    /// <summary>
    /// Stands in for the pending-run file and the RunOnce key.
    ///
    /// Snapshots each record through the real serializer, because the runner
    /// deliberately reuses one <see cref="PersistedState"/> across saves - so
    /// keeping the reference would show every earlier save as whatever the last
    /// one wrote.
    /// </summary>
    private sealed class RecordingStore : IPendingRunStore
    {
        public List<(PersistedState Record, bool ArmRunOnce)> Saves { get; } = [];

        public void Save(PersistedState state, bool armRunOnce)
        {
            var snapshot = StateStore.Deserialize(StateStore.Serialize(state))
                           ?? throw new InvalidOperationException(
                               "The runner wrote a record this version refuses to read back.");
            Saves.Add((snapshot, armRunOnce));
        }
    }

    /// <summary>
    /// Two stages, neither of which needs a restart, so one runner can get
    /// through both. The reboot stages are tested on their own below.
    /// </summary>
    private static SetupState TwoQuietStages()
    {
        var state = new SetupState
        {
            InstallCredentialProvider = false,
            EnableProviderFilter = false,
            Network = NetworkPolicy.DenyRights,
            DoppioDir = @"C:\doppio",
        };
        state.AccountsToEnroll.Add("admin");
        state.AccountsToDeny.Add("service");
        return state;
    }

    private static (StageRunner Runner, FakeExecutor Executor, RecordingStore Store) Build(
        SetupState state)
    {
        var executor = new FakeExecutor();
        var store = new RecordingStore();
        return (new StageRunner(state, executor, store), executor, store);
    }

    [Fact]
    public async Task Stages_run_in_plan_order_one_call_at_a_time()
    {
        var state = TwoQuietStages();
        var (runner, executor, _) = Build(state);

        Assert.Equal(StageId.Enrollment, runner.Next!.Id);

        var first = await runner.RunNextStageAsync();
        Assert.True(first.Ok);
        Assert.Equal(StageId.Enrollment, first.Stage.Id);
        Assert.Equal(StageId.NetworkPolicy, runner.Next!.Id);

        var second = await runner.RunNextStageAsync();
        Assert.True(second.Ok);
        Assert.Equal(StageId.NetworkPolicy, second.Stage.Id);

        Assert.True(runner.AllDone);
        Assert.Null(runner.Next);
        Assert.Equal(new[] { StageId.Enrollment, StageId.NetworkPolicy }, runner.Completed);
        Assert.Equal(2, executor.Ran.Count);
    }

    [Fact]
    public async Task A_failed_step_stops_the_rest_of_its_stage()
    {
        var state = new SetupState { InstallCredentialProvider = false, EnableProviderFilter = false };
        state.AccountsToEnroll.AddRange(["alice", "bob", "carol"]);

        var (runner, executor, store) = Build(state);
        executor.Failures["Enroll 'bob'"] = 1;

        var outcome = await runner.RunNextStageAsync();

        Assert.False(outcome.Ok);
        Assert.Equal("Enroll 'bob'", outcome.FailedStep!.Title);

        // carol is not attempted: the steps are ordered so that nothing starts
        // refusing logons before there is a way in, and carrying on past a
        // failure is how that ordering stops meaning anything.
        Assert.Equal(new[] { "Enroll 'alice'", "Enroll 'bob'" }, executor.Ran.Select(s => s.Title));

        // Not finished, so not recorded as finished - but still written down,
        // because an earlier stage in the same session may have finished.
        Assert.Empty(runner.Completed);
        Assert.Single(store.Saves);
        Assert.False(store.Saves[0].ArmRunOnce);
        Assert.Empty(store.Saves[0].Record.CompletedStages);
    }

    [Fact]
    public async Task A_step_that_throws_is_a_failed_step_and_not_a_crash()
    {
        var state = new SetupState { InstallCredentialProvider = false, EnableProviderFilter = false };
        state.AccountsToEnroll.Add("alice");

        var store = new RecordingStore();
        var runner = new StageRunner(state, new ThrowingExecutor(), store);

        var outcome = await runner.RunNextStageAsync();

        // A missing script or an exe that will not start lands here. The stage
        // has to stop the same way, and the record still has to be written.
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.FailedStep);
        Assert.Single(store.Saves);
        Assert.Contains("no such file", runner.Transcript);
    }

    private sealed class ThrowingExecutor : IStepExecutor
    {
        public Task<StepResult> RunAsync(Step step, Action<string> onLine, CancellationToken ct = default)
            => throw new FileNotFoundException("no such file");
    }

    [Fact]
    public async Task A_finished_stage_is_written_down_and_arms_run_once_only_when_a_reboot_is_pending()
    {
        var quiet = TwoQuietStages();
        var (runner, _, store) = Build(quiet);

        await runner.RunNextStageAsync();

        Assert.Single(store.Saves);
        Assert.False(store.Saves[0].ArmRunOnce);
        Assert.Equal(new[] { StageId.Enrollment }, store.Saves[0].Record.CompletedStages);
        Assert.Null(store.Saves[0].Record.AwaitingVerificationOf);

        // A stage with no reboot of its own used to be finished in memory and
        // nowhere else, so closing the window after it lost the fact that it
        // had run - and the next start re-ran enroll.exe for an account that
        // already had a secret.
        var provider = new SetupState { DoppioDir = @"C:\doppio" };
        var (rebooting, _, rebootStore) = Build(provider);

        var outcome = await rebooting.RunNextStageAsync();

        Assert.True(outcome.RebootPending);
        Assert.Single(rebootStore.Saves);
        Assert.True(rebootStore.Saves[0].ArmRunOnce);
        Assert.Equal(StageId.Provider, rebootStore.Saves[0].Record.AwaitingVerificationOf);
    }

    [Fact]
    public async Task A_stage_awaiting_its_restart_blocks_the_next_one()
    {
        var state = new SetupState { DoppioDir = @"C:\doppio" };
        state.AccountsToEnroll.Add("admin");

        var (runner, executor, _) = Build(state);

        await runner.RunNextStageAsync();
        Assert.Equal(StageId.Provider, runner.AwaitingReboot!.Id);

        // The next stage would be layered on a registration LSA has not read
        // yet, and a machine that then fails to boot has two suspects.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunNextStageAsync());
        Assert.Single(executor.Ran);
    }

    [Fact]
    public async Task A_failed_stage_can_be_run_again()
    {
        var state = new SetupState { InstallCredentialProvider = false, EnableProviderFilter = false };
        state.AccountsToEnroll.Add("alice");

        var (runner, executor, _) = Build(state);
        executor.Failures["Enroll 'alice'"] = 1;

        Assert.False((await runner.RunNextStageAsync()).Ok);

        // Stages are the unit of retry, so the same one is still what is next.
        Assert.Equal(StageId.Enrollment, runner.Next!.Id);

        executor.Failures.Clear();
        Assert.True((await runner.RunNextStageAsync()).Ok);
        Assert.Equal(new[] { StageId.Enrollment }, runner.Completed);
    }

    [Fact]
    public async Task A_resumed_run_does_not_repeat_what_is_already_done()
    {
        var state = new SetupState { DoppioDir = @"C:\doppio" };
        state.AccountsToEnroll.Add("admin");

        var persisted = PersistedState.From(state);
        persisted.CompletedStages = [StageId.Provider];
        persisted.AwaitingVerificationOf = StageId.Provider;
        persisted.Log = "what happened before the restart";

        var (runner, _, _) = Build(state);
        runner.AdoptResumedRun(persisted);

        Assert.Equal(StageId.Enrollment, runner.Next!.Id);
        Assert.StartsWith("what happened before the restart", runner.Transcript);
        Assert.Contains("=== Resumed after restart", runner.Transcript);

        await runner.RunNextStageAsync();
        Assert.Equal(new[] { StageId.Provider, StageId.Enrollment }, runner.Completed);
    }

    [Fact]
    public async Task The_record_keeps_the_start_time_across_saves()
    {
        var state = TwoQuietStages();
        var (runner, _, store) = Build(state);

        await runner.RunNextStageAsync();
        await runner.RunNextStageAsync();

        // Rebuilding the record per save would reset this to "now" every time,
        // and the resume page reports it as when the run began.
        Assert.Equal(2, store.Saves.Count);
        Assert.Equal(store.Saves[0].Record.StartedUtc, store.Saves[1].Record.StartedUtc);
    }

    [Fact]
    public async Task The_record_carries_the_log_so_far()
    {
        var state = TwoQuietStages();
        var (runner, _, store) = Build(state);

        await runner.RunNextStageAsync();

        Assert.Contains("[fake] Enroll 'admin'", store.Saves[0].Record.Log);
    }

    [Fact]
    public async Task Rebuilding_the_plan_keeps_finished_stages_finished()
    {
        var state = TwoQuietStages();
        var (runner, _, _) = Build(state);

        await runner.RunNextStageAsync();

        // The user went back and asked for the authentication package as well.
        state.InstallAuthPackage = true;
        runner.Rebuild();

        Assert.Contains(StageId.AuthPackage, runner.Stages.Select(s => s.Id));
        Assert.Equal(new[] { StageId.Enrollment }, runner.Completed);
        Assert.Equal(StageId.NetworkPolicy, runner.Next!.Id);
    }

    [Fact]
    public async Task There_is_nothing_to_run_once_every_stage_is_done()
    {
        var state = TwoQuietStages();
        var (runner, _, _) = Build(state);

        await runner.RunNextStageAsync();
        await runner.RunNextStageAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunNextStageAsync());
    }
}
