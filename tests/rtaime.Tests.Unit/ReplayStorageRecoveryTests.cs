// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class ReplayStorageRecoveryTests
{
	[Fact]
	public async Task Published_storage_observations_do_not_wait_for_the_filesystem_mutation_gate()
	{
		using var directory = new ReplayDirectory();
		using var store = directory.Open();
		store.PrepareForCapture(directory.Root);
		var segment = directory.Create(1, 64);
		store.AddFinalizedSegment(segment);
		var gate = typeof(RollingReplaySegmentStore).GetField("_gate",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
		using var release = new ManualResetEventSlim();
		var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		// Hold the same gate used during a slow directory scan or deletion.
		var storage = Task.Run(() =>
		{
			lock (gate)
			{
				acquired.SetResult();
				release.Wait();
			}
		});
		try
		{
			await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var observed = await Task.Run(() => (
				Segments: store.Snapshot(), store.RetainedBytes, store.EvictedSegments, store.StorageFailure,
				ContainsRange: store.ContainsRange(new ReplayRange(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)))))
				.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(segment, Assert.Single(observed.Segments));
			Assert.Equal(64, observed.RetainedBytes);
			Assert.Equal(0UL, observed.EvictedSegments);
			Assert.Null(observed.StorageFailure);
			Assert.True(observed.ContainsRange);
		}
		finally
		{
			release.Set();
			await storage.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public void Capture_root_with_trailing_separator_retains_the_same_directory_identity()
	{
		using var directory = new ReplayDirectory();
		using var store = directory.Open();
		store.PrepareForCapture(directory.Root + Path.DirectorySeparatorChar);
		var segment = directory.Create(1, 64);
		store.AddFinalizedSegment(segment);
		store.PrepareForCapture(directory.Root);
		Assert.Equal(segment, Assert.Single(store.Snapshot()));
		Assert.True(File.Exists(segment.Path));
		Assert.Equal(64, store.RetainedBytes);
		Assert.Null(store.StorageFailure);
	}

	[Fact]
	public void Repeated_sessions_reclaim_owned_history_and_partials_without_touching_saved_clips()
	{
		using var directory = new ReplayDirectory();
		var clips = Path.Combine(directory.Root, "clips");
		Directory.CreateDirectory(clips);
		var clip = Path.Combine(clips, $"replay-{Guid.NewGuid():N}.mp4");
		var unrelated = Path.Combine(directory.Root, "replay-reviewed-clip.mp4");
		File.WriteAllBytes(clip, [1, 2, 3]);
		File.WriteAllBytes(unrelated, [4, 5, 6]);
		string? previous = null;

		for (var session = 0; session < 8; session++)
		{
			using var store = directory.Open();
			store.PrepareForCapture(directory.Root);
			Assert.Equal(0, store.RetainedBytes);
			if (previous is not null)
				Assert.False(File.Exists(previous));
			var segment = directory.Create(1, 64);
			store.AddFinalizedSegment(segment);
			previous = segment.Path;
			Assert.Equal(64, store.RetainedBytes);
			Assert.Equal(64, directory.OwnedBytes());
			File.WriteAllBytes(Path.Combine(directory.Root, $"replay-{Guid.NewGuid():D}.partial.mp4"), new byte[17]);
			File.WriteAllBytes(Path.Combine(directory.Root, $"replay-{Guid.NewGuid():N}.mp4.lock"), []);
			Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(clip));
			Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(unrelated));
		}

		using var recovered = directory.Open();
		recovered.PrepareForCapture(directory.Root);
		Assert.Equal(0, directory.OwnedBytes());
		Assert.Equal(0, recovered.RetainedBytes);
		Assert.Empty(recovered.Snapshot());
		Assert.True(File.Exists(clip));
		Assert.True(File.Exists(unrelated));
	}

	[Fact]
	public void Directory_ownership_survives_store_disposal_until_the_last_clip_pin_releases()
	{
		using var directory = new ReplayDirectory();
		using var first = directory.Open();
		first.PrepareForCapture(directory.Root);
		var segment = directory.Create(1, 64);
		first.AddFinalizedSegment(segment);
		using var pin = first.PinRange(new ReplayRange(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
		using var second = directory.Open();

		Assert.Throws<IOException>(() => second.PrepareForCapture(directory.Root));
		Assert.True(File.Exists(segment.Path));
		first.Dispose();
		Assert.Throws<IOException>(() => second.PrepareForCapture(directory.Root));
		Assert.Equal(64, new FileInfo(pin.Segments[0].Path).Length);
		pin.Dispose();

		second.PrepareForCapture(directory.Root);
		Assert.False(File.Exists(segment.Path));
		Assert.Equal(0, second.RetainedBytes);
		Assert.Null(second.StorageFailure);
	}

	[Fact]
	public void Failed_eviction_and_rejected_candidate_deletion_remain_charged_until_retry()
	{
		if (!OperatingSystem.IsWindows())
			return; // The Windows gate exercises actual delete-sharing denial.
		using var directory = new ReplayDirectory();
		using var store = directory.Open(maximumBytes: 100);
		store.PrepareForCapture(directory.Root);
		var first = directory.Create(1, 80);
		store.AddFinalizedSegment(first);
		var candidate = directory.Create(11, 80);
		using (var firstHandle = new FileStream(first.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
		using (var candidateHandle = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Assert.Throws<IOException>(() => store.AddFinalizedSegment(candidate));
			Assert.Equal(160, directory.OwnedBytes());
			Assert.Equal(directory.OwnedBytes(), store.RetainedBytes);
			Assert.Equal(0UL, store.EvictedSegments);
			Assert.Empty(store.Snapshot());
			Assert.NotNull(store.StorageFailure);
			Assert.Throws<IOException>(() => store.PrepareForCapture(directory.Root));
			Assert.Equal(160, store.RetainedBytes);
		}

		store.Trim();
		Assert.Equal(0, directory.OwnedBytes());
		Assert.Equal(0, store.RetainedBytes);
		Assert.Equal(1UL, store.EvictedSegments);
		Assert.Null(store.StorageFailure);
		var next = directory.Create(21, 80);
		store.AddFinalizedSegment(next);
		Assert.Equal(directory.OwnedBytes(), store.RetainedBytes);
		Assert.Single(store.Snapshot());
	}

	[Fact]
	public void Startup_cleanup_failure_is_visible_and_does_not_authorize_another_capture_owner()
	{
		if (!OperatingSystem.IsWindows())
			return; // Native Windows filesystem sharing semantics are required.
		using var directory = new ReplayDirectory();
		var stale = directory.Create(1, 64);
		using var store = directory.Open();
		using (var handle = new FileStream(stale.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Assert.Throws<IOException>(() => store.PrepareForCapture(directory.Root));
			Assert.Equal(64, store.RetainedBytes);
			Assert.Equal(directory.OwnedBytes(), store.RetainedBytes);
			Assert.Empty(store.Snapshot());
			Assert.NotNull(store.StorageFailure);
			using var competing = directory.Open();
			Assert.Throws<IOException>(() => competing.PrepareForCapture(directory.Root));
			Assert.True(File.Exists(stale.Path));
		}
		store.PrepareForCapture(directory.Root);
		Assert.Equal(0, store.RetainedBytes);
		Assert.False(File.Exists(stale.Path));
		Assert.Null(store.StorageFailure);
	}

	[Fact]
	public void Oversized_candidate_is_rejected_and_actual_file_sizes_are_required()
	{
		using var directory = new ReplayDirectory();
		using var store = directory.Open(maximumBytes: 100);
		store.PrepareForCapture(directory.Root);
		var oversized = directory.Create(1, 101);
		Assert.Throws<IOException>(() => store.AddFinalizedSegment(oversized));
		Assert.False(File.Exists(oversized.Path));
		Assert.Equal(0, store.RetainedBytes);
		Assert.Empty(store.Snapshot());

		var mismatched = directory.Create(1, 64);
		File.WriteAllBytes(mismatched.Path, new byte[80]);
		Assert.Throws<InvalidDataException>(() => store.AddFinalizedSegment(mismatched));
		store.PrepareForCapture(directory.Root);
		Assert.False(File.Exists(mismatched.Path));
		Assert.Equal(0, store.RetainedBytes);
	}

	private sealed class ReplayDirectory : IDisposable
	{
		public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtaime-replay-storage-" + Guid.NewGuid().ToString("N"));
		public ReplayDirectory() => Directory.CreateDirectory(Root);
		public RollingReplaySegmentStore Open(long maximumBytes = 128) =>
			new(new ReplayBufferPolicy(TimeSpan.FromMinutes(1), maximumBytes, TimeSpan.FromSeconds(2)));

		public ReplaySegmentDescriptor Create(ulong firstSequence, int bytes)
		{
			var id = ReplaySegmentId.New();
			var path = Path.Combine(Root, $"replay-{id}.mp4");
			File.WriteAllBytes(path, new byte[bytes]);
			var start = TimeSpan.FromSeconds((firstSequence - 1) / 5);
			return new ReplaySegmentDescriptor(id, path, firstSequence, firstSequence + 9,
				start, start + TimeSpan.FromSeconds(2), bytes, true, false);
		}

		public long OwnedBytes() => Directory.EnumerateFiles(Root, "replay-*")
			.Where(path => !Path.GetFileName(path).StartsWith("replay-reviewed", StringComparison.Ordinal))
			.Sum(path => new FileInfo(path).Length);

		public void Dispose() => Directory.Delete(Root, recursive: true);
	}
}
