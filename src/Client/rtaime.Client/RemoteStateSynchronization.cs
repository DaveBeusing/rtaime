// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public sealed class RemoteStateSynchronizer
{
	private readonly object _gate = new();
	private string? _hostInstanceId;
	private ulong _stateVersion;

	public string? HostInstanceId
	{
		get { lock (_gate) return _hostInstanceId; }
	}

	public ulong StateVersion
	{
		get { lock (_gate) return _stateVersion; }
	}

	public void AcceptFullSnapshot(string hostInstanceId, ulong stateVersion)
	{
		if (string.IsNullOrWhiteSpace(hostInstanceId)) throw new ArgumentException("Remote host instance identity is required.", nameof(hostInstanceId));
		if (stateVersion == 0) throw new ArgumentOutOfRangeException(nameof(stateVersion), "Remote StateVersion must be greater than zero.");
		lock (_gate)
		{
			_hostInstanceId = hostInstanceId.Trim();
			_stateVersion = stateVersion;
		}
	}

	public bool TryApplyDelta(string hostInstanceId, ulong basedOnStateVersion, ulong stateVersion)
	{
		if (string.IsNullOrWhiteSpace(hostInstanceId)) return false;
		if (stateVersion == 0 || stateVersion <= basedOnStateVersion) return false;
		lock (_gate)
		{
			if (!string.Equals(_hostInstanceId, hostInstanceId, StringComparison.Ordinal)) return false;
			if (_stateVersion != basedOnStateVersion) return false;
			_stateVersion = stateVersion;
			return true;
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			_hostInstanceId = null;
			_stateVersion = 0;
		}
	}
}
