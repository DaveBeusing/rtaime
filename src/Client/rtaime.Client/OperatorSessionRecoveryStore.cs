// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.Client;

public sealed record OperatorSafeSessionState(string SelectedWorkspace)
{
	private static readonly HashSet<string> KnownWorkspaces = new(StringComparer.Ordinal)
	{
		"MEDIA",
		"EDIT",
		"LIVE",
		"SCENES",
		"COMPOSITING",
		"OUTPUTS",
		"HEALTH",
		"SETTINGS"
	};

	public static OperatorSafeSessionState Default { get; } = new("LIVE");

	public OperatorSafeSessionState Normalize()
	{
		var workspace = SelectedWorkspace?.Trim().ToUpperInvariant();
		return KnownWorkspaces.Contains(workspace ?? string.Empty)
			? new OperatorSafeSessionState(workspace!)
			: Default;
	}
}

public sealed record OperatorSessionRecoveryResult(
	bool PreviousSessionEndedUnexpectedly,
	bool StateWasCorrupted,
	bool PersistenceAvailable,
	OperatorSafeSessionState SafeState,
	DateTimeOffset? PreviousUpdatedAtUtc);

public sealed class OperatorSessionRecoveryStore
{
	private const int CurrentVersion = 1;

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true
	};

	private readonly string _path;
	private readonly Func<DateTimeOffset> _clock;
	private readonly SemaphoreSlim _writeGate = new(1, 1);

	public OperatorSessionRecoveryStore(
		string? path = null,
		Func<DateTimeOffset>? clock = null)
	{
		_path = path ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime",
			"operator-session.json");
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
	}

	public OperatorSessionRecoveryResult BeginSession(OperatorSafeSessionState currentSafeState)
	{
		ArgumentNullException.ThrowIfNull(currentSafeState);
		var current = currentSafeState.Normalize();
		var previous = TryRead(out var corrupted);
		var unexpected = previous is { CleanShutdown: false };
		var safeState = unexpected
			? (previous!.SafeState ?? OperatorSafeSessionState.Default).Normalize()
			: current;
		var previousUpdatedAt = previous?.UpdatedAtUtc;

		var persistenceAvailable = TryWrite(new OperatorSessionEnvelope(
			CurrentVersion,
			CleanShutdown: false,
			safeState,
			_clock()));

		return new OperatorSessionRecoveryResult(
			unexpected,
			corrupted,
			persistenceAvailable,
			safeState,
			previousUpdatedAt);
	}

	public Task<bool> CheckpointAsync(
		OperatorSafeSessionState safeState,
		CancellationToken cancellationToken = default) =>
		WriteAsync(safeState, cleanShutdown: false, cancellationToken);

	public Task<bool> MarkCleanShutdownAsync(
		OperatorSafeSessionState safeState,
		CancellationToken cancellationToken = default) =>
		WriteAsync(safeState, cleanShutdown: true, cancellationToken);

	private OperatorSessionEnvelope? TryRead(out bool corrupted)
	{
		corrupted = false;
		try
		{
			if (!File.Exists(_path))
				return null;

			var json = File.ReadAllText(_path);
			var envelope = JsonSerializer.Deserialize<OperatorSessionEnvelope>(json, SerializerOptions);
			if (envelope is null || envelope.Version != CurrentVersion || envelope.SafeState is null)
			{
				corrupted = true;
				return null;
			}

			return envelope with { SafeState = envelope.SafeState.Normalize() };
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			corrupted = true;
			return null;
		}
	}

	private async Task<bool> WriteAsync(
		OperatorSafeSessionState safeState,
		bool cleanShutdown,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(safeState);
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return TryWrite(new OperatorSessionEnvelope(
				CurrentVersion,
				cleanShutdown,
				safeState.Normalize(),
				_clock()));
		}
		finally
		{
			_writeGate.Release();
		}
	}

	private bool TryWrite(OperatorSessionEnvelope envelope)
	{
		var temporaryPath = _path + ".tmp";
		try
		{
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			var json = JsonSerializer.Serialize(envelope, SerializerOptions);
			File.WriteAllText(temporaryPath, json);
			File.Move(temporaryPath, _path, overwrite: true);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		finally
		{
			try
			{
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private sealed record OperatorSessionEnvelope(
		int Version,
		bool CleanShutdown,
		OperatorSafeSessionState SafeState,
		DateTimeOffset UpdatedAtUtc);
}
