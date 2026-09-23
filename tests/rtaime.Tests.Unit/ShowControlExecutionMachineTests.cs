// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class ShowControlExecutionMachineTests
{
	[Fact]
	public void Go_chains_actions_inside_one_cue_and_rearms_at_the_next_cue()
	{
		var machine = new ShowControlExecutionMachine();
		var cueList = Fixture();

		var armed = machine.Arm(cueList);
		var executing = machine.BeginGo();
		var secondAction = machine.CompleteCurrentAction();
		var nextCue = machine.CompleteCurrentAction();

		Assert.Equal(ShowControlExecutionState.Armed, armed.State);
		Assert.Equal(ShowControlExecutionState.Executing, executing.State);
		Assert.Equal(1, secondAction.ActionIndex);
		Assert.Equal(ShowControlExecutionState.Armed, nextCue.State);
		Assert.Equal(1, nextCue.CueIndex);
		Assert.Equal(cueList.Cues[1].CueId, nextCue.CurrentCueId);
	}

	[Fact]
	public void Frame_wait_requires_same_runtime_instance_and_target_sequence()
	{
		var machine = new ShowControlExecutionMachine();
		machine.Arm(Fixture());
		machine.BeginGo();
		machine.CompleteCurrentAction();
		var waiting = machine.BeginWait(120, "runtime-a");

		Assert.Equal(ShowControlExecutionState.Waiting, waiting.State);
		Assert.Throws<InvalidOperationException>(() => machine.CompleteWait(119, "runtime-a"));
		Assert.Throws<InvalidOperationException>(() => machine.CompleteWait(120, "runtime-b"));

		var completed = machine.CompleteWait(120, "runtime-a");
		Assert.Equal(ShowControlExecutionState.Armed, completed.State);
		Assert.Equal(1, completed.CueIndex);
	}

	[Fact]
	public void Restart_during_unsafe_action_requires_recovery_and_blocks_resume()
	{
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(Id(40)),
			"Unsafe",
			new[]
			{
				new ShowControlCue(
					new ShowControlCueId(Id(41)),
					"Take",
					new[] { new ShowControlAction(new ShowControlActionId(Id(42)), ShowControlActionKind.Cut) })
			});
		var first = new ShowControlExecutionMachine();
		first.Arm(cueList);
		var executing = first.BeginGo();
		var recovered = new ShowControlExecutionMachine();

		var recovery = recovered.Restore(cueList, executing);

		Assert.Equal(ShowControlExecutionState.RecoveryRequired, recovery.State);
		Assert.True(recovery.RequiresAcknowledgement);
		Assert.Throws<InvalidOperationException>(() => recovered.AcknowledgeRecovery(resume: true));
		Assert.Equal(ShowControlExecutionState.Cancelled, recovered.AcknowledgeRecovery(resume: false).State);
	}

	[Fact]
	public void Restart_during_replay_safe_action_can_be_acknowledged_to_armed()
	{
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(Id(50)),
			"Safe",
			new[]
			{
				new ShowControlCue(
					new ShowControlCueId(Id(51)),
					"Preview",
					new[]
					{
						new ShowControlAction(
							new ShowControlActionId(Id(52)),
							ShowControlActionKind.SetPreview,
							sourceId: Id(53).ToString())
					})
			});
		var first = new ShowControlExecutionMachine();
		first.Arm(cueList);
		var executing = first.BeginGo();
		var recovered = new ShowControlExecutionMachine();
		recovered.Restore(cueList, executing);

		var armed = recovered.AcknowledgeRecovery(resume: true);

		Assert.Equal(ShowControlExecutionState.Armed, armed.State);
		Assert.False(armed.RequiresAcknowledgement);
		Assert.Null(armed.Failure);
	}

	private static ShowControlCueList Fixture() =>
		new(
			ShowControlContractVersion.Current,
			new ShowControlCueListId(Id(1)),
			"Reference",
			new[]
			{
				new ShowControlCue(
					new ShowControlCueId(Id(2)),
					"First",
					new[]
					{
						new ShowControlAction(new ShowControlActionId(Id(3)), ShowControlActionKind.SetPreview, sourceId: Id(4).ToString()),
						new ShowControlAction(new ShowControlActionId(Id(5)), ShowControlActionKind.WaitFrames, waitFrames: 10)
					}),
				new ShowControlCue(
					new ShowControlCueId(Id(6)),
					"Second",
					new[] { new ShowControlAction(new ShowControlActionId(Id(7)), ShowControlActionKind.StopRecording) })
			});

	private static Identity Id(int value) =>
		new(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));
}
