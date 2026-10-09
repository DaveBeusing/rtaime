// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

internal sealed class RuntimeNetworkOutputBridge : IAsyncDisposable
{
	private readonly IReadOnlyDictionary<string, INetworkOutputSession> _sessions;
	private readonly IReadOnlyList<ProviderDescriptor> _providerDescriptors;

	public RuntimeNetworkOutputBridge(
		IReadOnlyList<RuntimeNetworkOutputTarget> targets,
		IRuntimeNetworkOutputProviderRegistry? providerRegistry = null)
	{
		ArgumentNullException.ThrowIfNull(targets);
		providerRegistry ??= new RuntimeNetworkOutputProviderRegistry();

		var sessions = new Dictionary<string, INetworkOutputSession>(StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (var target in targets)
			{
				if (!sessions.TryAdd(target.RoleId, providerRegistry.CreateSession(target)))
					throw new ArgumentException($"Network output role '{target.RoleId}' is configured more than once.", nameof(targets));
			}
		}
		catch
		{
			foreach (var session in sessions.Values)
				session.DisposeAsync().AsTask().GetAwaiter().GetResult();
			throw;
		}

		_sessions = sessions;
		_providerDescriptors = providerRegistry.ProviderDescriptorsFor(targets);
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => _providerDescriptors;
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
			var networkAudio = audioPayload;
			if (networkAudio.IsEmpty)
			{
				var silenceLength = checked((int)((long)audio.Timing.SampleCount * audio.Format.ChannelCount * sizeof(float)));
				networkAudio = new byte[silenceLength];
			}
			var sample = new NetworkOutputProgramSample(
				videoTiming.SequenceNumber,
				videoTiming,
				audio.Timing,
				payload,
				networkAudio);
			var result = session.TrySubmit(sample);
			payload = null!;
			return result;
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
