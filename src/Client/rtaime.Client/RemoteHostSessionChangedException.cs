// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public sealed class RemoteHostSessionChangedException : InvalidOperationException
{
	public RemoteHostSessionChangedException(string previousHostInstanceId, string currentHostInstanceId)
		: base($"Remote ControlHost changed from instance '{previousHostInstanceId}' to '{currentHostInstanceId}'. A full authoritative snapshot is required before another mutation.")
	{
		PreviousHostInstanceId = previousHostInstanceId;
		CurrentHostInstanceId = currentHostInstanceId;
	}

	public string PreviousHostInstanceId { get; }
	public string CurrentHostInstanceId { get; }
}
