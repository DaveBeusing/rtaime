// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// Stabilizes the RuntimeHost identity observed by ControlHost. An established logical binding never silently
/// switches to a different RuntimeHost process instance: the first observation of a replacement fails closed,
/// forcing ControlHost through Degraded before a subsequent explicit bind accepts and resynchronizes the new host.
/// </summary>
public sealed class RuntimeHostBindingTransport : IControlRuntimeTransportSeam
{
	private readonly object _gate = new();
	private readonly IControlRuntimeTransportSeam _inner;
	private bool _connected;
	private string? _acceptedHostInstanceId;

	public RuntimeHostBindingTransport(IControlRuntimeTransportSeam inner) =>
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));

	public RuntimeHostBindingTransport(string endpoint, TimeSpan connectTimeout, TimeSpan requestTimeout)
		: this(new NamedPipeRuntimeHostTransport(endpoint, connectTimeout, requestTimeout))
	{
	}

	public bool IsConnected
	{
		get
		{
			lock (_gate)
				return _connected && _inner.IsConnected;
		}
	}

	public string? HostInstanceId
	{
		get
		{
			lock (_gate)
				return _acceptedHostInstanceId;
		}
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => _inner.ProviderDescriptors;

	public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		string? expected;
		lock (_gate)
			expected = _acceptedHostInstanceId;

		await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
		var observed = RequireObservedIdentity();
		if (expected is not null && !string.Equals(expected, observed, StringComparison.Ordinal))
		{
			MarkDisconnectedPreservingIdentity();
			await _inner.DisconnectAsync().ConfigureAwait(false);
			throw new InvalidDataException($"RuntimeHost process identity changed from '{expected}' to '{observed}' during an established binding.");
		}

		lock (_gate)
		{
			_acceptedHostInstanceId = observed;
			_connected = true;
		}
	}

	public async ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default)
	{
		var result = await _inner.GetProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
		EnsureObservedIdentityMatchesBinding();
		return result;
	}

	public async ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var result = await _inner.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		EnsureObservedIdentityMatchesBinding();
		return result;
	}

	public async ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default)
	{
		var result = await _inner.ApplyExecutionAsync(preparedExecution, programSinkId, transition, cancellationToken).ConfigureAwait(false);
		EnsureObservedIdentityMatchesBinding();
		return result;
	}

	public async ValueTask DisconnectAsync()
	{
		await _inner.DisconnectAsync().ConfigureAwait(false);
		lock (_gate)
		{
			_connected = false;
			_acceptedHostInstanceId = null;
		}
	}

	private string RequireObservedIdentity()
	{
		var observed = _inner.HostInstanceId;
		if (string.IsNullOrWhiteSpace(observed))
			throw new InvalidDataException("Connected RuntimeHost did not expose a host instance identity.");
		return observed;
	}

	private void EnsureObservedIdentityMatchesBinding()
	{
		var observed = RequireObservedIdentity();
		lock (_gate)
		{
			if (!_connected || _acceptedHostInstanceId is null)
				throw new InvalidOperationException("RuntimeHost transport operation completed without an accepted binding.");
			if (string.Equals(_acceptedHostInstanceId, observed, StringComparison.Ordinal))
				return;
			_connected = false;
			throw new InvalidDataException($"RuntimeHost process identity changed from '{_acceptedHostInstanceId}' to '{observed}' during an established binding.");
		}
	}

	private void MarkDisconnectedPreservingIdentity()
	{
		lock (_gate)
			_connected = false;
	}
}
