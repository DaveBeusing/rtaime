// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;

namespace rtaime.Core;

/// <summary>
/// Process-lifetime lease for a local IPC endpoint. The named kernel object remains present while
/// the owning host process keeps this lease alive, independent of short Named Pipe listener re-arm gaps.
/// </summary>
public sealed class LocalEndpointLease : IDisposable
{
	private const string NamePrefix = "rtaime.endpoint.";
	private Mutex? _mutex;

	private LocalEndpointLease(string endpoint, Mutex mutex)
	{
		Endpoint = endpoint;
		_mutex = mutex;
	}

	public string Endpoint { get; }

	public static LocalEndpointLease Acquire(string endpoint)
	{
		var normalized = LocalEndpointLeaseNames.Normalize(endpoint);
		var mutex = LocalEndpointLeaseNames.Acquire(NamePrefix, normalized, "Local endpoint");
		return new LocalEndpointLease(normalized, mutex);
	}

	public static bool IsHeld(string endpoint) =>
		LocalEndpointLeaseNames.IsHeld(NamePrefix, LocalEndpointLeaseNames.Normalize(endpoint));

	public void Dispose()
	{
		Interlocked.Exchange(ref _mutex, null)?.Dispose();
	}
}

/// <summary>
/// Explicit process-shared readiness lease for a local IPC endpoint. Hosts acquire this lease only
/// while their lifecycle is healthy and their IPC server is running. It is intentionally distinct
/// from <see cref="LocalEndpointLease"/>, which only proves that a host process owns the endpoint.
/// </summary>
public sealed class LocalEndpointReadinessLease : IDisposable
{
	private const string NamePrefix = "rtaime.endpoint.ready.";
	private Mutex? _mutex;

	private LocalEndpointReadinessLease(string endpoint, Mutex mutex)
	{
		Endpoint = endpoint;
		_mutex = mutex;
	}

	public string Endpoint { get; }

	public static LocalEndpointReadinessLease Acquire(string endpoint)
	{
		var normalized = LocalEndpointLeaseNames.Normalize(endpoint);
		var mutex = LocalEndpointLeaseNames.Acquire(NamePrefix, normalized, "Local endpoint readiness");
		return new LocalEndpointReadinessLease(normalized, mutex);
	}

	public static bool IsHeld(string endpoint) =>
		LocalEndpointLeaseNames.IsHeld(NamePrefix, LocalEndpointLeaseNames.Normalize(endpoint));

	public void Dispose()
	{
		Interlocked.Exchange(ref _mutex, null)?.Dispose();
	}
}

internal static class LocalEndpointLeaseNames
{
	public static string Normalize(string endpoint)
	{
		if (string.IsNullOrWhiteSpace(endpoint))
			throw new ArgumentException("Local endpoint lease requires a non-empty endpoint.", nameof(endpoint));
		return endpoint.Trim();
	}

	public static Mutex Acquire(string prefix, string endpoint, string description)
	{
		var name = Create(prefix, endpoint);
		var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
		if (!createdNew)
		{
			mutex.Dispose();
			throw new InvalidOperationException($"{description} '{endpoint}' is already leased by another process.");
		}
		return mutex;
	}

	public static bool IsHeld(string prefix, string endpoint)
	{
		var name = Create(prefix, endpoint);
		try
		{
			using var existing = Mutex.OpenExisting(name);
			return true;
		}
		catch (WaitHandleCannotBeOpenedException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}

	private static string Create(string prefix, string endpoint)
	{
		var bytes = Encoding.UTF8.GetBytes(endpoint);
		var hash = SHA256.HashData(bytes);
		return prefix + Convert.ToHexString(hash).ToLowerInvariant();
	}
}
