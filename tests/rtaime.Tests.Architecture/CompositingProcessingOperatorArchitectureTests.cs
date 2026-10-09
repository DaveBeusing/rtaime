// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class CompositingProcessingOperatorArchitectureTests
{
	[Fact]
	public void Processing_workflow_reuses_governed_stack_mutation_path()
	{
		var repo = RepositorySnapshot.Load();
		var inspector = Read(repo, "src/Hosts/rtaime.Operator/MediaPoolInspectorViewModel.cs");
		var operatorViewModel = Read(repo, "src/Hosts/rtaime.Operator/OperatorViewModel.cs");

		Assert.Contains("_operator.SetCompositingLayerProcessingStackAsync", inspector, StringComparison.Ordinal);
		Assert.Contains("ReplaceProcessingNodeAsync", inspector, StringComparison.Ordinal);
		Assert.Contains("ResolveSelectedProcessingNodeId", inspector, StringComparison.Ordinal);
		Assert.Contains("CreateUniqueProcessingNodeId", inspector, StringComparison.Ordinal);
		Assert.Contains("MaximumProcessingNodeCount", inspector, StringComparison.Ordinal);
		Assert.Contains("_client.SetCompositingLayerProcessingStackAsync", operatorViewModel, StringComparison.Ordinal);

		Assert.DoesNotContain("OperatorControlClient", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("NamedPipe", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", inspector, StringComparison.Ordinal);
		Assert.DoesNotContain("ControlHost", inspector, StringComparison.Ordinal);
	}

	[Fact]
	public void Processing_editor_exposes_only_typed_bounded_controls()
	{
		var repo = RepositorySnapshot.Load();
		var inspector = Read(repo, "src/Hosts/rtaime.Operator/OperatorInspectorControl.xaml");

		Assert.Contains("CompositingProcessingEditorTemplate", inspector, StringComparison.Ordinal);
		Assert.Contains("AddColorGradeProcessingNodeCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("AddChromaKeyProcessingNodeCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("MoveProcessingNodeEarlierCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("MoveProcessingNodeLaterCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("ApplyColorGradeCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("ApplyChromaKeyCommand", inspector, StringComparison.Ordinal);
		Assert.Contains("SelectedChromaKeyTolerance", inspector, StringComparison.Ordinal);
		Assert.Contains("SelectedChromaKeySoftness", inspector, StringComparison.Ordinal);
		Assert.Contains("SelectedChromaKeySpillSuppression", inspector, StringComparison.Ordinal);

		Assert.DoesNotContain("Shader Editor", inspector, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Plugin Browser", inspector, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Custom Shader", inspector, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Processing_selection_and_layout_follow_stable_confirmed_identity()
	{
		var repo = RepositorySnapshot.Load();
		var graph = Read(repo, "src/Hosts/rtaime.Operator/CompositingGraphViewModel.cs");
		var inspector = Read(repo, "src/Hosts/rtaime.Operator/MediaPoolInspectorViewModel.cs");

		Assert.Contains("processing:{layerId}:{processingNode.NodeId}", graph, StringComparison.Ordinal);
		Assert.Contains("processingIndex * 290", graph, StringComparison.Ordinal);
		Assert.Contains("selectedLayerId", graph, StringComparison.Ordinal);
		Assert.Contains("SelectCompositingNode(fallback.Projection)", graph, StringComparison.Ordinal);
		Assert.Contains("_processingDraftDirty", inspector, StringComparison.Ordinal);
		Assert.Contains("_confirmedProcessingSignature", inspector, StringComparison.Ordinal);
		Assert.Contains("SynchronizeCompositingDraft(force: true)", inspector, StringComparison.Ordinal);
	}

	private static string Read(RepositorySnapshot repo, string relativePath) =>
		File.ReadAllText(Path.Combine(repo.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
