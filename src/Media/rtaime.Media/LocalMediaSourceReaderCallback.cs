// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace rtaime.Media;

// All ReadSample output pointers are null in asynchronous mode. Only this mailbox
// crosses the MF callback boundary; the owning decode worker serializes read/flush/seek.
[ComVisible(true)]
[Guid("DEEC8D99-FA1D-4D82-84C2-2C8969944867")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ILocalMediaSourceReaderCallback
{
    [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample);
    [PreserveSig] int OnFlush(uint stream);
    [PreserveSig] int OnEvent(uint stream, IntPtr mediaEvent);
}

[SupportedOSPlatform("windows")]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class LocalMediaSourceReaderCallback : ILocalMediaSourceReaderCallback, IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _readAvailable = new(false);
    private readonly AutoResetEvent _flushAvailable = new(false);
    private LocalMediaNativeRead? _result;
    private Exception? _readFailure;
    private Exception? _terminalFailure;
    private bool _readPending;
    private bool _flushPending;
    private bool _disposed;

    internal LocalMediaNativeRead Read(IMFSourceReader reader, uint stream, CancellationToken cancellationToken, TimeSpan remaining)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginRead();
        try
        {
            MediaFoundation.ThrowIfFailed(reader.ReadSample(stream, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            var result = WaitForRead(cancellationToken, remaining);
            if (result.Status < 0)
            {
                MediaFoundation.ReleaseComObject(result.Sample);
                var code = (unchecked((uint)result.Status) & 0xFFFF0000u) == 0x80070000u
                    ? "media.file.storage_failure" : "media.file.decoder_lost";
                throw new LocalMediaDecodeException(code,
                    $"Media Foundation read failed (HRESULT 0x{unchecked((uint)result.Status):X8}).");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            Flush(reader);
            throw;
        }
        catch (TimeoutException exception)
        {
            Flush(reader);
            throw new LocalMediaDecodeException("media.file.read_timeout", "Media Foundation read deadline expired.", exception);
        }
        finally { FinishRead(); }
    }

    // One owner and one callback mailbox. Reusing these two native events avoids
    // per-read blocking Task/monitor resources whose reclamation depends on GC.
    internal void BeginRead()
    {
        lock (_gate)
        {
            ThrowIfFaulted();
            if (_readPending) throw new InvalidOperationException("Only one MF read may be pending per session.");
            _readAvailable.Reset();
            _readFailure = null;
            _readPending = true;
        }
    }

    internal LocalMediaNativeRead WaitForRead(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var registration = cancellationToken.UnsafeRegister(static state =>
            ((LocalMediaSourceReaderCallback)state!).WakeRead(), this);
        var deadline = Environment.TickCount64 + Math.Max(1L, (long)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfFaulted();
                if (_readFailure is { } failure) throw failure;
                if (_result is { } result) { _result = null; return result; }
            }
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || !_readAvailable.WaitOne(TimeSpan.FromMilliseconds(remaining)))
                throw new TimeoutException("Media Foundation read deadline expired.");
        }
    }

    private void WakeRead()
    {
        lock (_gate) { if (!_disposed) _readAvailable.Set(); }
    }

    internal void FinishRead()
    {
        lock (_gate)
        {
            _readPending = false;
            if (_result is { } abandoned) MediaFoundation.ReleaseComObject(abandoned.Sample);
            _result = null;
        }
    }

    internal void Flush(IMFSourceReader reader)
    {
        lock (_gate)
        {
            ThrowIfFaulted();
            FinishRead();
            _flushAvailable.Reset();
            _flushPending = true;
        }
        try
        {
            MediaFoundation.ThrowIfFailed(reader.Flush(MediaFoundation.AllStreams));
            if (!_flushAvailable.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Media Foundation flush acknowledgement deadline expired.");
        }
        catch (Exception exception)
        {
            lock (_gate) _terminalFailure = exception;
            throw new LocalMediaReadCancellationException("Media Foundation did not acknowledge the cancelled read/flush.", exception);
        }
        finally { lock (_gate) _flushPending = false; }
    }

    private void ThrowIfFaulted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminalFailure is not null)
            throw new LocalMediaReadCancellationException("Media Foundation reader is fenced after an unacknowledged flush.", _terminalFailure);
    }

    public int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample)
    {
        IMFSample? managed = null;
        try
        {
            if (sample != IntPtr.Zero) managed = (IMFSample)Marshal.GetObjectForIUnknown(sample);
            lock (_gate)
            {
                if (!_disposed && _readPending && _result is null)
                {
                    _result = new(status, flags, timestamp, managed);
                    managed = null;
                    _readAvailable.Set();
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!_disposed && _readPending)
                {
                    _readFailure = new IOException("Media Foundation callback failed.", exception);
                    _readAvailable.Set();
                }
            }
            return Marshal.GetHRForException(exception);
        }
        finally { MediaFoundation.ReleaseComObject(managed); }
    }

    public int OnFlush(uint stream)
    {
        lock (_gate) { if (!_disposed && _flushPending) _flushAvailable.Set(); }
        return 0;
    }

    public int OnEvent(uint stream, IntPtr mediaEvent) => 0;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FinishRead();
            _disposed = true;
            _readAvailable.Dispose();
            _flushAvailable.Dispose();
        }
    }
}

internal readonly record struct LocalMediaNativeRead(int Status, uint Flags, long Timestamp, IMFSample? Sample);

public sealed class LocalMediaReadCancellationException : IOException
{
    public LocalMediaReadCancellationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
