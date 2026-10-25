// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using rtaime.Provider.Srt;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SrtMuxStreamTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	public void Stat_advertises_the_read_write_access_required_by_native_media_sinks(int statFlags)
	{
		using var mux = new Mux();
		mux.Stream.Stat(out var emptyStatus, statFlags);
		Assert.Equal(2, emptyStatus.type); // STGTY_STREAM
		Assert.Equal(2, emptyStatus.grfMode & 3); // STGM_READWRITE, not the zero/default STGM_READ
		Assert.Equal(0, emptyStatus.cbSize);

		mux.Stream.Write([1, 2, 3], 3, IntPtr.Zero);
		mux.Stream.Seek(0, 0, IntPtr.Zero);
		var read = new byte[3];
		mux.Stream.Read(read, read.Length, IntPtr.Zero);
		Assert.Equal(new byte[] { 1, 2, 3 }, read);
		Assert.Equal(read, mux.Drain());
		mux.Stream.Write([4], 1, IntPtr.Zero);
		mux.Stream.Seek(3, 0, IntPtr.Zero);
		mux.Stream.Read(read, 1, IntPtr.Zero);
		Assert.Equal(4, read[0]);
		mux.Stream.Stat(out var currentStatus, statFlags);
		Assert.Equal(2, currentStatus.grfMode & 3);
		Assert.Equal(4, currentStatus.cbSize);
		Assert.Equal(4, mux.Position);
	}

	[Fact]
	public void Sustained_write_drain_releases_history_and_preserves_absolute_offsets()
	{
		using var mux = new Mux();
		var payload = new byte[256 * 1024];
		long total = 0;
		for (var iteration = 0; iteration < 1024; iteration++)
		{
			Array.Fill(payload, (byte)iteration);
			BitConverter.TryWriteBytes(payload, iteration);
			mux.Stream.Write(payload, payload.Length, IntPtr.Zero);
			Assert.True(payload.AsSpan().SequenceEqual(mux.Drain()));
			total += payload.Length;
			mux.Stream.Stat(out var status, 0);
			Assert.Equal(total, status.cbSize);
			Assert.Equal(total, mux.Position);
			Assert.Equal(0, mux.Backing.Length);
			Assert.InRange(mux.Backing.Capacity, 0, 64 * 1024);
		}
		Assert.Equal(256L * 1024 * 1024, total);
		Assert.Empty(mux.Drain());
	}

	[Fact]
	public void Seek_and_resize_keep_absolute_positions_and_only_edit_unpublished_bytes()
	{
		using var mux = new Mux();
		mux.Stream.Write([1, 2, 3, 4, 5, 6, 7, 8], 8, IntPtr.Zero);
		Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, mux.Drain());
		mux.Stream.Seek(0, 2, IntPtr.Zero);
		mux.Stream.Write([9, 10, 11, 12], 4, IntPtr.Zero);
		mux.Stream.Seek(-2, 1, IntPtr.Zero);
		mux.Stream.Write([99], 1, IntPtr.Zero);
		mux.Stream.Seek(11, 0, IntPtr.Zero);
		mux.Stream.Write([100], 1, IntPtr.Zero);
		mux.Stream.SetSize(14);
		Assert.Equal(12, mux.Position);
		mux.Stream.Seek(-4, 2, IntPtr.Zero);
		var read = new byte[4];
		mux.Stream.Read(read, read.Length, IntPtr.Zero);
		Assert.Equal(new byte[] { 99, 100, 0, 0 }, read);
		Assert.Equal(new byte[] { 9, 10, 99, 100, 0, 0 }, mux.Drain());
		Assert.Equal(14, mux.Position);
		mux.Stream.Seek(16, 0, IntPtr.Zero);
		mux.Stream.SetSize(15);
		Assert.Equal(16, mux.Position);
		mux.Stream.Write([33], 1, IntPtr.Zero);
		Assert.Equal(new byte[] { 0, 0, 33 }, mux.Drain());
	}

	[Fact]
	public void Published_access_and_buffer_overflow_fail_without_moving_or_allocating()
	{
		using var mux = new Mux();
		mux.Stream.Write([1, 2], 2, IntPtr.Zero);
		_ = mux.Drain();
		var capacity = mux.Backing.Capacity;
		Assert.Throws<IOException>(() => mux.Stream.Seek(0, 0, IntPtr.Zero));
		Assert.Throws<IOException>(() => mux.Stream.SetSize(1));
		Assert.Throws<OverflowException>(() => mux.Stream.Seek(long.MaxValue, 2, IntPtr.Zero));
		Assert.Throws<IOException>(() => mux.Stream.Seek(16 * 1024 * 1024 + 1, 2, IntPtr.Zero));
		Assert.Throws<IOException>(() => mux.Stream.SetSize(16 * 1024 * 1024 + 3));
		Assert.Equal(2, mux.Position);
		Assert.Equal(capacity, mux.Backing.Capacity);
		mux.Stream.Stat(out var status, 0);
		Assert.Equal(2, status.cbSize);
		mux.Stream.Write([3], 1, IntPtr.Zero);
		Assert.Equal(new byte[] { 3 }, mux.Drain());
	}

	[Fact]
	public void Drain_preserves_a_seek_cursor_and_rejects_its_later_write_until_seek_to_end()
	{
		using var mux = new Mux();
		mux.Stream.Write([1, 2, 3, 4], 4, IntPtr.Zero);
		mux.Stream.Seek(2, 0, IntPtr.Zero);
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, mux.Drain());
		Assert.Equal(2, mux.Position);
		Assert.Throws<IOException>(() => mux.Stream.Write([99], 1, IntPtr.Zero));
		Assert.Throws<IOException>(() => mux.Stream.Read(new byte[1], 1, IntPtr.Zero));
		mux.Stream.Seek(0, 2, IntPtr.Zero);
		mux.Stream.Write([5], 1, IntPtr.Zero);
		Assert.Equal(new byte[] { 5 }, mux.Drain());
	}

	[Fact]
	public void Pending_capacity_limit_applies_to_sparse_writes_and_resize()
	{
		using var mux = new Mux();
		mux.Stream.SetSize(16 * 1024 * 1024);
		mux.Stream.Seek(0, 2, IntPtr.Zero);
		Assert.Throws<IOException>(() => mux.Stream.Write([1], 1, IntPtr.Zero));
		Assert.Equal(16 * 1024 * 1024, mux.Position);
		Assert.Equal(16 * 1024 * 1024, mux.Backing.Length);
		Assert.InRange(mux.Backing.Capacity, 0, 16 * 1024 * 1024);
	}

	[Fact]
	public void Copy_preserves_pending_bytes_and_dispose_releases_backing_stream()
	{
		using var source = new Mux();
		using var target = new Mux();
		source.Stream.Write([1, 2], 2, IntPtr.Zero);
		_ = source.Drain();
		source.Stream.Write([3, 4, 5], 3, IntPtr.Zero);
		source.Stream.Seek(2, 0, IntPtr.Zero);
		source.Stream.CopyTo(target.Stream, 10, IntPtr.Zero, IntPtr.Zero);
		Assert.Equal(new byte[] { 3, 4, 5 }, target.Drain());
		Assert.Equal(5, source.Position);
		var backing = source.Backing;
		source.Dispose();
		Assert.False(backing.CanWrite);
		Assert.Throws<ObjectDisposedException>(() => source.Stream.Write([6], 1, IntPtr.Zero));
		Assert.Throws<ObjectDisposedException>(() => source.Drain());
	}

	private sealed class Mux : IDisposable
	{
		private static readonly Type StreamType = typeof(WindowsMediaFoundationSrtEncoder).Assembly
			.GetType("rtaime.Provider.Srt.ChunkedComStream", throwOnError: true)!;
		public IStream Stream { get; } = (IStream)Activator.CreateInstance(StreamType, nonPublic: true)!;
		public MemoryStream Backing => (MemoryStream)StreamType.GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Stream)!;
		public long Position
		{
			get
			{
				var pointer = Marshal.AllocHGlobal(sizeof(long));
				try { Stream.Seek(0, 1, pointer); return Marshal.ReadInt64(pointer); }
				finally { Marshal.FreeHGlobal(pointer); }
			}
		}
		public byte[] Drain()
		{
			try { return (byte[])StreamType.GetMethod("Drain")!.Invoke(Stream, null)!; }
			catch (TargetInvocationException exception) when (exception.InnerException is not null)
			{
				ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
				throw;
			}
		}
		public void Dispose() => ((IDisposable)Stream).Dispose();
	}
}
