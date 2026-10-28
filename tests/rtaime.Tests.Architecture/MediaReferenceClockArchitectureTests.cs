// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class MediaReferenceClockArchitectureTests
{
	[Fact]
	public void Runtime_keeps_single_rational_Program_schedule_and_clock_is_observational()
	{
		var root = RepositorySnapshot.Load().Root;
		var runtime = Read(root, "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs");
		var engine = Read(root, "src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs");
		var virtualClock = Read(root, "src/Runtime/rtaime.Runtime/VirtualMediaReferenceClock.cs");
		var operatorUi = Read(root, "src/Hosts/rtaime.Operator/OperatorReferenceClock.cs");
		Assert.Contains("new RationalFrameSchedule(", runtime, StringComparison.Ordinal);
		Assert.Contains("runtime.ConfigureReferenceClockRequirement(", runtime, StringComparison.Ordinal);
		Assert.Contains("MediaReferenceAdmission.Allows(", engine, StringComparison.Ordinal);
		Assert.Contains("RuntimePrepareStatus.Rejected", engine, StringComparison.Ordinal);
		Assert.Contains("reference-clock", Read(root,
			"src/Hosts/rtaime.Operator/OperatorHealthSnapshotProvider.cs"), StringComparison.Ordinal);
		Assert.DoesNotContain("Task.Delay(", virtualClock, StringComparison.Ordinal);
		Assert.DoesNotContain("Stopwatch.StartNew", virtualClock, StringComparison.Ordinal);
		Assert.DoesNotContain("new RationalFrameSchedule", virtualClock, StringComparison.Ordinal);
		Assert.DoesNotContain("PtpSocket", operatorUi, StringComparison.Ordinal);
		Assert.DoesNotContain("MediaReferenceClockObservation", Read(root,
			"src/Hosts/rtaime.ControlHost/ControlHostService.cs"), StringComparison.Ordinal);
	}

	[Fact]
	public void Every_management_snapshot_hop_preserves_optional_clock_evidence()
	{
		var root = RepositorySnapshot.Load().Root;
		foreach (var path in new[]
		{
			"src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs",
			"src/Hosts/rtaime.RuntimeHost/RuntimeHostIpcServer.cs",
			"src/Hosts/rtaime.ControlHost/RuntimeHostIpcTransport.cs",
			"src/Hosts/rtaime.ControlHost/ControlHostIpcServer.cs",
			"src/Client/rtaime.Client/NamedPipeOperatorControlTransport.cs",
			"src/Client/rtaime.Client/OperatorControlClient.cs"
		})
			Assert.Contains("MediaReferenceClockObservation? ClockReference",
				Read(root, path), StringComparison.Ordinal);
	}

	private static string Read(string root, string relative) =>
		File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
