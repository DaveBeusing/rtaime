// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics.Tracing;

namespace rtaime.Tests.Integration;

// Optional diagnostic observer, never enabled for release/normal playback acceptance.
// Only sampled LOH type/size aggregates are retained; no objects, payloads or stacks.
internal sealed class LocalPlaybackAllocationListener : EventListener
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ulong Samples, ulong Bytes)> _types = new();
    private bool _armed;

    internal LocalPlaybackAllocationListener()
    {
        _armed = true;
        foreach (var source in EventSource.GetSources()) EnableRuntime(source);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (_armed) EnableRuntime(eventSource);
    }

    private void EnableRuntime(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventId != 10 || data.PayloadNames is null || data.Payload is null) return;
        object? Field(string name)
        {
            for (var index = 0; index < data.PayloadNames.Count; index++)
                if (data.PayloadNames[index] == name) return data.Payload[index];
            return null;
        }
        if (Convert.ToInt32(Field("AllocationKind") ?? -1) != 1) return;
        var type = Field("TypeName")?.ToString() ?? "UNAVAILABLE";
        var bytes = Convert.ToUInt64(Field("ObjectSize") ?? Field("AllocationAmount64") ?? 0UL);
        var key = type + ":" + bytes;
        lock (_gate)
        {
            if (!_types.ContainsKey(key) && _types.Count >= 32) key = "OTHER";
            _types.TryGetValue(key, out var previous);
            _types[key] = (previous.Samples + 1, previous.Bytes + bytes);
        }
    }

    internal object Snapshot()
    {
        lock (_gate)
            return _types.Select(pair => new { typeAndSize = pair.Key, samples = pair.Value.Samples, bytes = pair.Value.Bytes }).ToArray();
    }
}
