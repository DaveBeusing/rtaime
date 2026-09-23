// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class DiagnosticsTests
{
	[Fact]
	public void Bounded_buffer_retains_only_the_newest_events_in_sequence_order()
	{
		var buffer = new BoundedDiagnosticBuffer(3);
		for (var index = 0; index < 5; index++)
			buffer.Record(DiagnosticSeverity.Information, "runtime", $"event.{index}", $"Event {index}");

		var snapshot = buffer.Snapshot();
		Assert.Equal(3, buffer.Count);
		Assert.Equal(new ulong[] { 2, 3, 4 }, snapshot.Select(item => item.Sequence));
		Assert.Equal(new[] { "event.2", "event.3", "event.4" }, snapshot.Select(item => item.Code));
	}

	[Fact]
	public void Redaction_removes_secret_dimensions_and_inline_credentials()
	{
		var buffer = new BoundedDiagnosticBuffer(4);
		buffer.Record(
			DiagnosticSeverity.Error,
			"ipc",
			"ipc.failure",
			"Connection failed token=abc123 Authorization:Bearer super-secret",
			new Failure("ipc.failure", "password=hunter2"),
			new Dictionary<string, string>
			{
				["apiKey"] = "visible-secret",
				["endpoint"] = "rtaime.v1.runtime.default"
			});

		var item = Assert.Single(buffer.Snapshot());
		Assert.DoesNotContain("abc123", item.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("super-secret", item.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("hunter2", item.Failure!.Value.Message, StringComparison.Ordinal);
		Assert.Equal("[REDACTED]", item.Dimensions["apiKey"]);
		Assert.Equal("rtaime.v1.runtime.default", item.Dimensions["endpoint"]);
	}

	[Fact]
	public void Support_snapshot_serialization_is_deterministic_and_sanitized()
	{
		var buffer = new BoundedDiagnosticBuffer(4);
		buffer.Record(
			DiagnosticSeverity.Warning,
			"runtime",
			"runtime.degraded",
			"Runtime degraded.",
			dimensions: new Dictionary<string, string> { ["zeta"] = "z", ["alpha"] = "a" },
			timestampUtc: new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
		var build = new ProductBuildInfo("0.1.0-dev", "DEV");
		var captured = new DateTimeOffset(2026, 9, 16, 12, 1, 0, TimeSpan.Zero);
		var snapshot = new SupportSnapshotBuilder(build, "RuntimeHost", 42, "Ready", "Healthy")
			.Identity("instanceId", "runtime-1")
			.Status("zeta", "z")
			.Status("alpha", "a")
			.Counter("droppedFrames", 7)
			.Configuration("password", "must-not-leak")
			.Configuration("endpoint", "rtaime.v1.runtime.default")
			.Events(buffer.Snapshot())
			.Build(captured);

		var first = SupportSnapshotSerializer.Serialize(snapshot);
		var second = SupportSnapshotSerializer.Serialize(snapshot);
		Assert.Equal(first, second);
		Assert.DoesNotContain("must-not-leak", first, StringComparison.Ordinal);
		Assert.Contains("\"password\": \"[REDACTED]\"", first, StringComparison.Ordinal);
		Assert.True(first.IndexOf("\"alpha\"", StringComparison.Ordinal) < first.IndexOf("\"zeta\"", StringComparison.Ordinal));
		Assert.DoesNotContain("pixels", first, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("payload", first, StringComparison.OrdinalIgnoreCase);
	}


	[Fact]
	public void Exception_detail_redaction_is_bounded_and_removes_inline_secrets()
	{
		var detail = string.Join("\n", Enumerable.Range(0, 120).Select(index => $"line {index} token=secret-{index}"));
		var redacted = DiagnosticRedactor.RedactExceptionDetail(detail);

		Assert.DoesNotContain("secret-", redacted, StringComparison.Ordinal);
		Assert.Contains("token=[REDACTED]", redacted, StringComparison.Ordinal);
		Assert.Contains("[TRUNCATED]", redacted, StringComparison.Ordinal);
		Assert.True(redacted.Split(Environment.NewLine).Length <= 97);
	}

	[Fact]
	public void Host_log_writes_structured_redacted_json_lines()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-host-log-tests", Guid.NewGuid().ToString("N"));
		try
		{
			using (var log = HostLog.Open(
				"RuntimeHost",
				new[]
				{
					$"--log-root={root}",
					"--log-session-id=session-test",
					"--instance-id=runtime-1",
					"--log-level=Trace"
				},
				publishEnvironment: false))
			{
				log.Error(
					"ipc",
					"ipc.failure",
					"Runtime IPC failed token=abc123.",
					new InvalidOperationException("password=hunter2"),
					new Dictionary<string, string>
					{
						["endpoint"] = "rtaime.v1.runtime.default",
						["apiKey"] = "visible-secret"
					});
			}

			var sessionDirectory = Path.Combine(root, "session-test");
			var file = Assert.Single(Directory.GetFiles(sessionDirectory, "*.jsonl"));
			var lines = File.ReadAllLines(file);
			Assert.True(lines.Length >= 2);

			using var document = JsonDocument.Parse(lines[^1]);
			var record = document.RootElement;
			Assert.Equal(HostLog.CurrentSchemaVersion, record.GetProperty("schemaVersion").GetString());
			Assert.Equal("RuntimeHost", record.GetProperty("host").GetString());
			Assert.Equal("session-test", record.GetProperty("sessionId").GetString());
			Assert.Equal("runtime-1", record.GetProperty("instanceId").GetString());
			Assert.Equal("Error", record.GetProperty("level").GetString());
			Assert.Equal("ipc.failure", record.GetProperty("code").GetString());
			Assert.DoesNotContain("abc123", lines[^1], StringComparison.Ordinal);
			Assert.DoesNotContain("hunter2", lines[^1], StringComparison.Ordinal);
			Assert.DoesNotContain("visible-secret", lines[^1], StringComparison.Ordinal);
			Assert.Equal("[REDACTED]", record.GetProperty("dimensions").GetProperty("apiKey").GetString());
			Assert.Equal("System.InvalidOperationException", record.GetProperty("exception").GetProperty("type").GetString());
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Host_log_keeps_process_failures_best_effort_and_never_exposes_raw_exception_secrets()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-host-log-tests", Guid.NewGuid().ToString("N"));
		try
		{
			using (var log = HostLog.Open(
				"ControlHost",
				new[] { $"--log-root={root}", "--log-session-id=failure-test" },
				publishEnvironment: false))
			using (var subscription = log.AttachProcessFailureHandlers())
			{
				log.Critical(
					"process",
					"process.test-failure",
					"Test failure secret=top-secret.",
					new ApplicationException("Authorization:Bearer secret-value"));
				log.Flush();
			}

			var file = Assert.Single(Directory.GetFiles(Path.Combine(root, "failure-test"), "*.jsonl"));
			var content = File.ReadAllText(file);
			Assert.Contains("process.test-failure", content, StringComparison.Ordinal);
			Assert.DoesNotContain("top-secret", content, StringComparison.Ordinal);
			Assert.DoesNotContain("secret-value", content, StringComparison.Ordinal);
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Host_log_file_failure_does_not_fail_the_calling_host()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-host-log-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var fileInsteadOfDirectory = Path.Combine(root, "blocked-root");
		File.WriteAllText(fileInsteadOfDirectory, "not-a-directory");

		try
		{
			using var log = HostLog.Open(
				"AIHost",
				new[] { $"--log-root={fileInsteadOfDirectory}", "--log-session-id=file-failure-test" },
				publishEnvironment: false);

			var exception = Record.Exception(() =>
				log.Error("storage", "logging.path-unavailable", "Logging path is unavailable."));

			Assert.Null(exception);
			Assert.Null(log.CurrentFilePath);
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Host_log_rotates_before_exceeding_configured_segment_limit()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-host-log-tests", Guid.NewGuid().ToString("N"));
		try
		{
			using (var log = HostLog.Open(
				"RuntimeHost",
				new[]
				{
					$"--log-root={root}",
					"--log-session-id=rotation-test",
					"--log-max-file-mb=1"
				},
				publishEnvironment: false))
			{
				var dimensions = Enumerable.Range(0, 32)
					.ToDictionary(index => $"field-{index:00}", _ => new string('x', 512), StringComparer.Ordinal);
				for (var index = 0; index < 96; index++)
					log.Information("rotation", "rotation.test", $"Rotation record {index}.", dimensions);
			}

			var files = Directory.GetFiles(Path.Combine(root, "rotation-test"), "*.jsonl");
			Assert.True(files.Length >= 2);
			Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 1024L * 1024L));
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Host_log_prunes_expired_inactive_sessions()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-host-log-tests", Guid.NewGuid().ToString("N"));
		var expiredSession = Path.Combine(root, "expired-session");
		Directory.CreateDirectory(expiredSession);
		var expiredFile = Path.Combine(expiredSession, "rtaime-runtimehost-expired-000.jsonl");
		File.WriteAllText(expiredFile, "{}");
		File.SetLastWriteTimeUtc(expiredFile, DateTime.UtcNow.AddDays(-10));

		try
		{
			using var log = HostLog.Open(
				"ControlHost",
				new[]
				{
					$"--log-root={root}",
					"--log-session-id=current-session",
					"--log-retention-days=1"
				},
				publishEnvironment: false);

			Assert.False(Directory.Exists(expiredSession));
			Assert.True(Directory.Exists(log.SessionDirectory));
		}
		finally
		{
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Product_build_info_uses_informational_version_without_source_revision_suffix()
	{
		var build = ProductBuildInfo.FromAssembly(typeof(DiagnosticsTests).Assembly);
		Assert.False(string.IsNullOrWhiteSpace(build.ProductVersion));
		Assert.False(string.IsNullOrWhiteSpace(build.ReleaseStage));
	}
}
