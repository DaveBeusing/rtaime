// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class GovernedSceneOperatorTests
{
	[Fact]
	public async Task Selecting_scene_is_presentation_only_and_does_not_send_control_mutation()
	{
		var transport = new CountingOperatorTransport();
		var readiness = new PassiveReadinessService();
		await using var viewModel = new OperatorViewModel(
			new OperatorControlClient(transport),
			runtimeReadiness: readiness);
		var sourceId = ProductionSourceId.New().ToString();
		var source = new OperatorSourceTileViewModel(new OperatorSourceDescriptor(sourceId, "Camera A"));
		var scene = new OperatorSceneViewModel(
			new OperatorSceneDescriptor(SceneId.New().ToString(), "Camera A full frame", sourceId, sourceId),
			source,
			1);

		viewModel.SelectedScene = scene;

		Assert.Same(scene, viewModel.SelectedScene);
		Assert.Equal(0, transport.SceneActivationCalls);
		Assert.Equal(0, transport.RoutingMutationCalls);
		Assert.Equal(0, transport.SnapshotCalls);
	}

	[Fact]
	public void Scene_projection_exposes_declared_desired_layer_state_without_claiming_live_evidence()
	{
		var sourceId = ProductionSourceId.New().ToString();
		var source = new OperatorSourceTileViewModel(new OperatorSourceDescriptor(sourceId, "Camera A"));
		var descriptor = new OperatorSceneDescriptor(
			SceneId.New().ToString(),
			"Camera A with logo",
			sourceId,
			sourceId,
			new[]
			{
				new OperatorCompositingLayerDescriptor(
					"bitmap-graphics",
					2,
					0,
					true,
					192,
					0.1,
					0.2,
					1.25,
					"logo.rgba")
			});
		var scene = new OperatorSceneViewModel(descriptor, source, 1);

		scene.ApplyEvidence(sourceId, activeSceneId: null);

		Assert.True(scene.HasDesiredCompositingState);
		Assert.Equal(1, scene.DesiredLayerCount);
		Assert.Equal("1 LAYER", scene.DesiredStateSummary);
		Assert.False(scene.IsActive);
		Assert.Equal("PREVIEW", scene.Evidence);
	}

	[Fact]
	public void Scene_live_evidence_requires_explicit_authoritative_active_scene_identity()
	{
		var sourceId = ProductionSourceId.New().ToString();
		var source = new OperatorSourceTileViewModel(new OperatorSourceDescriptor(sourceId, "Camera A"));
		var descriptor = new OperatorSceneDescriptor(
			SceneId.New().ToString(),
			"Camera A full frame",
			sourceId,
			sourceId);
		var scene = new OperatorSceneViewModel(descriptor, source, 1);

		scene.ApplyEvidence(sourceId, activeSceneId: null);

		Assert.True(scene.IsPreview);
		Assert.False(scene.IsActive);
		Assert.Equal("PREVIEW", scene.Evidence);

		scene.ApplyEvidence(sourceId, descriptor.Id);

		Assert.True(scene.IsActive);
		Assert.Equal("ACTIVE", scene.Evidence);
	}

	private sealed class CountingOperatorTransport : IOperatorControlTransport
	{
		public int SnapshotCalls { get; private set; }
		public int RoutingMutationCalls { get; private set; }
		public int SceneActivationCalls { get; private set; }

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			SnapshotCalls++;
			return ValueTask.FromException<OperatorStatusSnapshot>(
				new InvalidOperationException("Snapshot should not be requested by local Scene selection."));
		}

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default)
		{
			RoutingMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(
				new InvalidOperationException("Routing mutation should not be requested by local Scene selection."));
		}

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			RoutingMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(
				new InvalidOperationException("Routing mutation should not be requested by local Scene selection."));
		}

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			RoutingMutationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(
				new InvalidOperationException("Routing mutation should not be requested by local Scene selection."));
		}

		public ValueTask<OperatorMutationResponse> ActivateSceneAsync(
			ActivateSceneCommand command,
			CancellationToken cancellationToken = default)
		{
			SceneActivationCalls++;
			return ValueTask.FromException<OperatorMutationResponse>(
				new InvalidOperationException("Scene activation should not be requested by local Scene selection."));
		}
	}

	private sealed class PassiveReadinessService : IRuntimeReadinessService
	{
		public RuntimeReadinessSnapshot Current { get; } =
			RuntimeReadinessSnapshot.Initial(DateTimeOffset.UnixEpoch);

		public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed
		{
			add { }
			remove { }
		}

		public void Observe(RuntimeReadinessObservation observation)
		{
		}

		public void InvalidatePerformance(string detail)
		{
		}
	}
}
