// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class MediaQcArchitectureTests
{
	[Fact]
	public void QC_runner_is_off_hotpath_and_uses_existing_catalogue_readonly_identity()
	{
		var root = RepositorySnapshot.Load().Root;
		var worker = Read(root, "src/Client/rtaime.Client/MediaQcScheduler.cs");
		var inspector = Read(root, "src/Client/rtaime.Client/MediaQcFileInspector.cs");
		var operatorView = Read(root, "src/Hosts/rtaime.Operator/MediaPoolOfflineQc.cs");
		Assert.Contains("Channel.CreateBounded<Job>", worker, StringComparison.Ordinal);
		Assert.Contains("MaximumQueued = 4", worker, StringComparison.Ordinal);
		Assert.Contains("MaximumInFlight = MaximumQueued + 1", worker, StringComparison.Ordinal);
		Assert.Contains("MediaQcEbuXml.Export(report)", worker, StringComparison.Ordinal);
		Assert.Contains("File.Move(temporary, path, overwrite: true)", worker, StringComparison.Ordinal);
		Assert.Contains("SHA256", inspector, StringComparison.Ordinal);
		Assert.Contains("FileOptions.Asynchronous | FileOptions.SequentialScan", inspector, StringComparison.Ordinal);
		Assert.Contains("MediaAssetDescriptor", operatorView, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeProgramExecutionService", worker, StringComparison.Ordinal);
		Assert.DoesNotContain("PreparedExecutionContract", worker, StringComparison.Ordinal);
		Assert.DoesNotContain("MediaDeck.Open", worker, StringComparison.Ordinal);
		Assert.DoesNotContain("ProductionJournal", worker, StringComparison.Ordinal);
		Assert.DoesNotContain("PeriodicTimer", worker, StringComparison.Ordinal);
	}

	[Fact]
	public void Schema_files_are_exactly_version_pinned_and_independent_of_legacy_model()
	{
		var root = RepositorySnapshot.Load().Root;
		var schema = Read(root, "schemas/quality-control/ebu/2026-04/qc.xsd");
		var timing = Read(root, "schemas/quality-control/ebu/2026-04/TimingExtensionMediaPlaybackEditUnits.xsd");
		var csproj = Read(root, "src/Client/rtaime.Client/rtaime.Client.csproj");
		Assert.Contains("tag:qc.ebu.ch,2026-04", schema, StringComparison.Ordinal);
		Assert.Contains("CheckResultDefinition", schema, StringComparison.Ordinal);
		Assert.Contains("tag:qc.ebu.ch,2026-04:extensions:timing", timing, StringComparison.Ordinal);
		Assert.Contains("rtaime.Qc202604.qc.xsd", csproj, StringComparison.Ordinal);
		Assert.Contains("rtaime.Qc202604.timing.xsd", csproj, StringComparison.Ordinal);
		Assert.DoesNotContain("2026-01", schema, StringComparison.Ordinal);
	}

	private static string Read(string root, string relative) =>
		File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
