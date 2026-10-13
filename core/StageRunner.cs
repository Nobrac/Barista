using System.Text;

namespace Barista.Services;

/// <summary>What one call to <see cref="StageRunner.RunNextStageAsync"/> came to.</summary>
/// <param name="Stage">The stage that ran.</param>
/// <param name="Ok">Every step succeeded.</param>
/// <param name="FailedStep">The step that failed, if one did.</param>
/// <param name="RebootPending">
/// The stage finished and LSA has not read it yet. Nothing further may run
/// until the machine has been round the restart and the user has said it came
/// back - which is the whole reason stages exist.
/// </param>
public sealed record StageOutcome(
    Stage Stage,
    bool Ok,
    Step? FailedStep,
    bool RebootPending);

/// <summary>
/// Runs the staged install: one stage at a time, stopping at the first failed
/// step, writing down every finished stage.
///
/// This used to live in the install page's code-behind, wrapped around
/// MessageBox calls. That made the one part of this tool where a mistake locks
/// someone out of their machine the one part no test could reach: the rules it
/// enforces - the plan's order, stop at the first failure, every finished stage
/// on disk, RunOnce armed only when a reboot is actually pending - were
/// asserted by comments and by whoever last read them.
///
/// The dialogs stayed in the page, because they are the only part that needs a
/// person looking at a screen. Everything here is decisions about the machine.
/// </summary>
public sealed class StageRunner
{
    private readonly SetupState _state;
    private readonly IStepExecutor _executor;
    private readonly IPendingRunStore _store;
    private readonly Action<string>? _onLine;

    private readonly List<StageId> _done = [];
    private readonly StringBuilder _transcript = new();

    /// <summary>
    /// Guards the transcript. <see cref="Log"/> is called both from here and,
    /// as the output callback, from the threadpool threads that read a script's
    /// stdout and stderr - so two appends can genuinely overlap, and
    /// StringBuilder is not thread-safe. The transcript is what gets persisted
    /// and what the user reads afterwards, so it is worth a lock held for the
    /// length of one line.
    /// </summary>
    private readonly object _transcriptGate = new();

    private List<Stage> _stages;

    /// <summary>
    /// The record being written, kept across calls.
    ///
    /// It has to be the same object every time: building a fresh one per save
    /// would reset StartedUtc to "now" on each write, and the resume page
    /// reports that value as when the run began. On a resumed run this starts
    /// as the record we came back from, so the original start time survives.
    /// </summary>
    private PersistedState? _record;

    public StageRunner(
        SetupState state,
        IStepExecutor? executor = null,
        IPendingRunStore? store = null,
        Action<string>? onLine = null)
    {
        _state = state;
        _executor = executor ?? ProcessStepExecutor.Instance;
        _store = store ?? PendingRunStore.Instance;
        _onLine = onLine;
        _stages = InstallPlan.BuildStages(state);
    }

    /// <summary>The plan, in the order it will run.</summary>
    public IReadOnlyList<Stage> Stages => _stages;

    /// <summary>Stages finished in this run or an earlier one.</summary>
    public IReadOnlyList<StageId> Completed => _done;

    /// <summary>Everything the log has said, including whatever a resumed record carried.</summary>
    public string Transcript
    {
        get { lock (_transcriptGate) return _transcript.ToString(); }
    }

    /// <summary>True while a stage is running. Nothing else may start.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The stage that finished and is waiting for its restart, if any. While
    /// this is set, <see cref="RunNextStageAsync"/> refuses to run anything:
    /// the next stage would be layered on top of a registration LSA has not
    /// loaded yet, and a machine that then fails to boot has two suspects.
    /// </summary>
    public Stage? AwaitingReboot { get; private set; }

    /// <summary>The next stage to run, or null when there is none left.</summary>
    public Stage? Next => _stages.FirstOrDefault(s => !_done.Contains(s.Id));

    public bool AllDone => _stages.Count > 0 && _stages.All(s => _done.Contains(s.Id));

    /// <summary>
    /// Rebuilds the plan from the current choices.
    ///
    /// Called when the install page is entered, because the user can go back
    /// and change a component after the plan was first built. Stages already
    /// finished keep their place: they are tracked by id, so a plan that no
    /// longer contains one simply does not offer it again.
    /// </summary>
    public void Rebuild() => _stages = InstallPlan.BuildStages(_state);

