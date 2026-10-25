// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;

namespace rtaime.ControlHost;

/// <summary>
/// Records launch ownership independently of transient readiness. A flushed reservation precedes every
/// Process.Start, so a crash between launch and identity capture leaves an explicit unknown outcome.
/// The existing lifetime start budgets bound the number of retained generations.
/// </summary>
internal sealed class LocalProcessOwnershipJournal
{
	private const long MaximumJournalBytes = 4 * 1024 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};
	private readonly object _gate = new();
	private readonly string _path;
	private readonly string _directory;
	private Snapshot _snapshot;
	public string LifecycleRunId => _snapshot.LifecycleRunId;

	public LocalProcessOwnershipJournal(
		string path,
		string lifecycleRunId,
		LocalProcessIdentity ownerIdentity,
		Child[] children)
	{
		_path = Path.GetFullPath(path);
		_directory = Path.GetDirectoryName(_path)!;
		_snapshot = new Snapshot("1.0", lifecycleRunId, ownerIdentity, children, []);
		Directory.CreateDirectory(_directory);
		if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
			throw new IOException("Durable child ownership cannot use a redirected ownership directory.");
		Write(_snapshot, overwrite: false);
	}

	public Launch Reserve(string name, string endpoint, string hostPath, string launchId, int attempt)
	{
		lock (_gate)
		{
			var child = _snapshot.Children.Single(candidate => candidate.Name == name);
			if (child.Endpoint != endpoint || Path.GetFullPath(child.HostPath) != Path.GetFullPath(hostPath) ||
				attempt <= 0 || attempt > child.MaxStartAttempts ||
				_snapshot.Generations.Any(candidate => candidate.LaunchId == launchId || candidate.Role == child.Role &&
					(candidate.Attempt == attempt || candidate.State is "LAUNCH_PENDING" or "RUNNING")))
				throw new InvalidOperationException("Child launch does not match its durable ownership declaration.");
			var launch = new Launch(this, child.Role, launchId,
				Path.Combine(_directory, $"{child.Role}-{launchId}.stop"),
				Path.Combine(_directory, $"{child.Role}-{launchId}.ready.json"));
			var generation = new Generation(child.Role, launchId, attempt, "LAUNCH_PENDING", null);
			Commit(_snapshot with { Generations = [.. _snapshot.Generations, generation] });
			return launch;
		}
	}

	private void Transition(Launch launch, string state, LocalProcessIdentity? identity = null)
	{
		lock (_gate)
		{
			var index = Array.FindIndex(_snapshot.Generations, candidate => candidate.LaunchId == launch.LaunchId);
			if (index < 0) throw new InvalidOperationException("Child launch has no durable reservation.");
			var current = _snapshot.Generations[index];
			if (current.State is "EXITED" or "NOT_STARTED")
				throw new InvalidOperationException("Child launch ownership has already completed.");
			if (state is "RUNNING" or "NOT_STARTED" && current.State != "LAUNCH_PENDING")
				throw new InvalidOperationException("Child launch ownership has already been published.");
			if (identity is not null && (identity.LaunchId != launch.LaunchId || identity.LifecycleRunId != _snapshot.LifecycleRunId))
				throw new InvalidOperationException("Child identity does not match its launch reservation.");
			var generations = (Generation[])_snapshot.Generations.Clone();
			generations[index] = current with { State = state, ProcessIdentity = identity ?? current.ProcessIdentity };
			Commit(_snapshot with { Generations = generations });
		}
	}

	private void Commit(Snapshot next)
	{
		Write(next, overwrite: true);
		_snapshot = next;
	}

	private void Write(Snapshot snapshot, bool overwrite)
	{
		var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
		try
		{
			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				JsonSerializer.Serialize(stream, snapshot, JsonOptions);
				if (stream.Length > MaximumJournalBytes)
					throw new IOException("Durable child ownership exceeded its 4 MiB journal limit; no further launch is permitted.");
				stream.Flush(flushToDisk: true);
			}
			File.Move(temporary, _path, overwrite);
		}
		finally
		{
			try { File.Delete(temporary); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}
	}

	internal sealed record Child(string Role, string Name, string Endpoint, string HostPath, int MaxStartAttempts);
	private sealed record Snapshot(string SchemaVersion, string LifecycleRunId, LocalProcessIdentity ControlIdentity, Child[] Children, Generation[] Generations);
	private sealed record Generation(string Role, string LaunchId, int Attempt, string State, LocalProcessIdentity? ProcessIdentity);

	internal sealed class Launch(LocalProcessOwnershipJournal journal, string role, string launchId, string stopPath, string readinessPath)
	{
		public string Role { get; } = role;
		public string LaunchId { get; } = launchId;
		public string StopPath { get; } = stopPath;
		public string ReadinessPath { get; } = readinessPath;
		public void Started(LocalProcessIdentity identity) => journal.Transition(this, "RUNNING", identity);
		public void NotStarted() => journal.Transition(this, "NOT_STARTED");
		public void Exited(Process process)
		{
			if (!process.HasExited) throw new InvalidOperationException("A live child cannot release durable launch ownership.");
			journal.Transition(this, "EXITED");
		}
	}
}
