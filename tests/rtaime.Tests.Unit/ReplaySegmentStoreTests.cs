// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class ReplaySegmentStoreTests
{
	[Fact]
	public void Retention_window_evicts_oldest_finalized_segment()
	{
		using var fixture = new Fixture(new ReplayBufferPolicy(TimeSpan.FromSeconds(4), 1024, TimeSpan.FromSeconds(2)));
		var first = fixture.Add(1, 10, TimeSpan.Zero, TimeSpan.FromSeconds(2), 64);
		var second = fixture.Add(11, 20, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 64);
		var third = fixture.Add(21, 30, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), 64);

		var retained = fixture.Store.Snapshot();

		Assert.Equal([second.SegmentId, third.SegmentId], retained.Select(segment => segment.SegmentId));
		Assert.False(File.Exists(first.Path));
		Assert.Equal(1UL, fixture.Store.EvictedSegments);
	}

	[Fact]
	public void Pinned_history_is_not_evicted_to_admit_new_storage()
	{
		using var fixture = new Fixture(new ReplayBufferPolicy(TimeSpan.FromMinutes(1), 100, TimeSpan.FromSeconds(2)));
		var first = fixture.Add(1, 10, TimeSpan.Zero, TimeSpan.FromSeconds(2), 80);

		using var pin = fixture.Store.PinRange(new ReplayRange(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1.5)));
		var candidate = fixture.Create(11, 20, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 80);

		Assert.Throws<IOException>(() => fixture.Store.AddFinalizedSegment(candidate));
		Assert.True(File.Exists(first.Path));
		Assert.False(File.Exists(candidate.Path));
		Assert.Single(fixture.Store.Snapshot());
	}

	[Fact]
	public void Discontinuous_history_cannot_be_materialized_as_continuous_range()
	{
		using var fixture = new Fixture(new ReplayBufferPolicy(TimeSpan.FromMinutes(1), 1024, TimeSpan.FromSeconds(2)));
		fixture.Add(1, 10, TimeSpan.Zero, TimeSpan.FromSeconds(2), 64);
		fixture.Add(11, 20, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 64, discontinuityBefore: true);

		var range = new ReplayRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

		Assert.False(fixture.Store.ContainsRange(range));
		Assert.Throws<InvalidOperationException>(() => fixture.Store.PinRange(range));
	}

	[Fact]
	public void Expired_range_is_rejected_deterministically()
	{
		using var fixture = new Fixture(new ReplayBufferPolicy(TimeSpan.FromSeconds(4), 1024, TimeSpan.FromSeconds(2)));
		fixture.Add(1, 10, TimeSpan.Zero, TimeSpan.FromSeconds(2), 64);
		fixture.Add(11, 20, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 64);
		fixture.Add(21, 30, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), 64);

		var expired = new ReplayRange(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1.5));

		Assert.False(fixture.Store.ContainsRange(expired));
		Assert.Throws<InvalidOperationException>(() => fixture.Store.PinRange(expired));
	}

	private sealed class Fixture : IDisposable
	{
		private readonly string _root = Path.Combine(Path.GetTempPath(), "rtaime-replay-store-tests", Guid.NewGuid().ToString("N"));

		public Fixture(ReplayBufferPolicy policy)
		{
			Directory.CreateDirectory(_root);
			Store = new RollingReplaySegmentStore(policy);
		}

		public RollingReplaySegmentStore Store { get; }

		public ReplaySegmentDescriptor Add(
			ulong firstSequence,
			ulong lastSequence,
			TimeSpan start,
			TimeSpan end,
			long bytes,
			bool discontinuityBefore = false)
		{
			var segment = Create(firstSequence, lastSequence, start, end, bytes, discontinuityBefore);
			Store.AddFinalizedSegment(segment);
			return segment;
		}

		public ReplaySegmentDescriptor Create(
			ulong firstSequence,
			ulong lastSequence,
			TimeSpan start,
			TimeSpan end,
			long bytes,
			bool discontinuityBefore = false)
		{
			var path = Path.Combine(_root, $"segment-{firstSequence}.mp4");
			File.WriteAllBytes(path, [1, 2, 3, 4]);
			return new ReplaySegmentDescriptor(
				ReplaySegmentId.New(),
				path,
				firstSequence,
				lastSequence,
				start,
				end,
				bytes,
				hasAudio: true,
				discontinuityBefore);
		}

		public void Dispose()
		{
			Store.Dispose();
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}
