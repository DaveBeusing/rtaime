// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace rtaime.Provider.Srt;

/// <summary>Retains only unpublished mux output while COM positions remain absolute.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ChunkedComStream : IStream, IDisposable
{
	internal const int MaximumBufferedBytes = 16 * 1024 * 1024;
	private const int RetainedCapacity = 64 * 1024;
	private readonly object _gate = new();
	private readonly MemoryStream _stream = new();
	private long _publishedLength;
	private long _position;
	private bool _disposed;

	public byte[] Drain()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_stream.Length == 0)
				return Array.Empty<byte>();
			var publishedLength = checked(_publishedLength + _stream.Length);
			var result = _stream.ToArray();
			_stream.SetLength(0);
			_stream.Position = 0;
			// Large mux bursts must not retain their peak storage for the connection lifetime.
			if (_stream.Capacity > RetainedCapacity)
				_stream.Capacity = RetainedCapacity;
			_publishedLength = publishedLength;
			return result;
		}
	}

	public void Read(byte[] pv, int cb, IntPtr pcbRead)
	{
		ValidateBuffer(pv, cb);
		lock (_gate)
		{
			var read = ReadUnsafe(pv, cb);
			if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
		}
	}

	private int ReadUnsafe(byte[] buffer, int count)
	{
		ThrowIfDisposed();
		_stream.Position = RelativePosition(_position);
		var read = _stream.Read(buffer, 0, count);
		_position += read;
		return read;
	}

	public void Write(byte[] pv, int cb, IntPtr pcbWritten)
	{
		ValidateBuffer(pv, cb);
		lock (_gate)
		{
			ThrowIfDisposed();
			var position = RelativePosition(_position);
			var end = checked(_position + cb);
			_ = RelativePosition(end);
			_stream.Position = position;
			_stream.Write(pv, 0, cb);
			_position = end;
			if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
		}
	}

	public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			var origin = dwOrigin switch
			{
				0 => 0,
				1 => _position,
				2 => checked(_publishedLength + _stream.Length),
				_ => throw new ArgumentOutOfRangeException(nameof(dwOrigin))
			};
			var position = checked(origin + dlibMove);
			if (dwOrigin != 1 || dlibMove != 0)
				_ = RelativePosition(position);
			// Validate before changing the cursor: a failed seek must not enable an overwrite.
			_position = position;
			if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, position);
		}
	}

	public void SetSize(long libNewSize)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			_stream.SetLength(RelativePosition(libNewSize));
			// IStream.SetSize does not move the absolute seek pointer.
		}
	}

	private long RelativePosition(long absolutePosition)
	{
		if (absolutePosition < _publishedLength)
			throw new IOException("Streaming container attempted to access already published bytes.");
		var relative = absolutePosition - _publishedLength;
		if (relative > MaximumBufferedBytes)
			throw new IOException($"Pending transport stream output exceeds {MaximumBufferedBytes} bytes.");
		return relative;
	}

	public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
	{
		ArgumentNullException.ThrowIfNull(pstm);
		ArgumentOutOfRangeException.ThrowIfNegative(cb);
		var buffer = new byte[checked((int)Math.Min(64 * 1024, cb))];
		long total = 0;
		while (total < cb)
		{
			var count = checked((int)Math.Min(buffer.Length, cb - total));
			int read;
			lock (_gate) read = ReadUnsafe(buffer, count);
			if (read == 0) break;
			pstm.Write(buffer, read, IntPtr.Zero);
			total += read;
		}
		if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, total);
		if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, total);
	}

	public void Commit(int grfCommitFlags) { }
	public void Revert() => throw new COMException("Revert is not supported.", unchecked((int)0x80004001));
	public void LockRegion(long libOffset, long cb, int dwLockType) { }
	public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
	public void Stat(out STATSTG pstatstg, int grfStatFlag)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			pstatstg = new STATSTG { cbSize = checked(_publishedLength + _stream.Length), type = 2 };
		}
	}
	public void Clone(out IStream ppstm) => throw new COMException("Clone is not supported.", unchecked((int)0x80004001));

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_stream.SetLength(0);
			_stream.Capacity = 0;
			_stream.Dispose();
		}
	}

	private static void ValidateBuffer(byte[] buffer, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
