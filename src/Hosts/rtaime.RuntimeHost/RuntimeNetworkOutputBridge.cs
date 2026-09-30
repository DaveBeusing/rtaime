// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.Srt;

namespace rtaime.RuntimeHost;

internal sealed class RuntimeNetworkOutputBridge : IAsyncDisposable
{
	private readonly IReadOnlyDictionary<string, SrtNetworkOutputSession> _sessions;
	private readonly SrtNetworkOutputProvider _provider;

	public RuntimeNetworkOutputBridge(
		IReadOnlyList<RuntimeNetworkOutputTarget> targets,
		Func<RuntimeNetworkOutputTarget, SrtNetworkOutputSession>? sessionFactory = null)
	{
		ArgumentNullException.ThrowIfNull(targets);
		_provider = new SrtNetworkOutputProvider();
		sessionFactory ??= target => _provider.CreateSession(
			target.Configuration,
			Environment.GetEnvironmentVariable("RTAIME_SRT_LIBRARY_PATH"));

		var sessions = new Dictionary<string, SrtNetworkOutputSession>(StringComparer.OrdinalIgnoreCase);
		foreach (var target in targets)
		{
			if (!sessions.TryAdd(target.RoleId, sessionFactory(target)))
				throw new ArgumentException($"Network output role '{target.RoleId}' is configured more than once.", nameof(targets));
		}
		_sessions = sessions;
	}

	public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;
	public bool Enabled => _sessions.Count > 0;
	public bool HasRole(string roleId) => _sessions.ContainsKey(roleId);

	public NetworkOutputEnqueueResult? TrySubmit(
		string roleId,
		GpuReadbackLease videoLease,
		FrameTiming videoTiming,
		AudioBufferDescriptor audio,
		ReadOnlyMemory<byte> audioPayload)
	{
		if (string.IsNullOrWhiteSpace(roleId))
			throw new ArgumentException("Output role identity is required.", nameof(roleId));
		ArgumentNullException.ThrowIfNull(videoLease);
		ArgumentNullException.ThrowIfNull(audio);

		if (!_sessions.TryGetValue(roleId, out var session))
		{
			videoLease.Dispose();
			return null;
		}

		var payload = new GpuNetworkOutputPayloadLease(videoLease);
		try
		{
			var sample = new NetworkOutputProgramSample(
				videoTiming.SequenceNumber,
				videoTiming,
				audio.Timing,
				payload,
				audioPayload);
			payload = null!;
			return session.TrySubmit(sample);
		}
		finally
		{
			payload?.Dispose();
		}
	}

	public NetworkOutputHealthSnapshot? SnapshotForRole(string roleId) =>
		_sessions.TryGetValue(roleId, out var session) ? session.Snapshot : null;

	public IReadOnlyList<NetworkOutputHealthSnapshot> Snapshots =>
		Array.AsReadOnly(_sessions.Values.Select(session => session.Snapshot).ToArray());

	public async ValueTask DisposeAsync()
	{
		foreach (var session in _sessions.Values)
			await session.DisposeAsync().ConfigureAwait(false);
	}

	private sealed class GpuNetworkOutputPayloadLease : INetworkOutputPayloadLease
	{
		private GpuReadbackLease? _lease;

		public GpuNetworkOutputPayloadLease(GpuReadbackLease lease) =>
			_lease = lease ?? throw new ArgumentNullException(nameof(lease));

		public ReadOnlyMemory<byte> Memory =>
			Volatile.Read(ref _lease)?.Memory ?? throw new ObjectDisposedException(nameof(GpuNetworkOutputPayloadLease));

		public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
	}
}
