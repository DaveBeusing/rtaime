// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Failure;

public sealed class RecordingFinalizeRecoveryTests
{
	[Fact]
	public async Task Finalize_failure_fails_session_and_a_later_session_can_recover()
	{
		var writer = new FailFinalizeOnceWriter();
		await using var recorder = new ProgramRecorder(writer, capacity: 4);

		var first = Request();
		Assert.True((await recorder.StartAsync(first)).Succeeded);
		Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
		var failedStop = await recorder.StopAsync();

		Assert.Equal(RecordingStopStatus.Failed, failedStop.Status);
		Assert.Equal(RecordingLifecycleState.Failed, recorder.Snapshot.State);
		Assert.True(recorder.Snapshot.Failure.HasValue);
		Assert.Equal("recording.finalize.writer_failure", recorder.Snapshot.Failure.Value.Code);
		Assert.Equal(1UL, recorder.Snapshot.Statistics.WriterFailures);
		Assert.Equal(1, writer.AbortCount);

		var second = Request();
		Assert.NotEqual(first.Output.OutputId, second.Output.OutputId);
		Assert.True((await recorder.StartAsync(second)).Succeeded);
		Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
		var recoveredStop = await recorder.StopAsync();

		Assert.Equal(RecordingStopStatus.Stopped, recoveredStop.Status);
		Assert.Equal(RecordingLifecycleState.Completed, recorder.Snapshot.State);
		Assert.False(recorder.Snapshot.Failure.HasValue);
		Assert.Equal(1UL, recorder.Snapshot.Statistics.Written);
		Assert.Equal(2, writer.OpenCount);
		Assert.Equal(2, writer.FinalizeCount);
	}

	private static RecordingStartRequest Request() => new(
		RecordingContractVersion.Current,
		RecordingSessionId.New(),
		new RecordingOutputDescriptor(RecordingOutputId.New(), MediaSinkId.New(), "Program"));

	private static FrameDescriptor Frame(ulong sequence) => new(
		MediaContractVersion.Current,
		MediaSourceId.New(),
		new SurfaceDescriptor(
			SurfaceId.New(),
			VideoFormat.Hd1080p50Rgba8,
			SurfaceStorageDomain.Shared,
			SurfaceOwnership.SharedLease,
			new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
			new OpaqueSurfaceHandle("recording.finalize.recovery", $"surface-{sequence}")),
		new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));

	private sealed class FailFinalizeOnceWriter : IProgramRecordingWriter
	{
		private bool _finalizeFailurePending = true;

		public int OpenCount { get; private set; }
		public int FinalizeCount { get; private set; }
		public int AbortCount { get; private set; }

		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
		{
			_ = request;
			cancellationToken.ThrowIfCancellationRequested();
			OpenCount++;
			return ValueTask.CompletedTask;
		}

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
		{
			_ = sample;
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask FinalizeAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			FinalizeCount++;
			if (_finalizeFailurePending)
			{
				_finalizeFailurePending = false;
				return ValueTask.FromException(new IOException("Simulated finalization failure."));
			}

			return ValueTask.CompletedTask;
		}

		public ValueTask AbortAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AbortCount++;
			return ValueTask.CompletedTask;
		}
	}
}
