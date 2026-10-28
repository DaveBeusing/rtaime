// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.St2110;

/// <summary>
/// Observes one-millisecond software sender burst windows. This is not
/// ST 2110-21 compliance or physical wire-rate evidence: neither NIC hardware
/// timestamps nor switch port traffic have been measured.
/// </summary>
public sealed record St2110SoftwareTrafficSnapshot(
    ulong VideoDatagrams, ulong AudioDatagrams,
    ulong VideoBytes, ulong AudioBytes,
    long PeakVideoBytesInOneMillisecond, long PeakAudioBytesInOneMillisecond,
    long PeakVideoPacketsInOneMillisecond, long PeakAudioPacketsInOneMillisecond,
    long ObservationWindowCount);

public sealed class St2110SoftwareTrafficMonitor
{
    private readonly object _gate = new();
    private readonly Window _video = new();
    private readonly Window _audio = new();

    public void Observe(bool video, int datagramBytes, TimeSpan elapsed)
    {
        if (datagramBytes is < 12 or > 1200 || elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(datagramBytes));
        lock (_gate)
            (video ? _video : _audio).Observe(datagramBytes, elapsed.Ticks / TimeSpan.TicksPerMillisecond);
    }

    public St2110SoftwareTrafficSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new St2110SoftwareTrafficSnapshot(
                    _video.Packets, _audio.Packets,
                    _video.Bytes, _audio.Bytes,
                    _video.MaxBytes, _audio.MaxBytes,
                    _video.MaxPackets, _audio.MaxPackets,
                    _video.DistinctWindows + _audio.DistinctWindows);
            }
        }
    }

    private sealed class Window
    {
        private long _index = -1;
        private long _bytes;
        private long _packets;
        public ulong Packets { get; private set; }
        public ulong Bytes { get; private set; }
        public long MaxBytes { get; private set; }
        public long MaxPackets { get; private set; }
        public long DistinctWindows { get; private set; }

        public void Observe(int bytes, long bucket)
        {
            if (_index >= 0 && bucket < _index)
                throw new InvalidOperationException("Sender monotonic timeline moved backwards.");
            if (_index != bucket)
            {
                _index = bucket;
                _bytes = 0;
                _packets = 0;
                DistinctWindows++;
            }
            _bytes += bytes;
            _packets++;
            Packets++;
            Bytes += (ulong)bytes;
            MaxBytes = Math.Max(MaxBytes, _bytes);
            MaxPackets = Math.Max(MaxPackets, _packets);
        }
    }
}
