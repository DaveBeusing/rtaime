// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class ShowProjectPersistenceIntegrationTests
{
	[Fact]
	public async Task Show_project_migrates_legacy_show_control_and_preserves_stable_authored_state()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-show-project-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var databasePath = Path.Combine(root, "management.db");
		var specification = CreateSpecification();
		var cueListId = ShowControlCueListId.New();
		var cueId = ShowControlCueId.New();
		var actionId = ShowControlActionId.New();
		var sceneId = specification.Scenes[0].SceneId;
		var cueList = new ShowControlCueList(
			ShowControlContractVersion.Current,
			cueListId,
			"Main",
			[
				new ShowControlCue(
					cueId,
					"Take scene",
					[new ShowControlAction(actionId, ShowControlActionKind.ActivateScene, sceneId: sceneId.ToString())])
			]);

		try
		{
			Identity projectId;
			DurableBitmapGraphicsReference bitmap;
			await using (var management = new SqliteManagementStore(databasePath))
			{
				await management.InitializeAsync();
				var legacy = new ShowControlPersistenceStore(management);
				var legacyWrite = await legacy.SaveAsync(
					specification.ProductionId,
					[cueList],
					cueListId,
					ShowControlExecutionSnapshot.Idle,
					expectedStorageVersion: 0);
				Assert.True(legacyWrite.Written, legacyWrite.Failure?.Message);

				var store = new ShowProjectPersistenceStore(management);
				var project = await store.LoadOrCreateAsync(specification);
				projectId = project.ProjectId;
				Assert.Equal(legacyWrite.Persisted!.StorageVersion, project.ShowControlStorageVersion);
				Assert.NotNull(project.ShowControlWorkspaceJson);

				var renamedScenes = project.Scenes
					.Select((scene, index) => index == 0
						? new ProductionSceneSpecification(scene.SceneId, "Renamed scene", scene.Routing, scene.CompositingState)
						: scene)
					.ToArray();
				project = await store.UpdateScenesAsync(specification, renamedScenes);
				Assert.Equal(sceneId, project.Scenes[0].SceneId);

				var rgba = new byte[] { 12, 34, 56, 255 };
				bitmap = await store.StoreBitmapAssetAsync(
					"durable.rgba",
					1,
					1,
					rgba,
					visible: true,
					positionX: 0.1,
					positionY: 0.2,
					scale: 1.5);

				var cg = new RuntimeProductionCgTextDefinition(
					"Durable",
					"Segoe UI",
					"Arial",
					36,
					new RuntimeCgColor(255, 255, 255, 255),
					0.1,
					0.8,
					640,
					120,
					1,
					1,
					new RuntimeCgPanel(true, new RuntimeCgColor(0, 0, 0, 160), 8, 16),
					true,
					1,
					0);
				var compositing = new ProductionCompositingState(
					ProductionCompositingState.CurrentVersion,
					[
						new ProductionCompositingLayerState(
							ProductionCompositingLayerIds.BitmapGraphics,
							ProductionCompositingLayerKind.BitmapGraphics,
							0,
							true,
							180,
							0.1,
							0.2,
							1.5,
							"durable.rgba",
							rotationDegrees: 17.5,
							anchorX: 0.5,
							anchorY: 0.25,
							cropLeft: 0.05,
							cropTop: 0.10,
							cropRight: 0.15,
							cropBottom: 0.20,
							processingNode: new ProductionCompositingProcessingNodeState(
								"grade-primary",
								ProductionCompositingProcessingNodeKind.ColorGrade,
								true,
								new ProductionColorGradeSettings(0.1, 1.2, 0.8))),
						new ProductionCompositingLayerState(
							ProductionCompositingLayerIds.ProductionCg,
							ProductionCompositingLayerKind.ProductionCg,
							1,
							true,
							220,
							0.1,
							0.8,
							1,
							"Durable")
					]);
				project = await store.UpdateGraphicsAsync(
					specification,
					new DurableGraphicsState(bitmap, cg, compositing));
				var breakawaySource = new MediaSourceId(specification.Sources[1].SourceId.Value);
				project = await store.UpdateAudioRoutingAsync(
					specification,
					new DurableAudioRoutingState(DurableAudioRoutingState.BreakawayMode, breakawaySource));

				var migratedShowControl = await store.LoadShowControlAsync(specification);
				var conflict = await store.UpdateShowControlAsync(
					specification,
					"{\"format\":\"invalid-overwrite\"}",
					expectedVersion: migratedShowControl.Version + 1);
				Assert.False(conflict.Written);
				Assert.Equal("persistence.version_conflict", conflict.Failure?.Code);

				var unchanged = await store.LoadShowControlAsync(specification);
				Assert.Equal(migratedShowControl.Version, unchanged.Version);
				Assert.Equal(migratedShowControl.Json, unchanged.Json);
			}

			await using (var reopenedManagement = new SqliteManagementStore(databasePath))
			{
				await reopenedManagement.InitializeAsync();
				var reopenedStore = new ShowProjectPersistenceStore(reopenedManagement);
				var reopened = await reopenedStore.LoadAsync(specification);
				Assert.Equal(projectId, reopened.ProjectId);
				Assert.Equal(sceneId, reopened.Scenes[0].SceneId);
				Assert.Equal("Renamed scene", reopened.Scenes[0].Name);
				Assert.Equal(bitmap.AssetId, reopened.Graphics.Bitmap?.AssetId);
				Assert.Equal("Durable", reopened.Graphics.ProductionCgText?.Text);
				Assert.Equal(2, reopened.Graphics.CompositingState?.Layers.Count);
				var reopenedBitmapLayer = Assert.Single(
					reopened.Graphics.CompositingState!.Layers,
					layer => layer.LayerId == ProductionCompositingLayerIds.BitmapGraphics);
				Assert.Equal(17.5, reopenedBitmapLayer.RotationDegrees, 6);
				Assert.Equal(0.5, reopenedBitmapLayer.AnchorX, 6);
				Assert.Equal(0.25, reopenedBitmapLayer.AnchorY, 6);
				Assert.Equal(0.05, reopenedBitmapLayer.CropLeft, 6);
				Assert.Equal(0.10, reopenedBitmapLayer.CropTop, 6);
				Assert.Equal(0.15, reopenedBitmapLayer.CropRight, 6);
				Assert.Equal(0.20, reopenedBitmapLayer.CropBottom, 6);
				Assert.NotNull(reopenedBitmapLayer.ProcessingNode);
				Assert.Equal("grade-primary", reopenedBitmapLayer.ProcessingNode!.NodeId);
				Assert.Equal(0.1, reopenedBitmapLayer.ProcessingNode.ColorGrade.Brightness, 6);
				Assert.Equal(1.2, reopenedBitmapLayer.ProcessingNode.ColorGrade.Contrast, 6);
				Assert.Equal(0.8, reopenedBitmapLayer.ProcessingNode.ColorGrade.Saturation, 6);
				Assert.NotNull(reopened.AudioRouting);
				Assert.Equal(DurableAudioRoutingState.BreakawayMode, reopened.AudioRouting!.Mode);
				Assert.Equal(new MediaSourceId(specification.Sources[1].SourceId.Value), reopened.AudioRouting.BreakawaySourceId);

				var bytes = await reopenedStore.LoadBitmapAssetAsync(Assert.IsType<DurableBitmapGraphicsReference>(reopened.Graphics.Bitmap));
				Assert.Equal(new byte[] { 12, 34, 56, 255 }, bytes);

				await reopenedStore.DeleteBitmapAssetAsync(reopened.Graphics.Bitmap);
				await Assert.ThrowsAsync<InvalidDataException>(async () =>
					await reopenedStore.LoadBitmapAssetAsync(Assert.IsType<DurableBitmapGraphicsReference>(reopened.Graphics.Bitmap)));
			}
		}
		finally
		{
			try
			{
				if (Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task Unsupported_show_project_format_fails_closed()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-show-project-format-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var specification = CreateSpecification();
			await using var management = new SqliteManagementStore(Path.Combine(root, "management.db"));
			await management.InitializeAsync();
			var write = await management.PutDocumentAsync(
				"show.project",
				specification.ProductionId.ToString(),
				"{\"format\":\"rtaime.show-project.v99\",\"projectId\":\"00000000-0000-0000-0000-000000000001\",\"productionId\":\"" + specification.ProductionId + "\",\"name\":\"Future\",\"scenes\":[],\"graphics\":null,\"showControlWorkspaceJson\":null,\"showControlStorageVersion\":0}",
				expectedVersion: 0);
			Assert.True(write.Written, write.Failure?.Message);

			var store = new ShowProjectPersistenceStore(management);
			var exception = await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(specification));
			Assert.Contains("Unsupported show project format", exception.Message, StringComparison.Ordinal);
		}
		finally
		{
			try
			{
				if (Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	private static ProductionSpecification CreateSpecification()
	{
		var sourceA = new ProductionSourceId(Identity.Parse("6a000000-0000-0000-0000-00000000000a"));
		var sourceB = new ProductionSourceId(Identity.Parse("6a000000-0000-0000-0000-00000000000b"));
		var sceneA = new ProductionSceneSpecification(
			new SceneId(Identity.Parse("6a000000-0000-0000-0000-000000000101")),
			"Scene A",
			new ProductionRoutingState(sourceA, sourceA));
		var sceneB = new ProductionSceneSpecification(
			new SceneId(Identity.Parse("6a000000-0000-0000-0000-000000000102")),
			"Scene B",
			new ProductionRoutingState(sourceA, sourceB));
		return new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Identity.Parse("6a000000-0000-0000-0000-000000000001")),
			"Durable show project",
			[
				new ProductionSourceSpecification(sourceA, "Input A"),
				new ProductionSourceSpecification(sourceB, "Input B")
			],
			new ProductionRoutingState(sourceA, sourceA),
			[sceneA, sceneB]);
	}
}
