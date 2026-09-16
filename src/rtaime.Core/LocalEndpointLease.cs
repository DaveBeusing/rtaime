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
		var normalized = NormalizeEndpoint(endpoint);
		var name = CreateLeaseName(normalized);
		var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
		if (!createdNew)
		{
			mutex.Dispose();
			throw new InvalidOperationException($"Local endpoint '{normalized}' is already leased by another process.");
		}

		return new LocalEndpointLease(normalized, mutex);
	}

	public static bool IsHeld(string endpoint)
	{
		var name = CreateLeaseName(NormalizeEndpoint(endpoint));
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

	public void Dispose()
	{
		Interlocked.Exchange(ref _mutex, null)?.Dispose();
	}

	private static string NormalizeEndpoint(string endpoint)
	{
		if (string.IsNullOrWhiteSpace(endpoint))
			throw new ArgumentException("Local endpoint lease requires a non-empty endpoint.", nameof(endpoint));
		return endpoint.Trim();
	}

	private static string CreateLeaseName(string endpoint)
	{
		var bytes = Encoding.UTF8.GetBytes(endpoint);
		var hash = SHA256.HashData(bytes);
		return NamePrefix + Convert.ToHexString(hash).ToLowerInvariant();
	}
}
