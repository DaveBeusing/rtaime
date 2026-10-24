// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public sealed class RemoteHostSessionChangedException : InvalidOperationException
{
	public RemoteHostSessionChangedException(string previousHostInstanceId, string currentHostInstanceId)
		: this(previousHostInstanceId, currentHostInstanceId, requestWasDispatched: true) { }

	public RemoteHostSessionChangedException(string previousHostInstanceId, string currentHostInstanceId, bool requestWasDispatched)
		: base($"Remote ControlHost changed from instance '{previousHostInstanceId}' to '{currentHostInstanceId}'. A full authoritative snapshot is required before another mutation.")
	{
		PreviousHostInstanceId = previousHostInstanceId;
		CurrentHostInstanceId = currentHostInstanceId;
		RequestWasDispatched = requestWasDispatched;
	}

	public string PreviousHostInstanceId { get; }
	public string CurrentHostInstanceId { get; }
	public bool RequestWasDispatched { get; }
}

public sealed class ControlSnapshotRequiredException(string message) : InvalidOperationException(message);

public sealed class ControlActionObservationException(Exception innerException)
	: IOException("The control action completed, but its authoritative snapshot refresh failed. Do not repeat the action to refresh state.", innerException);
