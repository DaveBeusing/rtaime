// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class BroadcastContinuityOperatorTests
{
	[Theory]
	[InlineData("CONTINUITY FAILED · incident 12 / runtime.continuity.input_lost", "INCIDENT ACTIVE")]
	[InlineData("CONTINUITY RECOVERED AWAITING RE-ARM · incident 12; approval NOT CONFIRMED", "NOT CONFIRMED")]
	[InlineData("CONTINUITY SOFTWARE OBSERVED · frame 50; physical scan-out UNVERIFIED", "physical playout UNVERIFIED")]
	[InlineData("CONTINUITY UNVERIFIED · no complete output", "CONTINUITY UNVERIFIED")]
	public void Operator_continuity_status_is_read_only_runtime_evidence_not_an_optimistic_failover(
		string evidence, string expected)
	{
		var output = new OutputStatusViewModel("program", "PROGRAM");
		var changed = new List<string>();
		output.PropertyChanged += (_, args) => { if (args.PropertyName is not null) changed.Add(args.PropertyName); };
		Update(output, evidence);
		Assert.Contains(expected, output.ContinuityStatus, StringComparison.Ordinal);
		Assert.Contains(nameof(OutputStatusViewModel.ContinuityStatus), changed);
		Assert.Equal("GOVERNED PROGRAM ROLE · PHYSICAL SCAN-OUT UNVERIFIED", output.OutputKind);
	}

	[Fact]
	public void Non_program_presentation_cannot_impersonate_a_governed_failover()
	{
		var preview = new OutputStatusViewModel("preview", "PREVIEW");
		Update(preview, "CONTINUITY FAILED · stale remote observation");
		Assert.Contains("NOT A GOVERNED PHYSICAL ROLE", preview.ContinuityStatus, StringComparison.Ordinal);
	}

	[Fact]
	public void Output_ui_exposes_recovery_without_a_fake_operator_rearm_or_autonomous_program_action()
	{
		var root = FindRepositoryRoot();
		var xaml = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator",
			"OutputRoutingHealthControl.xaml"));
		var view = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator",
			"OutputRoutingHealthViewModel.cs"));
		Assert.Contains("Binding SelectedOutput.ContinuityStatus", xaml, StringComparison.Ordinal);
		Assert.Contains("A local notification acknowledgement does not authorize Program re-arm.", xaml, StringComparison.Ordinal);
		Assert.DoesNotContain("ContinuityRearmCommand", view, StringComparison.Ordinal);
		Assert.DoesNotContain("ApplyProductionCgText", view, StringComparison.Ordinal);
	}

	private static void Update(OutputStatusViewModel model, string detail) =>
		model.Update(
			target: "VIRTUAL", assignedSource: "INPUT A",
			resolution: "1920×1080", frameRate: "50", pixelFormat: "RGBA8", colorSpace: "UNVERIFIED",
			status: "WARNING", evidenceState: "UNVERIFIED", detail: detail,
			recordingStatus: "UNAVAILABLE", streamingStatus: "UNAVAILABLE",
			streamingTarget: "UNAVAILABLE", streamingProvider: "UNAVAILABLE",
			streamingBitrate: "UNAVAILABLE", streamingQueue: "UNAVAILABLE",
			streamingDrops: "UNAVAILABLE", streamingReconnects: "UNAVAILABLE");

	private static string FindRepositoryRoot()
	{
		for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
			if (File.Exists(Path.Combine(folder.FullName, "rtaime.slnx")))
				return folder.FullName;
		throw new DirectoryNotFoundException("Unable to locate rtaime.slnx for Operator qualification.");
	}
}
