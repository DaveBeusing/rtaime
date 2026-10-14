// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class TimelineErgonomicsTests
{
	[Fact]
	public void Snap_prefers_nearest_then_cue_clip_playhead_with_integral_frame_fallback()
	{
		var targets = new[] { new TimelineSnapTarget(48, TimelineSnapKind.Playhead), new TimelineSnapTarget(52, TimelineSnapKind.Clip), new TimelineSnapTarget(48, TimelineSnapKind.Cue) };
		Assert.Equal(new TimelineSnapResult(48, TimelineSnapKind.Cue, true), TimelineSnapPolicy.SnapFrame(50, targets, 3, 100, true));
		Assert.Equal(52, TimelineSnapPolicy.SnapFrame(51, targets, 3, 100, true).Frame);
		Assert.Equal(new TimelineSnapResult(50, TimelineSnapKind.Frame, false), TimelineSnapPolicy.SnapFrame(50, targets, 3, 100, false));
		Assert.Equal(0, TimelineSnapPolicy.SnapFrame(-9, targets, 3, 100, true).Frame);
		Assert.Equal(99, TimelineSnapPolicy.SnapFrame(long.MaxValue, targets, 3, 100, false).Frame);
		Assert.Equal(0, TimelineSnapPolicy.SnapFrame(1, targets, 3, 1, true).Frame);
	}
	[Theory]
	[InlineData(1)]
	[InlineData(1.25)]
	[InlineData(1.5)]
	[InlineData(2)]
	public void Pointer_rounding_and_bounds_are_independent_of_monitor_scale(double dpi)
	{
		Assert.Equal(51, MediaTimelineGeometry.FrameFromPhysicalPosition(505 * dpi, 1000 * dpi, dpi, 101));
		Assert.Equal(0, MediaTimelineGeometry.FrameFromPhysicalPosition(-1, 1000 * dpi, dpi, 101));
		Assert.Equal(100, MediaTimelineGeometry.FrameFromPhysicalPosition(2000 * dpi, 1000 * dpi, dpi, 101));
	}

	[Fact]
	public async Task Local_cursor_drag_undo_and_redo_never_seek_and_marker_changes_invalidate_history()
	{
		var sent = new List<long>();
		var controller = new MediaTimelineController((command, _) =>
		{
			sent.Add(command.TargetFrame!.Value);
			return ValueTask.FromResult(MediaTransportCommandResult.Accepted(Snapshot(command.TargetFrame.Value).Transport!));
		});
		await using var model = new MediaTimelineViewModel(controller);
		Apply(model, Snapshot(10));
		model.EditCursorMode = true; model.SnapEnabled = false;
		model.BeginPointerSeek(); model.PreviewPointerSeek(75, 100);
		Assert.Equal(74, model.EditCursorFrame);
		Assert.Equal(10, model.ConfirmedFrame);
		await model.CompletePointerSeekAsync(75, 100);
		Assert.True(model.UndoDraftCommand.CanExecute(null));
		model.UndoDraftCommand.Execute(null);
		Assert.False(model.HasEditCursor);
		model.RedoDraftCommand.Execute(null);
		Assert.Equal(74, model.EditCursorFrame);
		Assert.Empty(sent);
		model.ZoomInCommand.Execute(null);
		model.SetEditCursor(99);
		Assert.Equal(99, model.VisibleEndFrame);
		Apply(model, Snapshot(12)); // Transport movement is evidence, not an authoring edit.
		Assert.Equal(99, model.VisibleEndFrame);
		Assert.True(model.UndoDraftCommand.CanExecute(null));
		Apply(model, Snapshot(12, changedMarkers: true));
		Assert.False(model.UndoDraftCommand.CanExecute(null));
		Assert.False(model.RedoDraftCommand.CanExecute(null));
		Assert.Empty(sent);
	}

	[Fact]
	public async Task Explicit_cursor_seek_requires_current_preview_excludes_program_and_is_not_undoable()
	{
		var calls = 0;
		var controller = new MediaTimelineController((command, _) =>
		{
			calls++;
			return ValueTask.FromResult(MediaTransportCommandResult.Accepted(Snapshot(command.TargetFrame!.Value).Transport!));
		});
		await using var model = new MediaTimelineViewModel(controller);
		Apply(model, Snapshot(10));
		model.SetEditCursor(60);
		Assert.False(model.SeekEditCursorCommand.CanExecute(null));
		model.SetConfirmedRoutingContext(Source.ToString(), "other-program", true);
		Assert.True(model.SeekEditCursorCommand.CanExecute(null));
		model.SeekEditCursorCommand.Execute(null);
		Assert.Equal(1, calls);
		Assert.Equal(60, model.ConfirmedFrame);
		Assert.False(model.UndoDraftCommand.CanExecute(null));
		Apply(model, Snapshot(60, onProgram: true));
		model.SetConfirmedRoutingContext(Source.ToString(), Source.ToString(), true);
		Assert.False(model.SeekEditCursorCommand.CanExecute(null));
		Assert.Contains("PROGRAM CONFIRMED", model.ProgramPositionLabel);
		model.SetConfirmedRoutingContext(Source.ToString(), Source.ToString(), false);
		Assert.Contains("unavailable", model.PreviewPositionLabel);
		Assert.False(model.SeekEditCursorCommand.CanExecute(null));
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task Draft_history_is_bounded_and_cancelled_drag_restores_local_cursor_only()
	{
		await using var model = new MediaTimelineViewModel(new MediaTimelineController());
		Apply(model, Snapshot(10)); model.EditCursorMode = true; model.SnapEnabled = false;
		for (var frame = 0; frame < 50; frame++) model.SetEditCursor(frame);
		var count = 0;
		while (model.UndoDraftCommand.CanExecute(null)) { model.UndoDraftCommand.Execute(null); count++; }
		Assert.Equal(32, count);
		model.SetEditCursor(25);
		model.BeginPointerSeek(); model.PreviewPointerSeek(80, 100); model.CancelPointerEdit();
		Assert.Equal(25, model.EditCursorFrame);
		Assert.Equal(10, model.ConfirmedFrame);
		Assert.False(model.Controller.State.HasPendingSeek);
		model.PreviewPointerSeek(double.NaN, 100);
		Assert.Equal(25, model.EditCursorFrame);
	}

	[Fact]
	public async Task Range_toggle_and_select_all_stay_presentation_only()
	{
		await using var model = new MediaTimelineViewModel(new MediaTimelineController());
		Apply(model, Snapshot(10));
		var track = model.Tracks.First();
		var a = new TimelineTrackItemViewModel("a", TimelineTrackCategory.Graphics, "a", 0, 100, null, null, "graphics", "PROJECTED", false);
		var b = a with { Id = "b", Label = "b" };
		var c = a with { Id = "c", Label = "c" };
		track.Items.Add(a); track.Items.Add(b); track.Items.Add(c);
		model.SelectItem(a); model.SelectItem(c, extendSelection: true, rangeSelection: true);
		Assert.Equal(3, model.SelectedItemCount);
		model.SelectItem(b, extendSelection: true, toggleSelection: true);
		Assert.Equal(2, model.SelectedItemCount);
		model.SelectAllItemsCommand.Execute(null);
		Assert.Equal(4, model.SelectedItemCount);
		model.ClearSelectionCommand.Execute(null);
		Assert.Empty(model.SelectedItems);
		Assert.Equal(10, model.ConfirmedFrame);
	}

	[Fact]
	public async Task Rundown_undo_restores_only_authoring_and_refresh_clears_history_before_live_execution()
	{
		var transport = new RundownTransport();
		var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel(client);
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var pool = new MediaPoolInspectorViewModel(owner, deck);
		var model = new RundownViewModel(client, owner, pool);
		model.AddHoldCommand.Execute(null);
		model.AddHoldCommand.Execute(null);
		var selected = model.SelectedItem!.Id;
		model.MoveUpCommand.Execute(null);
		Assert.Equal(selected, model.Items[0].Id);
		model.UndoDraftCommand.Execute(null);
		Assert.Equal(selected, model.Items[1].Id);
		model.RedoDraftCommand.Execute(null);
		Assert.Equal(selected, model.Items[0].Id);
		Assert.Equal(0, transport.Calls);
		transport.Current = new RundownWorkspaceSnapshot(new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Remote", model.Items.Select(i => i.Item).ToArray()),
			RundownExecutionSnapshot.Idle with { State = RundownExecutionState.Executing, Revision = 42 }, 9);
		await model.RefreshAsync();
		Assert.False(model.UndoDraftCommand.CanExecute(null));
		Assert.False(model.RedoDraftCommand.CanExecute(null));
		Assert.False(model.AddHoldCommand.CanExecute(null));
		Assert.False(model.CanEditAuthoring);
		model.Name = "Cannot rename active production";
		Assert.Equal("Remote", model.Name);
		Assert.Equal(1, transport.Calls);
	}

	internal static async Task VerifyWindowAsync(MainWindow owner, Dispatcher dispatcher)
	{
		owner.Shell.SelectWorkspace("EDIT");
		await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
		var control = Descendants(owner).OfType<MediaTimelineControl>().First();
		var original = control.DataContext;
		var controller = new MediaTimelineController();
		await using var model = new MediaTimelineViewModel(controller);
		Apply(model, Snapshot(10)); model.EditCursorMode = true; model.SnapEnabled = false;
		control.DataContext = model;
		try
		{
			control.Focus();
			await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
			var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			control.RaiseEvent(key);
			Assert.True(key.Handled); Assert.Equal(11, model.EditCursorFrame); Assert.Equal(10, model.ConfirmedFrame);
			model.UndoDraftCommand.Execute(null);
			Assert.False(model.HasEditCursor);
			Console.WriteLine("TIMELINE ERGONOMICS PASS: real themed timeline, focused edit-cursor key, local undo and unchanged confirmed transport.");
		}
		finally { control.DataContext = original; }
	}
	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is null) continue;
			yield return child;
			foreach (var descendant in Descendants(child)) yield return descendant;
		}
	}
	private static readonly MediaAssetId Asset = new(Identity.Parse("45000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId Source = new(Identity.Parse("45000000-0000-0000-0000-000000000002"));
	private static void Apply(MediaTimelineViewModel model, MediaDeckSnapshot snapshot)
	{
		model.Controller.ApplyConfirmedSnapshot(snapshot.Transport!);
		model.ApplyMediaDeckSnapshot(snapshot);
	}
	private static MediaDeckSnapshot Snapshot(long frame, bool changedMarkers = false, bool onProgram = false)
	{
		var duration = TimeSpan.FromSeconds(2); var position = TimeSpan.FromMilliseconds(frame * 20);
		var transport = new MediaTransportSnapshot(MediaContractVersion.Current, Asset, Source, MediaTransportState.Paused,
			new MediaTransportPosition(frame, 100, position, duration, duration - position, FrameRate.Fps50), null, isOnProgram: onProgram);
		var probe = new LocalMediaProbe(MediaContractVersion.Current, Asset, Source, "reference.mp4", MediaContainerFormat.Mp4,
			MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, duration);
		var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, Asset, 100, changedMarkers ? 11 : 10, 90,
			[new MediaCuePoint(new MediaCuePointId(Identity.Parse("45000000-0000-0000-0000-000000000003")), "Cue", 20)]);
		return new(MediaContractVersion.Current, MediaDeckState.Paused, Source, probe, transport, markers);
	}
	private sealed class RundownTransport : IOperatorControlTransport
	{
		public int Calls;
		public RundownWorkspaceSnapshot Current = new(null, RundownExecutionSnapshot.Idle, 0);
		public ValueTask<RundownWorkspaceSnapshot> GetRundownSnapshotAsync(CancellationToken cancellationToken = default)
		{ Calls++; return ValueTask.FromResult(Current); }
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<OperatorStatusSnapshot>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
	}
}
