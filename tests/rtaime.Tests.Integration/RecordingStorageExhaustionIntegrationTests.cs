// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RecordingStorageExhaustionIntegrationTests
{
	[Fact]
	public async Task Storage_exhaustion_fails_recording_without_stopping_committed_program()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-recording-exhaustion", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var sourceA = new ProductionSourceId(Identity.Parse("7c000000-0000-0000-0000-00000000000a"));
			var sourceB = new ProductionSourceId(Identity.Parse("7c000000-0000-0000-0000-00000000000b"));
			var specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("7c000000-0000-0000-0000-000000000001")),
				"V1 Recording Storage Exhaustion",
				new[]
				{
					new ProductionSourceSpecification(sourceA, "Input 1"),
					new ProductionSourceSpecification(sourceB, "Input 2")
				},
				new ProductionRoutingState(sourceA, sourceA));

			var writer = new ReferenceRecordingPayloadWriter(root, maximumPayloadBytes: 1);
			await using var runtime = new V1RuntimeHostService(
				new MediaSourceId(sourceA.Value),
				new MediaSourceId(sourceB.Value),
				VideoFormat.Hd1080p50Rgba8,
				writer);
			await using var journal = new BoundedProductionJournal(64);
			var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
			CommitInitialization(control, runtime);

			var start = await runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New());
			Assert.True(start.Succeeded, start.Failure?.ToString());
			var first = runtime.ProcessNextBoundary();
			Assert.NotNull(first.Recording);
			Assert.True(first.Recording!.Accepted, first.Recording.Failure?.ToString());

			await WaitForAsync(() => runtime.Snapshot.Recording.State == RecordingLifecycleState.Failed);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Snapshot.Runtime.Status);
			Assert.True(runtime.Snapshot.Recording.Failure.HasValue);
			Assert.Equal("recording.write.writer_failure", runtime.Snapshot.Recording.Failure.Value.Code);
			Assert.Contains(runtime.Snapshot.Recording.Failure.Value.Message, "quota", StringComparison.OrdinalIgnoreCase);
			Assert.False(File.Exists(writer.FinalPath));
			Assert.False(File.Exists(writer.PartialPath));

			var second = runtime.ProcessNextBoundary();
			Assert.Equal(first.SequenceNumber + 1, second.SequenceNumber);
			Assert.Equal(first.CommittedProgramSourceId, second.CommittedProgramSourceId);
			Assert.True(second.Audio.Emitted, second.Audio.Failure?.ToString());
			Assert.Null(second.Recording);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Snapshot.Runtime.Status);
			Assert.True(runtime.ProgramFrames.Count >= 2);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	private static void CommitInitialization(ControlHostService control, V1RuntimeHostService runtime)
	{
		var staged = control.Initialize();
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		var execution = Assert.IsType<ControlHostExecutionPackage>(staged.Execution);
		var applied = runtime.ApplyExecution(execution.PreparedExecution, execution.ProgramSinkId, execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		var commit = Assert.IsType<RuntimeCommitResult>(applied.Commit);
		var confirmed = control.ConfirmRuntimeCommit(execution.PreparedExecution.PreparedExecutionId, commit);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		for (var attempt = 0; attempt < 200; attempt++)
		{
			if (condition())
				return;
			await Task.Delay(10);
		}

		throw new Xunit.Sdk.XunitException("Timed out waiting for recording storage exhaustion failure state.");
	}
}
