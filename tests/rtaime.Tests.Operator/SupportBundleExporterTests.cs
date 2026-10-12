// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.IO.Compression;
using System.Text.Json;
using rtaime.Client;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class SupportBundleExporterTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"rtaime-support-bundle-tests",
		Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task Export_includes_correlated_logs_health_lifecycle_and_redacts_support_metadata()
	{
		var sessionId = "support-test-session";
		var logRoot = Path.Combine(_root, "logs");
		var sessionDirectory = Path.Combine(logRoot, sessionId);
		Directory.CreateDirectory(sessionDirectory);

		var logPath = Path.Combine(sessionDirectory, "rtaime-operator-123-20260923-200000000-000.jsonl");
		await File.WriteAllTextAsync(
			logPath,
			"{\"schemaVersion\":\"1.0\",\"message\":\"already redacted\"}" + Environment.NewLine);
		var crashPath = Path.Combine(sessionDirectory, "operator-crash-20260923-200000000.log");
		await File.WriteAllTextAsync(crashPath, "failure detail [REDACTED]");

		var lifecyclePath = Path.Combine(_root, "apphost-lifecycle.json");
		Directory.CreateDirectory(_root);
		await File.WriteAllTextAsync(
			lifecyclePath,
			"""
			{
			  "schemaVersion": "1.0",
			  "diagnosticPath": "C:\\logs\\startup.log",
			  "failureReason": "authorization=Bearer secret-value",
			  "token": "secret-token"
			}
			""");

		var capturedAt = new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);
		var provider = new FixedHealthProvider(
		[
			new SubsystemHealthSnapshot(
				"control",
				"Control Service",
				"Lifecycle",
				SubsystemHealthState.Healthy,
				"Connected token=secret-value",
				capturedAt.AddMinutes(-1),
				capturedAt.AddSeconds(-5),
				[new HealthMetricSnapshot("API token", "secret-token")],
				"No recovery required.",
				"authorization: Bearer secret-value")
		]);
		var exporter = new SupportBundleExporter(
			provider,
			() => capturedAt,
			sessionIdProvider: () => sessionId,
			logRootProvider: () => logRoot);
		var destination = Path.Combine(_root, "bundle.zip");

		var result = await exporter.ExportAsync(destination, lifecyclePath);

		Assert.Equal(destination, result.Path);
		Assert.Empty(result.Warnings);
		Assert.True(result.FileCount >= 5);
		Assert.True(result.SourceBytes > 0);

		using var archive = ZipFile.OpenRead(destination);
		var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
		Assert.Contains("Manifest.json", names);
		Assert.Contains("health/OperatorHealth.json", names);
		Assert.Contains("startup/AppHostLifecycle.json", names);
		Assert.Contains("logs/" + Path.GetFileName(logPath), names);
		Assert.Contains("logs/" + Path.GetFileName(crashPath), names);

		var healthJson = await ReadEntryAsync(archive, "health/OperatorHealth.json");
		Assert.DoesNotContain("secret-value", healthJson, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-token", healthJson, StringComparison.Ordinal);
		Assert.Contains("[REDACTED]", healthJson, StringComparison.Ordinal);

		var lifecycleJson = await ReadEntryAsync(archive, "startup/AppHostLifecycle.json");
		Assert.DoesNotContain("secret-value", lifecycleJson, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-token", lifecycleJson, StringComparison.Ordinal);
		Assert.Contains("[REDACTED]", lifecycleJson, StringComparison.Ordinal);

		var manifestJson = await ReadEntryAsync(archive, "Manifest.json");
		using var manifest = JsonDocument.Parse(manifestJson);
		Assert.Equal("1.0", manifest.RootElement.GetProperty("schemaVersion").GetString());
		Assert.Equal(sessionId, manifest.RootElement.GetProperty("session").GetProperty("id").GetString());
		Assert.Equal("Operator", manifest.RootElement.GetProperty("process").GetProperty("host").GetString());
		Assert.True(manifest.RootElement.GetProperty("runtime").TryGetProperty("framework", out _));
		Assert.True(manifest.RootElement.GetProperty("runtime").TryGetProperty("os", out _));
	}

	[Fact]
	public async Task Export_omits_session_files_after_bounded_source_limit()
	{
		var sessionId = "bounded-session";
		var logRoot = Path.Combine(_root, "bounded-logs");
		var sessionDirectory = Path.Combine(logRoot, sessionId);
		Directory.CreateDirectory(sessionDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(sessionDirectory, "rtaime-runtimehost-1-20260923-200000000-000.jsonl"),
			new string('x', 1024));

		var exporter = new SupportBundleExporter(
			new FixedHealthProvider([]),
			maximumSourceBytes: 64,
			sessionIdProvider: () => sessionId,
			logRootProvider: () => logRoot);
		var destination = Path.Combine(_root, "bounded.zip");

		var result = await exporter.ExportAsync(destination, lifecycleEvidencePath: null);

		Assert.Contains(
			result.Warnings,
			warning => warning.Contains("source limit", StringComparison.OrdinalIgnoreCase));
		using var archive = ZipFile.OpenRead(destination);
		Assert.DoesNotContain(
			archive.Entries,
			entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
		Assert.Contains(archive.Entries, entry => entry.FullName == "Manifest.json");
		Assert.Contains(archive.Entries, entry => entry.FullName == "health/OperatorHealth.json");
	}

	[Fact]
	public async Task Incident_export_preserves_provenance_and_rejects_private_key_material()
	{
		var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
		var snapshot = new SubsystemHealthSnapshot(
			"recording", "Recording", "Recording", SubsystemHealthState.Failed,
			"Recording failed token=do-not-export", now, null, [],
			"Review recording diagnostics.", "-----BEGIN " + "PRIVATE KEY" + "-----\\nnot-a-real-key",
			ProgramImpact: SubsystemProgramImpact.RecordingOnly,
			EvidenceSource: "Control-confirmed recording snapshot", LastObservedAt: now.AddSeconds(2));
		var exporter = new SupportBundleExporter(new FixedHealthProvider([snapshot]),
			sessionIdProvider: () => null, logRootProvider: () => _root);
		var destination = Path.Combine(_root, "incident.zip");
		await exporter.ExportAsync(destination, null);
		using var archive = ZipFile.OpenRead(destination);
		var json = await ReadEntryAsync(archive, "health/OperatorHealth.json");
		Assert.DoesNotContain("do-not-export", json, StringComparison.Ordinal);
		Assert.DoesNotContain("not-a-real-key", json, StringComparison.Ordinal);
		using var document = JsonDocument.Parse(json);
		var incident = document.RootElement[0];
		Assert.Equal("RecordingOnly", incident.GetProperty("programImpact").GetString());
		Assert.Equal(snapshot.EvidenceSource, incident.GetProperty("evidenceSource").GetString());
		Assert.Equal(now.AddSeconds(2), incident.GetProperty("lastProjectedAtUtc").GetDateTimeOffset());
		Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("media", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task Cancelled_export_does_not_leave_a_final_or_temporary_bundle()
	{
		var exporter = new SupportBundleExporter(new FixedHealthProvider([]),
			sessionIdProvider: () => null, logRootProvider: () => _root);
		var destination = Path.Combine(_root, "cancelled.zip");
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(destination, null, cancellation.Token));
		Assert.False(File.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
	}

	[Fact]
	public async Task Export_includes_retained_capture_with_matching_identity_and_redacted_streams()
	{
		var capture = Guid.NewGuid().ToString("N");
		var directory = Path.Combine(_root, "startup-evidence", capture);
		Directory.CreateDirectory(directory);
		var lifecycle = Path.Combine(_root, "apphost-lifecycle.json");
		await File.WriteAllTextAsync(lifecycle, JsonSerializer.Serialize(new { captureId = capture, sessionId = "retained-session" }));
		foreach (var name in new[] { "manifest.json", "before-shutdown.json", "apphost-shutdown.json", "apphost-lifecycle.json" })
			await File.WriteAllTextAsync(Path.Combine(directory, name),
				JsonSerializer.Serialize(new { sessionId = "retained-session", captureId = capture, password = "capture-secret" }));
		await File.WriteAllTextAsync(Path.Combine(directory, "controlhost-process.log"),
			"sessionId=retained-session stream=stderr token=stream-secret" + Environment.NewLine);
		await File.WriteAllTextAsync(Path.Combine(directory, "production-state.json"), "must not export");
		var destination = Path.Combine(_root, "retained.zip");
		var exporter = new SupportBundleExporter(new FixedHealthProvider([]),
			sessionIdProvider: () => null, logRootProvider: () => _root);
		var result = await exporter.ExportAsync(destination, lifecycle);
		Assert.True(result.SourceBytes > 0);
		using var archive = ZipFile.OpenRead(destination);
		foreach (var name in new[] { "manifest.json", "before-shutdown.json", "apphost-shutdown.json", "apphost-lifecycle.json", "controlhost-process.log" })
		{
			var content = await ReadEntryAsync(archive, "startup/" + capture + "/" + name);
			Assert.Contains("retained-session", content);
			Assert.DoesNotContain("capture-secret", content);
			Assert.DoesNotContain("stream-secret", content);
			Assert.Contains("[REDACTED]", content);
		}
		Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("production-state", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Export_never_follows_a_capture_identifier_outside_the_owned_directory()
	{
		Directory.CreateDirectory(_root);
		var lifecycle = Path.Combine(_root, "apphost-lifecycle.json");
		await File.WriteAllTextAsync(lifecycle, """{"captureId":"../../sensitive","retainedEvidencePath":"C:\\sensitive\\secret.json"}""");
		var exporter = new SupportBundleExporter(new FixedHealthProvider([]),
			sessionIdProvider: () => null, logRootProvider: () => _root);
		var destination = Path.Combine(_root, "safe.zip");
		await exporter.ExportAsync(destination, lifecycle);
		using var archive = ZipFile.OpenRead(destination);
		Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("before-shutdown", StringComparison.Ordinal));
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
	{
		var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == name);
		await using var stream = entry.Open();
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync();
	}

	private sealed class FixedHealthProvider(
		IReadOnlyList<SubsystemHealthSnapshot> snapshots) : IHealthSnapshotProvider
	{
		public event EventHandler<HealthSnapshotChangedEventArgs>? Changed
		{
			add { }
			remove { }
		}

		public IReadOnlyList<SubsystemHealthSnapshot> GetCurrent() => snapshots;
	}
}
