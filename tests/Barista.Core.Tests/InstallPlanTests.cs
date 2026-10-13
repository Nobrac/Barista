using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// The order of the stage plan, which is the part of this tool that can lock
/// someone out of a machine. Every assertion here corresponds to a sentence in
/// InstallPlan's own comments; the point of the file is that those sentences
/// now fail a build instead of being re-read by whoever changes the plan next.
/// </summary>
public class InstallPlanTests
{
    private static SetupState Defaults() => new();

    private static List<StageId> Ids(SetupState state) =>
        InstallPlan.BuildStages(state).Select(s => s.Id).ToList();

    [Fact]
    public void The_tile_goes_in_first()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");

        // Nothing is denied and nobody is blocked before there is a tile to
        // sign in with.
        Assert.Equal(StageId.Provider, Ids(state).First());
    }

    [Fact]
    public void The_filter_runs_after_enrollment_and_before_the_network_stage()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");
        state.Network = NetworkPolicy.DoppioFilter;

        var ids = Ids(state);
        var enrollment = ids.IndexOf(StageId.Enrollment);
        var filter = ids.IndexOf(StageId.ProviderFilter);
        var network = ids.IndexOf(StageId.NetworkPolicy);

        Assert.True(enrollment >= 0 && filter >= 0 && network >= 0);

        // The accounts a filter will judge need a secret first: the filter
        // script refuses to register while an enabled local account has none.
        Assert.True(enrollment < filter);

        // And the filter goes in while a network logon is still a way back into
        // a machine whose tile turns out to be broken.
        Assert.True(filter < network);
    }

    [Fact]
    public void The_authentication_package_is_last()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");
        state.Network = NetworkPolicy.RistrettoGate;
        state.InstallAuthPackage = true;

        Assert.Equal(StageId.AuthPackage, Ids(state).Last());
    }

    [Fact]
    public void Declining_the_filter_leaves_the_stage_out_entirely()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");
        state.EnableProviderFilter = false;

        Assert.DoesNotContain(StageId.ProviderFilter, Ids(state));
    }

    [Fact]
    public void The_enrollment_index_is_rebuilt_only_when_a_filter_will_read_it()
    {
        var withoutFilter = Defaults();
        withoutFilter.AccountsToEnroll.Add("admin");
        withoutFilter.Network = NetworkPolicy.DenyRights;

        var enrollment = InstallPlan.BuildStages(withoutFilter)
            .Single(s => s.Id == StageId.Enrollment);
        Assert.Single(enrollment.Steps);

        var withFilter = Defaults();
        withFilter.AccountsToEnroll.Add("admin");
        withFilter.Network = NetworkPolicy.DoppioFilter;

        var reindexed = InstallPlan.BuildStages(withFilter)
            .Single(s => s.Id == StageId.Enrollment);

        // Last, not first: an account enrolled before the index existed would
        // read as not enrolled and its network logons would keep working.
        Assert.Contains("/reindex", reindexed.Steps.Last().Arguments);
    }

    [Fact]
    public void Deny_rights_are_one_step_per_account_and_carry_the_keep_network_choice()
    {
        var state = Defaults();
        state.Network = NetworkPolicy.DenyRights;
        state.AccountsToDeny.AddRange(["alice", "bob"]);
        state.DenyKeepNetwork = true;

        var stage = InstallPlan.BuildStages(state).Single(s => s.Id == StageId.NetworkPolicy);

        Assert.Equal(2, stage.Steps.Count);
        Assert.Contains("alice", stage.Steps[0].Arguments);
        Assert.Contains("bob", stage.Steps[1].Arguments);
        Assert.All(stage.Steps, s => Assert.Contains("-KeepNetwork", s.Arguments));

        // Nothing here runs in lsass, so nothing here needs a reboot to be read.
        Assert.False(stage.RebootAfter);
    }

    [Fact]
    public void Nobody_to_deny_means_no_network_stage_at_all()
    {
        var state = Defaults();
        state.Network = NetworkPolicy.DenyRights;

        // An empty stage would still ask for a confirmation and still be
        // written down as done, which is a stage that says something happened
        // when nothing did.
        Assert.DoesNotContain(StageId.NetworkPolicy, Ids(state));
    }

    [Fact]
    public void Every_stage_that_reboots_can_be_verified_afterwards()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");
        state.AccountsToDeny.Add("admin");
        state.Network = NetworkPolicy.DoppioFilter;
        state.InstallAuthPackage = true;

        foreach (var stage in InstallPlan.BuildStages(state).Where(s => s.RebootAfter))
        {
            // The reboot is the test. A stage that reboots without asking
            // anything afterwards has spent a restart and learned nothing,
            // which defeats the point of staging at all.
            Assert.False(string.IsNullOrWhiteSpace(stage.VerifyPrompt));
            Assert.False(string.IsNullOrWhiteSpace(stage.VerifyDetail));
        }
    }

    [Fact]
    public void The_filter_stage_asks_its_question_before_it_runs()
    {
        var state = Defaults();
        state.AccountsToEnroll.Add("admin");

        var filter = InstallPlan.BuildStages(state).Single(s => s.Id == StageId.ProviderFilter);

        // "Have you actually signed in through the tile yet?" - no registry
        // read can answer that, and it is the last moment the password tile is
        // there to catch a wrong answer.
        Assert.False(string.IsNullOrWhiteSpace(filter.PreRunPrompt));
    }

    [Fact]
    public void Stage_ids_are_part_of_the_on_disk_format_and_do_not_move()
    {
        // A resumed record holds these numbers. Renumbering them turns "the
        // provider is done" into "the enrollment is done" on a machine halfway
        // through an install.
        Assert.Equal(1, (int)StageId.Provider);
        Assert.Equal(2, (int)StageId.Enrollment);
        Assert.Equal(3, (int)StageId.NetworkPolicy);
        Assert.Equal(4, (int)StageId.AuthPackage);
        Assert.Equal(5, (int)StageId.ProviderFilter);
    }
}
