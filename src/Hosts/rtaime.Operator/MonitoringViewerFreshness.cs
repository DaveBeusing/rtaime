// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

// Only an observation for the selected source, route revision and transport
// connection may make an active viewer LIVE. Receipt time is captured before
// Dispatcher delivery so blocked UI work cannot rejuvenate an old image.
internal sealed class MonitoringViewerFreshness
{
	internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

	private string _sourceId = string.Empty;
	private long _routeVersion;
	private long _connectionEpoch;
	private DateTimeOffset _lastReceipt;
	private ulong _lastSequence;
	private ulong? _lastResourceGeneration;
	private bool _hasSequence;
	private string? _providerInstanceId;

	internal void SelectSource(string sourceId, long routeVersion, long connectionEpoch)
	{
		_sourceId = sourceId;
		_routeVersion = routeVersion;
		_connectionEpoch = connectionEpoch;
		_lastReceipt = default;
		_lastSequence = 0;
		_lastResourceGeneration = null;
		_hasSequence = false;
		_providerInstanceId = null;
	}

	internal bool Observe(string sourceId, long routeVersion, long connectionEpoch, DateTimeOffset receivedAt, ulong sequenceNumber,
		string? providerInstanceId = null, ulong? resourceGeneration = null)
	{
		if (!string.Equals(sourceId, _sourceId, StringComparison.Ordinal) ||
			routeVersion != _routeVersion ||
			connectionEpoch != _connectionEpoch ||
			receivedAt == default)
			return false;

		// A provider restart establishes a new sequence epoch even if the
		// monitoring pipe remains connected. Reordered receipts stay rejected.
		if (receivedAt < _lastReceipt)
			return false;
		var providerChanged = providerInstanceId is not null &&
			!string.Equals(providerInstanceId, _providerInstanceId, StringComparison.Ordinal);
		if (_hasSequence && !providerChanged)
		{
			// Resource replacement can restart frame sequencing, but an older
			// generation or reordered sample must never supersede current evidence.
			if (resourceGeneration is { } generation && _lastResourceGeneration is { } previousGeneration)
			{
				if (generation < previousGeneration ||
					(generation == previousGeneration && sequenceNumber <= _lastSequence))
					return false;
			}
			else if (sequenceNumber <= _lastSequence)
				return false;
		}

		_lastReceipt = receivedAt;
		if (providerInstanceId is not null)
			_providerInstanceId = providerInstanceId;
		_lastSequence = sequenceNumber;
		_lastResourceGeneration = resourceGeneration;
		_hasSequence = true;
		return true;
	}

	internal string Status(bool hasFrame, DateTimeOffset now)
	{
		if (_lastReceipt == default)
			return hasFrame ? "UNKNOWN" : "RECOVERING";
		if (now - _lastReceipt > Budget)
			return "STALE";
		return hasFrame ? "LIVE" : "UNKNOWN";
	}

	// Display-only elapsed receipt age. It is not a decoder, presentation, sink
	// or native Program frame latency measurement. The existing 500-ms monitoring
	// watchdog refreshes this label; no dedicated UI timer is introduced.
	internal string FrameAgeEvidence(bool hasFrame, DateTimeOffset now)
	{
		var state = Status(hasFrame, now);
		if (_lastReceipt == default || state is "UNKNOWN" or "RECOVERING")
			return $"FRAME AGE — · {state}";
		if (now < _lastReceipt)
			return "FRAME AGE — · CLOCK UNVERIFIED";
		var ageMilliseconds = Math.Max(0, (long)(now - _lastReceipt).TotalMilliseconds);
		return $"RECEIVED {ageMilliseconds} ms AGO · {state}";
	}

	internal DateTimeOffset LastReceipt => _lastReceipt;
}
