// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProgramFrameMemoryOwnershipTests
{
	[Fact]
	public async Task Repeated_program_boundaries_reuse_a_bounded_readback_buffer()
	{
		await using var fixture = CreateFixture(new NoopRecordingWriter());

		for (var index = 0; index < 96; index++)
		{
			using (var boundary = fixture.Runtime.ProcessNextBoundary())
			{
				Assert.Equal((ulong)index, boundary.SequenceNumber);
				Assert.Equal(1, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
			}

			Assert.Equal(0, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
		}

		var statistics = fixture.Runtime.ProgramReadbackPoolStatistics;
		Assert.Equal(V1RuntimeHostService.ProgramReadbackBufferCapacity, statistics.Capacity);
		Assert.Equal(1, statistics.AllocatedBuffers);
		Assert.Equal(1, statistics.AvailableBuffers);
		Assert.Equal(96UL, statistics.TotalRents);
		Assert.Equal(0UL, statistics.ExhaustedRents);
	}

	[Fact]
	public async Task Retained_program_boundary_pixels_are_not_mutated_by_later_boundaries()
	{
		await using var fixture = CreateFixture(new NoopRecordingWriter());
		var first = fixture.Runtime.ProcessNextBoundary();
		try
		{
			var firstHash = SHA256.HashData(first.ProgramPixels.Span);
			fixture.Runtime.SetExternalInputContent(
				fixture.MediaSourceA,
				RgbaFrameBuffer.Solid(fixture.Format, 240, 12, 24, 255));

			using var second = fixture.Runtime.ProcessNextBoundary();
			var secondHash = SHA256.HashData(second.ProgramPixels.Span);

			Assert.NotEqual(firstHash, secondHash);
			Assert.Equal(firstHash, SHA256.HashData(first.ProgramPixels.Span));
			Assert.Equal(2, fixture.Runtime.ProgramReadbackPoolStatistics.AllocatedBuffers);
			Assert.Equal(2, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
		}
		finally
		{
			first.Dispose();
		}

		Assert.Equal(0, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
	}

	[Fact]
	public async Task Asynchronous_recording_keeps_its_program_lease_until_the_writer_finishes()
	{
		var writer = new BlockingPayloadWriter();
		await using var fixture = CreateFixture(writer);
		try
		{
			var start = await fixture.Runtime.StartRecordingAsync(
				RecordingSessionId.New(),
				RecordingOutputId.New());
			Assert.True(start.Succeeded, start.Failure?.ToString());

			var first = fixture.Runtime.ProcessNextBoundary();
			var firstHash = SHA256.HashData(first.ProgramPixels.Span);
			Assert.True(first.Recording is { Accepted: true });
			first.Dispose();

			await writer.WriteStarted.WaitAsync(TimeSpan.FromSeconds(3));

			fixture.Runtime.SetExternalInputContent(
				fixture.MediaSourceA,
				RgbaFrameBuffer.Solid(fixture.Format, 220, 40, 15, 255));
			using (var later = fixture.Runtime.ProcessNextBoundary())
				Assert.NotEqual(firstHash, SHA256.HashData(later.ProgramPixels.Span));

			writer.AllowWrites();
			var stop = await fixture.Runtime.StopRecordingAsync();
			Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
			Assert.True(writer.WrittenVideoHashes.Count >= 2);
			Assert.Equal(firstHash, writer.WrittenVideoHashes[0]);
			Assert.Equal(0, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
		}
		finally
		{
			writer.AllowWrites();
		}
	}

	private static Fixture CreateFixture(IProgramRecordingWriter writer)
	{
		var format = VideoFormat.Hd1080p50Rgba8;
		var sourceA = new ProductionSourceId(Identity.Parse("d1000000-0000-0000-0000-00000000000a"));
		var sourceB = new ProductionSourceId(Identity.Parse("d1000000-0000-0000-0000-00000000000b"));
		var mediaA = new MediaSourceId(sourceA.Value);
		var mediaB = new MediaSourceId(sourceB.Value);
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Identity.Parse("d1000000-0000-0000-0000-000000000001")),
			"Program Frame Memory Ownership",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input 1"),
				new ProductionSourceSpecification(sourceB, "Input 2")
			},
			new ProductionRoutingState(sourceA, sourceA));

		var runtime = new V1RuntimeHostService(mediaA, mediaB, format, writer);
		var journal = new BoundedProductionJournal(64);
		var control = new ControlHostService(specification, runtime.ProviderDescriptors, journal);
		var staged = control.Initialize();
		Assert.True(staged.Accepted, staged.Failure?.ToString());
		Assert.NotNull(staged.Execution);
		var execution = staged.Execution!;
		var applied = runtime.ApplyExecution(
			execution.PreparedExecution,
			execution.ProgramSinkId,
			execution.ProgramTransition);
		Assert.True(applied.Committed, applied.Commit?.Failure?.ToString() ?? applied.Prepare.Failure?.ToString());
		Assert.NotNull(applied.Commit);
		var confirmed = control.ConfirmRuntimeCommit(
			execution.PreparedExecution.PreparedExecutionId,
			applied.Commit!);
		Assert.True(confirmed.Committed, confirmed.Failure?.ToString());

		return new Fixture(format, mediaA, runtime, journal);
	}

	private sealed class Fixture : IAsyncDisposable
	{
		public Fixture(
			VideoFormat format,
			MediaSourceId mediaSourceA,
			V1RuntimeHostService runtime,
			BoundedProductionJournal journal)
		{
			Format = format;
			MediaSourceA = mediaSourceA;
			Runtime = runtime;
			Journal = journal;
		}

		public VideoFormat Format { get; }
		public MediaSourceId MediaSourceA { get; }
		public V1RuntimeHostService Runtime { get; }
		public BoundedProductionJournal Journal { get; }

		public async ValueTask DisposeAsync()
		{
			await Runtime.DisposeAsync();
			await Journal.DisposeAsync();
		}
	}

	private sealed class NoopRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}

	private sealed class BlockingPayloadWriter : IProgramRecordingPayloadWriter
	{
		private readonly object _gate = new();
		private readonly Dictionary<ulong, IProgramRecordingPayloadLease> _staged = new();
		private readonly List<byte[]> _writtenVideoHashes = new();
		private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _allowWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task WriteStarted => _writeStarted.Task;
		public IReadOnlyList<byte[]> WrittenVideoHashes
		{
			get
			{
				lock (_gate)
					return _writtenVideoHashes.Select(hash => hash.ToArray()).ToArray();
			}
		}

		public void AllowWrites() => _allowWrites.TrySetResult();

		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public void StagePayload(
			ulong sequenceNumber,
			IProgramRecordingPayloadLease videoPayload,
			ReadOnlyMemory<byte> audioPayload)
		{
			ArgumentNullException.ThrowIfNull(videoPayload);
			lock (_gate)
			{
				if (!_staged.TryAdd(sequenceNumber, videoPayload))
					throw new InvalidOperationException($"Duplicate staged payload '{sequenceNumber}'.");
			}
		}

		public void DiscardPayload(ulong sequenceNumber)
		{
			IProgramRecordingPayloadLease? lease = null;
			lock (_gate)
			{
				if (_staged.Remove(sequenceNumber, out var staged))
					lease = staged;
			}
			lease?.Dispose();
		}

		public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
		{
			IProgramRecordingPayloadLease lease;
			lock (_gate)
			{
				if (!_staged.Remove(sample.SequenceNumber, out var staged))
					throw new InvalidOperationException($"Missing staged payload '{sample.SequenceNumber}'.");
				lease = staged;
			}

			try
			{
				_writeStarted.TrySetResult();
				await _allowWrites.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
				var hash = SHA256.HashData(lease.Memory.Span);
				lock (_gate)
					_writtenVideoHashes.Add(hash);
			}
			finally
			{
				lease.Dispose();
			}
		}

		public ValueTask FinalizeAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask AbortAsync(CancellationToken cancellationToken)
		{
			_ = cancellationToken;
			IProgramRecordingPayloadLease[] leases;
			lock (_gate)
			{
				leases = _staged.Values.ToArray();
				_staged.Clear();
			}
			foreach (var lease in leases)
				lease.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
