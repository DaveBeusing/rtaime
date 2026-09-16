// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.AIHost;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class SupportSnapshotIntegrationTests
{
	[Fact]
	public void Host_support_snapshot_projections_share_schema_and_exclude_bulk_media()
	{
		var captured = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
		var snapshots = new[]
		{
			ControlHostDiagnostics.Capture(new ControlHostProcess(ControlHostProcessOptions.Default), captured),
			RuntimeHostDiagnostics.Capture(new RuntimeHostProcess(RuntimeHostProcessOptions.Default), captured),
			AIHostDiagnostics.Capture(new AIHostProcess(AIHostProcessOptions.Default), captured)
		};

		Assert.Equal(new[] { "ControlHost", "RuntimeHost", "AIHost" }, snapshots.Select(snapshot => snapshot.Host));
		foreach (var snapshot in snapshots)
		{
			Assert.Equal(SupportSnapshotBuilder.CurrentSchemaVersion, snapshot.SchemaVersion);
			Assert.Equal(captured, snapshot.CapturedAtUtc);
			var json = SupportSnapshotSerializer.Serialize(snapshot);
			Assert.DoesNotContain("pixels", json, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
		}
	}
}
