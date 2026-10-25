// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;
using Xunit.Abstractions;

namespace rtaime.Tests.Operator;

public sealed class MediaTransportFailureOperatorTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData("io")]
	[InlineData("timeout")]
	[InlineData("session")]
	[InlineData("snapshot")]
	[InlineData("rpc")]
	public Task Media_commands_contain_transport_faults_and_reconcile_without_replaying(string failure) =>
		RunDispatcherAsync(async (dispatcher, unhandled) =>
		{
			foreach (var action in new[] { "step-back", "step-forward", "start", "end", "previous", "next",
				"selected", "edit-cursor", "in", "out", "cue", "play", "pause", "stop", "open", "close",
				"policy", "set-in", "set-out", "clear-in", "clear-out", "add-cue", "rename-cue", "delete-cue",
				"pointer-release", "trim-in", "trim-out" })
			{
				output.WriteLine($"Media command failure case: failure={failure}, action={action}");
				var transport = new FaultingTransport(failure) { Current = Snapshot(30, action == "pause") };
				await using var controller = new MediaDeckController(new OperatorControlClient(transport));
				await using var model = new MediaDeckViewModel(controller, () => @"C:\media\reference.mp4", () => Source.ToString());
				controller.ApplyConfirmedSnapshot(transport.Current);
				model.SelectedCue = model.Cues[0];
				model.Timeline.SelectCue(model.Timeline.Cues[0]);
				model.CueName = "Keep draft name";
				model.AutoPlayOnProgram = false;
				model.Timeline.SetEditCursor(65);
				model.Timeline.SetConfirmedRoutingContext(Source.ToString(), "other-program", true);
				model.Timeline.SnapEnabled = false;
				var confirmed = controller.Snapshot;

				if (action == "pointer-release")
				{
					model.Timeline.BeginPointerSeek();
					await model.Timeline.CompletePointerSeekAsync(75, 100);
				}
				else if (action == "trim-in")
					Assert.False(await model.Timeline.TrimInAsync(20, 100));
				else if (action == "trim-out")
					Assert.False(await model.Timeline.TrimOutAsync(80, 100));
				else
				{
					var command = Command(model, action);
					Assert.True(command.CanExecute(null), action);
					command.Execute(null);
				}

				// Uncertainty is published before the command continuation posts its busy cleanup.
				await UntilAsync(() => (model.RequiresSynchronization && !model.IsBusy) || unhandled.Count > 0);
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
				Assert.Empty(unhandled);
				Assert.True(model.RequiresSynchronization, action);
				Assert.False(model.IsBusy, $"failure={failure}, action={action}: command cleanup is incomplete");
				Assert.Contains("UNCONFIRMED", model.LastError);
				Assert.Same(confirmed, controller.Snapshot);
				Assert.Equal(30, model.Timeline.ConfirmedFrame);
				Assert.Equal(30, model.Timeline.DisplayFrame);
				Assert.False(model.Timeline.HasPendingSeek);
				Assert.False(model.CanSeek);
				Assert.False(model.PlayCommand.CanExecute(null));
				Assert.False(model.OpenCommand.CanExecute(null));
				Assert.False(model.CloseCommand.CanExecute(null));
				Assert.Equal("Keep draft name", model.CueName);
				Assert.False(model.AutoPlayOnProgram);
				model.PlayCommand.Execute(null);
				model.Timeline.StepForwardCommand.Execute(null);
				Assert.Equal(1, transport.Mutations);

				transport.Failure = null;
				transport.Current = Snapshot(42);
				Assert.True(model.RefreshCommand.CanExecute(null));
				model.RefreshCommand.Execute(null);
				// Relay execution flags unwind after the view-model task completes.
				await UntilAsync(() => (!model.IsBusy && !model.RequiresSynchronization &&
					model.RefreshCommand.CanExecute(null) && model.PlayCommand.CanExecute(null) &&
					model.Timeline.StepForwardCommand.CanExecute(null)) || unhandled.Count > 0);
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
				Assert.Empty(unhandled);
				Assert.Equal(new[] { "full", "deck" }, transport.Reads);
				Assert.Equal(42, model.Timeline.ConfirmedFrame);
				Assert.True(model.PlayCommand.CanExecute(null));
				Assert.True(model.Timeline.StepForwardCommand.CanExecute(null));
				Assert.Null(model.LastError);
				Assert.Equal(1, transport.Mutations);
			}
		});

	[Fact]
	public Task Unconfirmed_seek_disables_commands_before_queued_timeline_projection_is_delivered() =>
		RunDispatcherAsync(async (_, unhandled) =>
		{
			var transport = new FaultingTransport("rpc") { Current = Snapshot(30) };
			await using var controller = new MediaDeckController(new OperatorControlClient(transport));
			var heldContext = new HeldSynchronizationContext();
			var previousContext = SynchronizationContext.Current;
			MediaDeckViewModel model;
			try
			{
				SynchronizationContext.SetSynchronizationContext(heldContext);
				model = new MediaDeckViewModel(controller, () => null, () => Source.ToString());
			}
			finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
			await using var modelLifetime = model;
			controller.ApplyConfirmedSnapshot(transport.Current);
			heldContext.DeliverAll();
			Assert.True(model.CanSeek);
			Assert.True(model.JumpInCommand.CanExecute(null));

			await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => controller.Markers.JumpToInAsync().AsTask());
			Assert.True(heldContext.PendingCount > 0);
			Assert.True(model.RequiresSynchronization);
			Assert.False(model.IsBusy);
			Assert.Null(model.Timeline.Failure); // The previous healthy projection is deliberately still visible.
			Assert.Equal(30, model.Timeline.ConfirmedFrame);
			Assert.False(model.CanSeek);
			Assert.False(model.JumpInCommand.CanExecute(null));
			Assert.False(model.JumpOutCommand.CanExecute(null));
			Assert.False(model.Timeline.StepForwardCommand.CanExecute(null));
			model.JumpInCommand.Execute(null);
			model.Timeline.StepForwardCommand.Execute(null);
			Assert.Equal(1, transport.Mutations);

			heldContext.DeliverAll();
			Assert.Contains("UNCONFIRMED", model.Timeline.Failure);
			transport.Failure = null;
			transport.Current = Snapshot(42);
			await controller.RefreshAsync();
			Assert.False(model.RequiresSynchronization);
			Assert.True(heldContext.PendingCount > 0);
			Assert.False(model.CanSeek); // Re-enable only after the confirmed projection is delivered.
			heldContext.DeliverAll();
			Assert.Equal(new[] { "full", "deck" }, transport.Reads);
			Assert.Equal(42, model.Timeline.ConfirmedFrame);
			Assert.True(model.CanSeek);
			Assert.True(model.JumpInCommand.CanExecute(null));
			Assert.True(model.Timeline.StepForwardCommand.CanExecute(null));
			Assert.Equal(1, transport.Mutations);
			Assert.Empty(unhandled);
		});

	[Theory]
	[InlineData("step-forward", false)]
	[InlineData("play", false)]
	[InlineData("set-in", false)]
	[InlineData("step-forward", true)]
	[InlineData("play", true)]
	[InlineData("set-in", true)]
	public Task Closing_the_window_contains_cancelled_and_successful_racing_replies(string action, bool successfulReply) =>
		RunDispatcherAsync(async (dispatcher, unhandled) =>
		{
			var transport = new FaultingTransport(null) { BlockUntilCancellation = true, SuccessAfterCancellation = successfulReply };
			await using var controller = new MediaDeckController(new OperatorControlClient(transport));
			var model = new MediaDeckViewModel(controller, () => null, () => Source.ToString());
			controller.ApplyConfirmedSnapshot(transport.Current);
			var window = new Window { Width = 200, Height = 120, ShowInTaskbar = false, DataContext = model };
			try
			{
				window.Show();
				Command(model, action).Execute(null);
				await transport.Started.Task;
				window.Close();
				await model.DisposeAsync();
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
				Assert.Empty(unhandled);
				Assert.Equal(1, transport.Mutations);
				Assert.False(dispatcher.HasShutdownStarted);
			}
			finally
			{
				if (window.IsVisible) window.Close();
			}
		});

	private static ICommand Command(MediaDeckViewModel model, string action) => action switch
	{
		"step-back" => model.Timeline.StepBackwardCommand,
		"step-forward" => model.Timeline.StepForwardCommand,
		"start" => model.Timeline.JumpToStartCommand,
		"end" => model.Timeline.JumpToEndCommand,
		"previous" => model.Timeline.PreviousCueCommand,
		"next" => model.Timeline.NextCueCommand,
		"selected" => model.Timeline.JumpSelectedCueCommand,
		"edit-cursor" => model.Timeline.SeekEditCursorCommand,
		"in" => model.JumpInCommand,
		"out" => model.JumpOutCommand,
		"cue" => model.JumpCueCommand,
		"play" => model.PlayCommand,
		"pause" => model.PauseCommand,
		"stop" => model.StopCommand,
		"open" => model.OpenCommand,
		"close" => model.CloseCommand,
		"policy" => model.ApplyPlaybackPolicyCommand,
		"set-in" => model.SetInCommand,
		"set-out" => model.SetOutCommand,
		"clear-in" => model.ClearInCommand,
		"clear-out" => model.ClearOutCommand,
		"add-cue" => model.AddCueCommand,
		"rename-cue" => model.RenameCueCommand,
		"delete-cue" => model.DeleteCueCommand,
		_ => throw new ArgumentOutOfRangeException(nameof(action))
	};

	private static async Task UntilAsync(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		while (!condition()) await Task.Delay(1, timeout.Token);
	}

	private sealed class HeldSynchronizationContext : SynchronizationContext
	{
		private readonly object _gate = new();
		private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = [];
		public int PendingCount { get { lock (_gate) return _pending.Count; } }

		public override void Post(SendOrPostCallback callback, object? state)
		{
			lock (_gate) _pending.Enqueue((callback, state));
		}

		public void DeliverAll()
		{
			var previous = Current;
			SetSynchronizationContext(this);
			try
			{
				while (true)
				{
					(SendOrPostCallback Callback, object? State) pending;
					lock (_gate)
					{
						if (!_pending.TryDequeue(out pending)) return;
					}
					pending.Callback(pending.State);
				}
			}
			finally { SetSynchronizationContext(previous); }
		}
	}

	private static async Task RunDispatcherAsync(Func<Dispatcher, List<Exception>, Task> action)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
			var unhandled = new List<Exception>();
			Exception? failure = null;
			dispatcher.UnhandledException += (_, args) => { unhandled.Add(args.Exception); args.Handled = true; };
			dispatcher.BeginInvoke(new Action(async () =>
			{
				try { await action(dispatcher, unhandled); Assert.Empty(unhandled); }
				catch (Exception exception) { failure = exception; }
				finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
			}));
			Dispatcher.Run();
			if (failure is null) completion.TrySetResult();
			else completion.TrySetException(failure);
		}) { IsBackground = true, Name = "Media command transport qualification" };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private static readonly MediaAssetId Asset = new(Identity.Parse("46000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId Source = new(Identity.Parse("46000000-0000-0000-0000-000000000002"));
	private static MediaDeckSnapshot Snapshot(long frame, bool playing = false)
	{
		var duration = TimeSpan.FromSeconds(2);
		var position = TimeSpan.FromMilliseconds(frame * 20);
		var transport = new MediaTransportSnapshot(MediaContractVersion.Current, Asset, Source,
			playing ? MediaTransportState.Playing : MediaTransportState.Paused,
			new MediaTransportPosition(frame, 100, position, duration, duration - position, FrameRate.Fps50), null);
		var probe = new LocalMediaProbe(MediaContractVersion.Current, Asset, Source, "reference.mp4", MediaContainerFormat.Mp4,
			MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, duration);
		var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, Asset, 100, 10, 90,
			[new MediaCuePoint(MediaCuePointId.New(), "Before", 20), new MediaCuePoint(MediaCuePointId.New(), "After", 40)]);
		return new(MediaContractVersion.Current, playing ? MediaDeckState.Playing : MediaDeckState.Paused,
			Source, probe, transport, markers);
	}

	private sealed class FaultingTransport(string? failure) : IOperatorControlTransport
	{
		public string? Failure = failure;
		public bool BlockUntilCancellation;
		public bool SuccessAfterCancellation;
		public int Mutations;
		public MediaDeckSnapshot Current = Snapshot(30);
		public List<string> Reads { get; } = [];
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		private async ValueTask<MediaDeckSnapshot> MutateAsync(CancellationToken token)
		{
			Interlocked.Increment(ref Mutations);
			Started.TrySetResult();
			await Task.Yield();
			if (BlockUntilCancellation)
			{
				if (!SuccessAfterCancellation) await Task.Delay(Timeout.Infinite, token);
				var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				using var registration = token.Register(() => cancelled.TrySetResult());
				await cancelled.Task;
				return Current;
			}
			if (Failure == "timeout") await Task.Delay(5, token);
			if (Failure is not null) throw Failure switch
			{
				"io" => (Exception)new IOException("Connection lost after dispatch"),
				"timeout" => new TimeoutException("Command deadline elapsed"),
				"session" => new RemoteHostSessionChangedException("old", "replacement"),
				"snapshot" => new ControlSnapshotRequiredException("Full snapshot required"),
				"rpc" => new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "Peer unavailable")),
				_ => new ArgumentOutOfRangeException(nameof(Failure))
			};
			return Current;
		}

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			Reads.Add("full");
			var source = ProductionSourceId.New();
			return ValueTask.FromResult(new OperatorStatusSnapshot(
				new(ControlContractVersion.Current, ProductionId.New(), new Revision(1), new(source, source)),
				[new(source.ToString(), "Camera")], "READY", "READY", "READY", "UNKNOWN", "IDLE", false, 0, mediaDeck: Current));
		}
		public ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default)
		{
			Reads.Add("deck");
			return ValueTask.FromResult(Current);
		}
		public ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(MediaSourceId source, CancellationToken cancellationToken = default) =>
			GetMediaDeckSnapshotAsync(cancellationToken);
		public ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(MediaDeckOpenRequest request, CancellationToken cancellationToken = default) => MutateAsync(cancellationToken);
		public ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(MediaTransportCommand command, CancellationToken cancellationToken = default) => MutateAsync(cancellationToken);
		public ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(MediaMarkerCommand command, CancellationToken cancellationToken = default) => MutateAsync(cancellationToken);
		public ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) => MutateAsync(cancellationToken);
		public ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(MediaSourceId source, CancellationToken cancellationToken = default) => MutateAsync(cancellationToken);
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
