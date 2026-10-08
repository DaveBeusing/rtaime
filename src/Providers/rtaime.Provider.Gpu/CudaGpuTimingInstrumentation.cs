// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.Gpu;

public enum CudaGpuTimingOperation
{
    UploadHostToDevice = 1,
    KernelLaunch = 2,
    ContextSynchronize = 3,
    KernelGpuElapsed = 4,
    ReadbackDeviceToHost = 5,
    MonitoringExport = 6
}

public readonly record struct CudaGpuTimingSample(
    CudaGpuTimingOperation Operation,
    ulong ByteLength,
    double Milliseconds);

/// <summary>
/// Optional bounded CUDA timing evidence sink. Production execution does not create a collector by default.
/// Qualification can opt in without changing scheduling, ownership, or completion semantics.
/// </summary>
public sealed class CudaGpuTimingCollector
{
    public const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly CudaGpuTimingSample[] _samples;
    private int _sampleCount;
    private int _nextSampleIndex;
    private ulong _overwrittenSampleCount;

    public CudaGpuTimingCollector(int capacity = DefaultCapacity)
    {
        if (capacity < 64 || capacity > 65_536)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _samples = new CudaGpuTimingSample[capacity];
    }

    public int Capacity => _samples.Length;

    public ulong OverwrittenSampleCount
    {
        get
        {
            lock (_gate)
                return _overwrittenSampleCount;
        }
    }

    internal void Record(CudaGpuTimingOperation operation, nuint byteLength, TimeSpan duration)
    {
        if (!Enum.IsDefined(typeof(CudaGpuTimingOperation), operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        lock (_gate)
        {
            if (_sampleCount == _samples.Length && _overwrittenSampleCount < ulong.MaxValue)
                _overwrittenSampleCount++;

            _samples[_nextSampleIndex] = new CudaGpuTimingSample(
                operation,
                checked((ulong)byteLength),
                duration.TotalMilliseconds);
            _nextSampleIndex = (_nextSampleIndex + 1) % _samples.Length;
            _sampleCount = Math.Min(_sampleCount + 1, _samples.Length);
        }
    }

    internal void RecordGpuMilliseconds(CudaGpuTimingOperation operation, nuint byteLength, float milliseconds)
    {
        if (float.IsNaN(milliseconds) || float.IsInfinity(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));

        Record(operation, byteLength, TimeSpan.FromMilliseconds(milliseconds));
    }

    public IReadOnlyList<CudaGpuTimingSample> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new CudaGpuTimingSample[_sampleCount];
            var start = _sampleCount == _samples.Length ? _nextSampleIndex : 0;
            for (var index = 0; index < _sampleCount; index++)
                snapshot[index] = _samples[(start + index) % _samples.Length];
            return snapshot;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_samples);
            _sampleCount = 0;
            _nextSampleIndex = 0;
            _overwrittenSampleCount = 0;
        }
    }
}
