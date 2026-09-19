// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class CompositingGraphProjectionTests
{
	[Fact]
	public void Projection_maps_sources_routing_composite_outputs_and_recorder()
	{
		var graph = CompositingGraphProjector.Project(CreateInput());

		Assert.Contains(graph.Nodes, node => node.Id == "source:camera-a" && node.Kind == CompositingGraphNodeKind.Source);
		Assert.Contains(graph.Nodes, node => node.Id == "routing" && node.Kind == CompositingGraphNodeKind.Routing);
		Assert.Contains(graph.Nodes, node => node.Id == "graphics-transform" && node.Kind == CompositingGraphNodeKind.Transform);
		Assert.Contains(graph.Nodes, node => node.Id == "composite" && node.Kind == CompositingGraphNodeKind.Composite);
		Assert.Contains(graph.Nodes, node => node.Id == "program-output" && node.Kind == CompositingGraphNodeKind.Output);
		Assert.Contains(graph.Nodes, node => node.Id == "recorder" && node.Kind == CompositingGraphNodeKind.Recorder);
		Assert.Contains(graph.Connections, connection =>
			connection.FromNodeId == "composite" &&
			connection.ToNodeId == "program-output");
		Assert.Contains(graph.Connections, connection =>
			connection.FromNodeId == "program-output" &&
			connection.ToNodeId == "recorder");
	}

	[Fact]
	public void Projection_preserves_stable_ids_across_status_updates()
	{
		var first = CompositingGraphProjector.Project(CreateInput());
		var second = CompositingGraphProjector.Project(CreateInput() with
		{
			RuntimeStatus = "DEGRADED",
			RecordingStatus = "RECORDING"
		});

		Assert.Equal(
			first.Nodes.Select(node => node.Id),
			second.Nodes.Select(node => node.Id));
		Assert.Equal(
			first.Connections.Select(connection => connection.Id),
			second.Connections.Select(connection => connection.Id));
	}

	[Fact]
	public void Projection_marks_failed_source_and_degraded_runtime_without_false_green()
	{
		var input = CreateInput() with
		{
			RuntimeStatus = "DEGRADED",
			RuntimeHealth = "FAIL"
		};
		input = input with
		{
			Sources =
			[
				new("camera-a", "Camera A", "LIVE", "1920x1080", "OFFLINE", true, false)
			]
		};

		var graph = CompositingGraphProjector.Project(input);

		Assert.Equal(
			CompositingGraphHealth.Error,
			Assert.Single(graph.Nodes.Where(node => node.Id == "source:camera-a")).Health);
		Assert.Equal(
			CompositingGraphHealth.Error,
			Assert.Single(graph.Nodes.Where(node => node.Id == "composite")).Health);
		Assert.Equal(
			CompositingGraphHealth.Error,
			Assert.Single(graph.Nodes.Where(node => node.Id == "program-output")).Health);
	}

	[Fact]
	public void Projection_keeps_unsupported_rewire_disabled()
	{
		var graph = CompositingGraphProjector.Project(CreateInput());

		Assert.All(graph.Nodes, node => Assert.False(node.CanRewire));
	}

	[Fact]
	public void Projection_handles_large_graph_with_bounded_smoke_cost()
	{
		var sources = Enumerable.Range(0, 1024)
			.Select(index => new CompositingGraphSourceProjection(
				$"source-{index:N4}",
				$"Source {index:N4}",
				"LIVE",
				"1920x1080p50",
				"READY",
				index == 0,
				index == 1))
			.ToArray();
		var input = CreateInput() with { Sources = sources };

		var stopwatch = Stopwatch.StartNew();
		var graph = CompositingGraphProjector.Project(input);
		stopwatch.Stop();

		Assert.Equal(sources.Length + 7, graph.Nodes.Count);
		Assert.Equal(sources.Length + 6, graph.Connections.Count);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Projection took {stopwatch.Elapsed}.");
	}

	private static CompositingGraphProjectionInput CreateInput() =>
		new(
			[
				new("camera-a", "Camera A", "LIVE", "1920x1080p50", "READY", true, false),
				new("camera-b", "Camera B", "LIVE", "1920x1080p50", "READY", false, true)
			],
			"camera-a",
			"camera-b",
			"READY",
			"PASS",
			"PASS",
			"CONFIRMED · REV 4",
			"READY",
			"1920x1080p50 RGBA8",
			"GRAPHICS ON",
			"LowerThird.png",
			"384x96",
			"ON AIR",
			true,
			72,
			6,
			1,
			"IDLE",
			null);
}
