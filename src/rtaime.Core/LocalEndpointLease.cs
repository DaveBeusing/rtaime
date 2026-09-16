// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
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
/// while their lifecycle is healthy and their IPC server is running. Readiness is represented by a
/// deterministic local marker containing both process identity and process start time, so a stale
/// marker left by an abrupt process exit cannot be mistaken for a live ready host.
/// </summary>
public sealed class LocalEndpointReadinessLease : IDisposable
{
	private readonly int _processId;
	private readonly long _processStartUtcTicks;
	private string? _markerPath;

	private LocalEndpointReadinessLease(string endpoint, string markerPath, int processId, long processStartUtcTicks)
	{
		Endpoint = endpoint;
		_markerPath = markerPath;
		_processId = processId;
		_processStartUtcTicks = processStartUtcTicks;
	}

	public string Endpoint { get; }

	public static LocalEndpointReadinessLease Acquire(string endpoint)
	{
		var normalized = LocalEndpointLeaseNames.Normalize(endpoint);
		var markerPath = LocalEndpointReadinessMarker.GetPath(normalized);
		if (IsHeld(normalized))
			throw new InvalidOperationException($"Local endpoint readiness '{normalized}' is already leased by another process.");

		using var process = Process.GetCurrentProcess();
		var processId = process.Id;
		var processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
		LocalEndpointReadinessMarker.Write(markerPath, normalized, processId, processStartUtcTicks);
		return new LocalEndpointReadinessLease(normalized, markerPath, processId, processStartUtcTicks);
	}

	public static bool IsHeld(string endpoint)
	{
		var normalized = LocalEndpointLeaseNames.Normalize(endpoint);
		var markerPath = LocalEndpointReadinessMarker.GetPath(normalized);
		if (!LocalEndpointReadinessMarker.TryRead(markerPath, normalized, out var processId, out var processStartUtcTicks))
			return false;

		try
		{
			using var process = Process.GetProcessById(processId);
			if (process.HasExited)
			{
				LocalEndpointReadinessMarker.DeleteIfMatches(markerPath, normalized, processId, processStartUtcTicks);
				return false;
			}

			var observedStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
			if (observedStartUtcTicks == processStartUtcTicks)
				return true;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			// A marker is readiness evidence only while its exact process identity can still be validated.
		}

		LocalEndpointReadinessMarker.DeleteIfMatches(markerPath, normalized, processId, processStartUtcTicks);
		return false;
	}

	public void Dispose()
	{
		var markerPath = Interlocked.Exchange(ref _markerPath, null);
		if (markerPath is null) return;
		LocalEndpointReadinessMarker.DeleteIfMatches(markerPath, Endpoint, _processId, _processStartUtcTicks);
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

internal static class LocalEndpointReadinessMarker
{
	private const string MarkerDirectoryName = "endpoint-readiness";

	public static string GetPath(string endpoint)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));
		var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".ready";
		return Path.Combine(Path.GetTempPath(), "rtaime", MarkerDirectoryName, fileName);
	}

	public static void Write(string markerPath, string endpoint, int processId, long processStartUtcTicks)
	{
		var directory = Path.GetDirectoryName(markerPath)
			?? throw new InvalidOperationException("Endpoint readiness marker directory could not be resolved.");
		Directory.CreateDirectory(directory);
		var temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		var content = string.Join(
			Environment.NewLine,
			$"endpoint={endpoint}",
			$"processId={processId}",
			$"processStartUtcTicks={processStartUtcTicks}") + Environment.NewLine;
		try
		{
			File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
			File.Move(temporaryPath, markerPath, overwrite: true);
		}
		finally
		{
			try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
			catch (IOException) { }
		}
	}

	public static bool TryRead(string markerPath, string expectedEndpoint, out int processId, out long processStartUtcTicks)
	{
		processId = 0;
		processStartUtcTicks = 0;
		if (!File.Exists(markerPath)) return false;

		try
		{
			var values = File.ReadAllLines(markerPath)
				.Select(line => line.Split('=', 2))
				.Where(parts => parts.Length == 2)
				.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
			return values.TryGetValue("endpoint", out var endpoint) &&
				string.Equals(endpoint, expectedEndpoint, StringComparison.Ordinal) &&
				values.TryGetValue("processId", out var processIdText) &&
				int.TryParse(processIdText, out processId) &&
				processId > 0 &&
				values.TryGetValue("processStartUtcTicks", out var processStartText) &&
				long.TryParse(processStartText, out processStartUtcTicks) &&
				processStartUtcTicks > 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			return false;
		}
	}

	public static void DeleteIfMatches(string markerPath, string endpoint, int processId, long processStartUtcTicks)
	{
		if (!TryRead(markerPath, endpoint, out var observedProcessId, out var observedStartUtcTicks)) return;
		if (observedProcessId != processId || observedStartUtcTicks != processStartUtcTicks) return;
		try { File.Delete(markerPath); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}
}