    /// <summary>
    /// Picks up a run that was interrupted by a restart: which stages are
    /// already done, and the log so far.
    /// </summary>
    public void AdoptResumedRun(PersistedState persisted)
    {
        _record = persisted;

        _done.Clear();
        _done.AddRange(persisted.CompletedStages);

        lock (_transcriptGate)
        {
            _transcript.Clear();
            if (persisted.Log.Length > 0)
            {
                _transcript.Append(persisted.Log);
                if (!persisted.Log.EndsWith('\n')) _transcript.AppendLine();
                _transcript.AppendLine("=== Resumed after restart");
                _transcript.AppendLine();
            }
        }
    }

    /// <summary>
    /// Runs the next stage and writes the result down.
    ///
    /// Steps run in order and stop at the first failure. That matters more than
    /// usual here: the plan is ordered so that nothing starts refusing logons
    /// before there is a way in, and carrying on past a failed step is how that
    /// ordering stops meaning anything.
    /// </summary>
    public async Task<StageOutcome> RunNextStageAsync(CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("A stage is already running.");
        if (AwaitingReboot is { } pending)
            throw new InvalidOperationException(
                $"{pending.Title} is finished but not loaded. The machine has to restart first.");
        if (Next is not { } stage)
            throw new InvalidOperationException("There is no stage left to run.");

        IsRunning = true;
        try
        {
            Log($"=== {stage.Title}");

            Step? failedStep = null;

            for (var i = 0; i < stage.Steps.Count; i++)
            {
                var step = stage.Steps[i];
                Log($"--- [{i + 1}/{stage.Steps.Count}] {step.Title}");

                // reg.exe takes a file, not a command line anyone would
                // recognise from the step title, so the log says which file.
                if (step.Kind == StepKind.RegImport)
                    Log($"    reg import {System.IO.Path.GetFileName(step.Command)}");

                StepResult result;
                try
                {
                    result = await _executor.RunAsync(step, Log, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A missing script or an exe that will not start arrives
                    // here. It is a failed step like any other: the stage stops
                    // and the record still gets written.
                    Log($"    FAILED: {ex.Message}");
                    failedStep = step;
                    break;
                }

                if (result.Ok)
                {
                    Log("    Done.");
                    continue;
                }

                Log(step.Kind == StepKind.Console
                    // enroll.exe is the console step, and it stores nothing
                    // unless the code it was given matched. Saying so stops the
                    // obvious next question.
                    ? $"    Exited with code {result.ExitCode}. Nothing was stored if it reported a failure."
                    : $"    FAILED (exit {result.ExitCode}).");

                failedStep = step;
                break;
            }

            if (failedStep is not null)
            {
                Log("");
                Log("Stopped. A later step would have assumed this one worked.");
                Log("Read the error above, fix it, and run the stage again - or restore the snapshot.");

                // Written even though this stage did not finish: earlier stages
                // in this session may have, and losing those is how a resume
                // ends up re-running work that is already done. No RunOnce -
                // nothing is waiting on a reboot.
                Save(armRunOnce: false);

                return new StageOutcome(stage, Ok: false, failedStep, RebootPending: false);
            }

            _done.Add(stage.Id);
            Log($"=== {stage.Title}: done.");
            Log("");

            // Set before the save: the record carries which stage the resume
            // page has to ask about.
            if (stage.RebootAfter) AwaitingReboot = stage;

            // Every finished stage is written down, whether or not a reboot
            // follows. Only a pending reboot arms RunOnce - see StateStore.Save.
            Save(armRunOnce: stage.RebootAfter);

            return new StageOutcome(stage, Ok: true, FailedStep: null, stage.RebootAfter);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Writes the pending-run record.
    ///
    /// Public because the page saves again when the user chooses to restart
    /// later: the record is the same, but RunOnce has to be armed so the tool
    /// comes back on its own.
    /// </summary>
    public void Save(bool armRunOnce)
    {
        var record = _record ??= PersistedState.From(_state);

        // The choices are not copied field by field - the record holds the
        // SetupState itself. That is the whole point of the envelope: a new
        // choice added to SetupState is persisted without anyone remembering to
        // come back here.
        record.Setup = _state;
        record.CompletedStages = [.. _done];
        record.AwaitingVerificationOf = AwaitingReboot?.Id;
        record.Log = Transcript;

        _store.Save(record, armRunOnce);
    }

    private void Log(string line)
    {
        lock (_transcriptGate) _transcript.AppendLine(line);

        // Outside the lock: the display sink marshals to the UI thread and
        // blocks until it has run, and holding a lock across that - while a
        // reader thread is trying to take the same one - is how a hang gets
        // built.
        _onLine?.Invoke(line);
    }
}
