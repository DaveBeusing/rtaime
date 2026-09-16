// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
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
	public void Product_build_info_uses_informational_version_without_source_revision_suffix()
	{
		var build = ProductBuildInfo.FromAssembly(typeof(DiagnosticsTests).Assembly);
		Assert.False(string.IsNullOrWhiteSpace(build.ProductVersion));
		Assert.False(string.IsNullOrWhiteSpace(build.ReleaseStage));
	}
}
