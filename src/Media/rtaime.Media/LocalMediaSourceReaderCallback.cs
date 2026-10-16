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
public sealed class LocalMediaSourceReaderCallback : ILocalMediaSourceReaderCallback
{
    private readonly object _gate = new();
    private TaskCompletionSource<LocalMediaNativeRead>? _read;
    private TaskCompletionSource? _flush;

    internal LocalMediaNativeRead Read(IMFSourceReader reader, uint stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<LocalMediaNativeRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_read is not null) throw new InvalidOperationException("Only one MF read may be pending per session.");
            _read = completion;
        }
        try
        {
            MediaFoundation.ThrowIfFailed(reader.ReadSample(stream, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            var result = completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).GetAwaiter().GetResult();
            if (result.Status < 0)
            {
                MediaFoundation.ReleaseComObject(result.Sample);
                Marshal.ThrowExceptionForHR(result.Status);
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
            throw new IOException("Media Foundation read deadline expired.", exception);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_read, completion)) _read = null; }
        }
    }

    internal void Flush(IMFSourceReader reader)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            // A cancelled wait may race with delivery. Release either the delivered
            // sample here or any later delivery in OnReadSample, exactly once.
            if (_read?.Task is { IsCompletedSuccessfully: true } delivered)
                MediaFoundation.ReleaseComObject(delivered.Result.Sample);
            _read = null;
            _flush = completion;
        }
        try
        {
            MediaFoundation.ThrowIfFailed(reader.Flush(MediaFoundation.AllStreams));
            completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (TimeoutException exception)
        {
            throw new IOException("Media Foundation did not acknowledge the cancelled read/flush.", exception);
        }
        finally { lock (_gate) { if (ReferenceEquals(_flush, completion)) _flush = null; } }
    }

    public int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample)
    {
        IMFSample? managed = null;
        try
        {
            if (sample != IntPtr.Zero) managed = (IMFSample)Marshal.GetObjectForIUnknown(sample);
            lock (_gate)
            {
                if (_read is not null && _read.TrySetResult(new(status, flags, timestamp, managed))) managed = null;
            }
            return 0;
        }
        catch (Exception exception)
        {
            lock (_gate) _read?.TrySetException(new IOException("Media Foundation callback failed.", exception));
            return Marshal.GetHRForException(exception);
        }
        finally { MediaFoundation.ReleaseComObject(managed); }
    }

    public int OnFlush(uint stream)
    {
        lock (_gate) _flush?.TrySetResult();
        return 0;
    }

    public int OnEvent(uint stream, IntPtr mediaEvent) => 0;
}

internal readonly record struct LocalMediaNativeRead(int Status, uint Flags, long Timestamp, IMFSample? Sample);
