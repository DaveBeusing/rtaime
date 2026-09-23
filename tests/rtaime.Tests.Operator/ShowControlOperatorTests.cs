// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ShowControlOperatorTests
{
	[Fact]
	public void Selecting_a_show_control_cue_is_presentation_only()
	{
		var transport = new CountingTransport();
		var viewModel = new ShowControlViewModel(new OperatorControlClient(transport));
		var first = CreateCue("Opening", ShowControlActionKind.Cut);
		var second = CreateCue("Playback", ShowControlActionKind.MediaPlay);
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			ShowControlCueListId.New(),
			"Main show",
			[first, second]);

		viewModel.ApplyConfirmedSnapshot(new ShowControlWorkspaceSnapshot(
			[cueList],
			cueList.CueListId,
			ShowControlExecutionSnapshot.Idle));
		viewModel.SelectedCue = viewModel.SelectedCueList!.Cues[1];

		Assert.Equal("Playback", viewModel.SelectedCue.Name);
		Assert.Equal(0, transport.ShowControlMutationCalls);
		Assert.Equal(0, transport.ProductionMutationCalls);
	}

	[Fact]
	public void Editor_preserves_stable_identity_across_rename_and_reorder()
	{
		var actionA = ShowControlActionEditorItem.FromContract(new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut));
		var actionB = ShowControlActionEditorItem.FromContract(new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.StopRecording));
		var cueA = new ShowControlCueEditorItem(ShowControlCueId.New(), "Cue A", [actionA]);
		var cueB = new ShowControlCueEditorItem(ShowControlCueId.New(), "Cue B", [actionB]);
		var editor = new ShowControlCueListEditorItem(ShowControlCueListId.New(), "Show", [cueA, cueB]);

		var originalListId = editor.CueListId;
		var originalCueAId = cueA.CueId;
		var originalActionAId = actionA.ActionId;
		editor.Name = "Renamed Show";
		cueA.Name = "Renamed Cue";
		editor.Cues.Move(0, 1);

		var contract = editor.ToContract();

		Assert.Equal(originalListId, contract.CueListId);
		Assert.Equal(originalCueAId, contract.Cues[1].CueId);
		Assert.Equal(originalActionAId, contract.Cues[1].Actions[0].ActionId);
		Assert.Equal("Renamed Show", contract.Name);
		Assert.Equal("Renamed Cue", contract.Cues[1].Name);
	}

	[Fact]
	public void Recovery_required_exposes_acknowledgement_and_blocks_go()
	{
		var transport = new CountingTransport();
		var viewModel = new ShowControlViewModel(new OperatorControlClient(transport));
		var cue = CreateCue("Safe stop", ShowControlActionKind.StopRecording);
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			ShowControlCueListId.New(),
			"Main show",
			[cue]);
		var execution = new ShowControlExecutionSnapshot(
			ShowControlContractVersion.Current,
			ShowControlExecutionId.New(),
			cueList.CueListId,
			ShowControlExecutionState.RecoveryRequired,
			0,
			0,
			cue.CueId,
			cue.Actions[0].ActionId,
			7,
			null,
			null,
			true,
			new Failure("show_control.recovery.test", "Acknowledgement required."));

		viewModel.ApplyConfirmedSnapshot(new ShowControlWorkspaceSnapshot([cueList], cueList.CueListId, execution));

		Assert.True(viewModel.RequiresAcknowledgement);
		Assert.False(viewModel.GoCommand.CanExecute(null));
		Assert.True(viewModel.ResumeRecoveryCommand.CanExecute(null));
		Assert.Contains("acknowledgement", viewModel.ExecutionDetail, StringComparison.OrdinalIgnoreCase);
	}

	private static ShowControlCue CreateCue(string name, ShowControlActionKind kind)
	{
		var action = kind switch
		{
			ShowControlActionKind.MediaPlay => new ShowControlAction(
				ShowControlActionId.New(),
				kind,
				mediaAssetId: Identity.New().ToString()),
			_ => new ShowControlAction(ShowControlActionId.New(), kind)
		};
		return new ShowControlCue(ShowControlCueId.New(), name, [action]);
	}

	private sealed class CountingTransport : IOperatorControlTransport
	{
		public int ShowControlMutationCalls { get; private set; }
		public int ProductionMutationCalls { get; private set; }

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorStatusSnapshot>(new InvalidOperationException("Not required by presentation-only tests."));

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default)
		{
			ProductionMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Unexpected production mutation."));
		}

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			ProductionMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Unexpected production mutation."));
		}

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			ProductionMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Unexpected production mutation."));
		}

		public ValueTask<ShowControlWorkspaceSnapshot> SelectShowControlCueListAsync(
			ShowControlCueListId cueListId,
			CancellationToken cancellationToken = default)
		{
			ShowControlMutationCalls++;
			return ValueTask.FromException<ShowControlWorkspaceSnapshot>(new InvalidOperationException("Unexpected show-control mutation."));
		}

		public ValueTask<ShowControlWorkspaceSnapshot> ArmShowControlAsync(CancellationToken cancellationToken = default)
		{
			ShowControlMutationCalls++;
			return ValueTask.FromException<ShowControlWorkspaceSnapshot>(new InvalidOperationException("Unexpected show-control mutation."));
		}

		public ValueTask<ShowControlWorkspaceSnapshot> GoShowControlAsync(CancellationToken cancellationToken = default)
		{
			ShowControlMutationCalls++;
			return ValueTask.FromException<ShowControlWorkspaceSnapshot>(new InvalidOperationException("Unexpected show-control mutation."));
		}

		public ValueTask<ShowControlWorkspaceSnapshot> CancelShowControlAsync(CancellationToken cancellationToken = default)
		{
			ShowControlMutationCalls++;
			return ValueTask.FromException<ShowControlWorkspaceSnapshot>(new InvalidOperationException("Unexpected show-control mutation."));
		}
	}
}
