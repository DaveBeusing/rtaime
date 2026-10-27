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
	private bool _hasSequence;

	internal void SelectSource(string sourceId, long routeVersion, long connectionEpoch)
	{
		_sourceId = sourceId;
		_routeVersion = routeVersion;
		_connectionEpoch = connectionEpoch;
		_lastReceipt = default;
		_lastSequence = 0;
		_hasSequence = false;
	}

	internal bool Observe(string sourceId, long routeVersion, long connectionEpoch, DateTimeOffset receivedAt, ulong sequenceNumber)
	{
		if (!string.Equals(sourceId, _sourceId, StringComparison.Ordinal) ||
			routeVersion != _routeVersion ||
			connectionEpoch != _connectionEpoch ||
			receivedAt == default)
			return false;

		// Delayed/reordered observations must not revive or age a newer viewer.
		if (receivedAt < _lastReceipt || (_hasSequence && sequenceNumber <= _lastSequence))
			return false;

		_lastReceipt = receivedAt;
		_lastSequence = sequenceNumber;
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

	internal DateTimeOffset LastReceipt => _lastReceipt;
}
