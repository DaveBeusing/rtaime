// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.ControlHost;

public sealed record DurableBitmapGraphicsReference(
	Identity AssetId,
	string Name,
	uint Width,
	uint Height,
	string AssetFileName,
	string ChecksumSha256,
	bool Visible,
	double PositionX,
	double PositionY,
	double Scale);

public sealed record DurableGraphicsState(
	DurableBitmapGraphicsReference? Bitmap,
	RuntimeProductionCgTextDefinition? ProductionCgText,
	ProductionCompositingState? CompositingState)
{
	public static DurableGraphicsState Empty { get; } = new(null, null, null);
}

public sealed record PersistedShowProject(
	Identity ProjectId,
	ProductionId ProductionId,
	string Name,
	IReadOnlyList<ProductionSceneSpecification> Scenes,
	DurableGraphicsState Graphics,
	string? ShowControlWorkspaceJson,
	ulong ShowControlStorageVersion,
	ulong StorageVersion)
{
	public ProductionSpecification ApplyTo(ProductionSpecification baseline)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		if (baseline.ProductionId != ProductionId)
			throw new InvalidOperationException("Durable show project belongs to a different production.");

		return new ProductionSpecification(
			baseline.Version,
			baseline.ProductionId,
			baseline.Name,
			baseline.Sources,
			baseline.InitialRouting,
			Scenes,
			baseline.InitialOutputRoles);
	}
}

public sealed record ShowProjectSubdocumentSnapshot(string? Json, ulong Version);

public sealed record ShowProjectSubdocumentWriteResult(
	bool Written,
	ShowProjectSubdocumentSnapshot? Snapshot,
	Failure? Failure);

/// <summary>
/// Canonical ControlHost-owned durable authoring store for a production show/project.
/// The JSON document contains bounded authored state only; bitmap payload bytes are retained
/// in a checksummed sidecar so management persistence never carries bulk RGBA data.
/// </summary>
public sealed class ShowProjectPersistenceStore
{
	private const string Area = "show.project";
	private const string DocumentFormat = "rtaime.show-project.v1";
	private const string LegacyShowControlArea = "show-control.workspace";
	private const int MaximumScenes = 128;
	private const int MaximumProjectNameLength = 200;
	private const int MaximumShowControlJsonBytes = 4 * 1024 * 1024;
	private const uint MaximumBitmapDimension = 384;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SqliteManagementStore _managementStore;
	private readonly string _assetDirectory;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public ShowProjectPersistenceStore(SqliteManagementStore managementStore)
	{
		_managementStore = managementStore ?? throw new ArgumentNullException(nameof(managementStore));
		var root = Path.GetDirectoryName(_managementStore.Path)
			?? throw new InvalidOperationException("Management persistence path has no parent directory.");
		_assetDirectory = Path.Combine(root, "show-assets");
	}

	public async ValueTask<PersistedShowProject> LoadOrCreateAsync(
		ProductionSpecification baseline,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var document = await _managementStore
				.GetDocumentAsync(Area, baseline.ProductionId.ToString(), cancellationToken)
				.ConfigureAwait(false);
			if (document is not null)
				return Deserialize(document, baseline);

			var legacyShowControl = await _managementStore
				.GetDocumentAsync(LegacyShowControlArea, baseline.ProductionId.ToString(), cancellationToken)
				.ConfigureAwait(false);
			var initial = new PersistedShowProject(
				Identity.New(),
				baseline.ProductionId,
				baseline.Name,
				baseline.Scenes.ToArray(),
				DurableGraphicsState.Empty,
				legacyShowControl?.Json,
				legacyShowControl?.Version ?? 0,
				0);
			var json = Serialize(initial);
			var write = await _managementStore
				.PutDocumentAsync(
					Area,
					baseline.ProductionId.ToString(),
					json,
					expectedVersion: 0,
					cancellationToken)
				.ConfigureAwait(false);
			if (!write.Written || write.Document is null)
				throw new InvalidOperationException(write.Failure?.Message ?? "Durable show project could not be created.");

			return initial with { StorageVersion = write.Document.Version };
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<PersistedShowProject> LoadAsync(
		ProductionSpecification baseline,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var document = await _managementStore
				.GetDocumentAsync(Area, baseline.ProductionId.ToString(), cancellationToken)
				.ConfigureAwait(false)
				?? throw new InvalidDataException("Durable show project does not exist.");
			return Deserialize(document, baseline);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<PersistedShowProject> UpdateScenesAsync(
		ProductionSpecification baseline,
		IReadOnlyList<ProductionSceneSpecification> scenes,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(scenes);
		ValidateScenes(baseline, scenes);

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var current = await RequireDocumentAsync(baseline.ProductionId, cancellationToken).ConfigureAwait(false);
			var project = Deserialize(current, baseline);
			var updated = project with { Scenes = scenes.ToArray() };
			return await WriteAsync(updated, current.Version, baseline, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<PersistedShowProject> UpdateGraphicsAsync(
		ProductionSpecification baseline,
		DurableGraphicsState graphics,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(graphics);
		ValidateGraphics(graphics);

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var current = await RequireDocumentAsync(baseline.ProductionId, cancellationToken).ConfigureAwait(false);
			var project = Deserialize(current, baseline);
			var updated = project with { Graphics = graphics };
			return await WriteAsync(updated, current.Version, baseline, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ShowProjectSubdocumentSnapshot> LoadShowControlAsync(
		ProductionSpecification baseline,
		CancellationToken cancellationToken = default)
	{
		var project = await LoadAsync(baseline, cancellationToken).ConfigureAwait(false);
		return new ShowProjectSubdocumentSnapshot(project.ShowControlWorkspaceJson, project.ShowControlStorageVersion);
	}

	public async ValueTask<ShowProjectSubdocumentWriteResult> UpdateShowControlAsync(
		ProductionSpecification baseline,
		string json,
		ulong expectedVersion,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		if (string.IsNullOrWhiteSpace(json))
			throw new ArgumentException("Show-control workspace JSON is required.", nameof(json));
		if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumShowControlJsonBytes)
			throw new ArgumentOutOfRangeException(nameof(json), $"Show-control workspace JSON must not exceed {MaximumShowControlJsonBytes} bytes.");

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var current = await RequireDocumentAsync(baseline.ProductionId, cancellationToken).ConfigureAwait(false);
			var project = Deserialize(current, baseline);
			if (project.ShowControlStorageVersion != expectedVersion)
			{
				return new ShowProjectSubdocumentWriteResult(
					false,
					null,
					new Failure(
						"persistence.version_conflict",
						$"Expected show-control storage version {expectedVersion}, current version is {project.ShowControlStorageVersion}."));
			}

			var nextSubdocumentVersion = checked(project.ShowControlStorageVersion + 1);
			var updated = project with
			{
				ShowControlWorkspaceJson = json,
				ShowControlStorageVersion = nextSubdocumentVersion
			};
			var persisted = await WriteAsync(updated, current.Version, baseline, cancellationToken).ConfigureAwait(false);
			return new ShowProjectSubdocumentWriteResult(
				true,
				new ShowProjectSubdocumentSnapshot(persisted.ShowControlWorkspaceJson, persisted.ShowControlStorageVersion),
				null);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<DurableBitmapGraphicsReference> StoreBitmapAssetAsync(
		string name,
		uint width,
		uint height,
		byte[] rgbaPixels,
		bool visible,
		double positionX,
		double positionY,
		double scale,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Graphics asset name is required.", nameof(name));
		if (width is 0 or > MaximumBitmapDimension || height is 0 or > MaximumBitmapDimension)
			throw new ArgumentOutOfRangeException(nameof(width), $"Graphics assets must be between 1x1 and {MaximumBitmapDimension}x{MaximumBitmapDimension} pixels.");
		ArgumentNullException.ThrowIfNull(rgbaPixels);
		var expectedBytes = checked((int)((ulong)width * height * 4UL));
		if (rgbaPixels.Length != expectedBytes)
			throw new ArgumentException($"Graphics RGBA payload requires exactly {expectedBytes} bytes.", nameof(rgbaPixels));
		ValidatePlacement(positionX, positionY, scale);

		Directory.CreateDirectory(_assetDirectory);
		var assetId = Identity.New();
		var fileName = $"{assetId}.rgba";
		var targetPath = Path.Combine(_assetDirectory, fileName);
		var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporaryPath, rgbaPixels, cancellationToken).ConfigureAwait(false);
			File.Move(temporaryPath, targetPath, overwrite: false);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				try { File.Delete(temporaryPath); } catch (IOException) { }
			}
		}

		return new DurableBitmapGraphicsReference(
			assetId,
			name.Trim(),
			width,
			height,
			fileName,
			ComputeChecksum(rgbaPixels),
			visible,
			positionX,
			positionY,
			scale);
	}

	public async ValueTask<byte[]> LoadBitmapAssetAsync(
		DurableBitmapGraphicsReference asset,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(asset);
		ValidateBitmapReference(asset);
		var path = ResolveAssetPath(asset.AssetFileName);
		byte[] bytes;
		try
		{
			bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		}
		catch (FileNotFoundException exception)
		{
			throw new InvalidDataException($"Durable graphics asset '{asset.AssetId}' is missing.", exception);
		}

		var expectedBytes = checked((int)((ulong)asset.Width * asset.Height * 4UL));
		if (bytes.Length != expectedBytes)
			throw new InvalidDataException($"Durable graphics asset '{asset.AssetId}' has an invalid byte length.");
		if (!string.Equals(ComputeChecksum(bytes), asset.ChecksumSha256, StringComparison.Ordinal))
			throw new InvalidDataException($"Durable graphics asset '{asset.AssetId}' checksum mismatch.");
		return bytes;
	}

	public ValueTask DeleteBitmapAssetAsync(
		DurableBitmapGraphicsReference? asset,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (asset is null)
			return ValueTask.CompletedTask;
		ValidateBitmapReference(asset);
		var path = ResolveAssetPath(asset.AssetFileName);
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
			// Orphan cleanup is best-effort; the durable project document no longer references this payload.
		}
		return ValueTask.CompletedTask;
	}

	private async ValueTask<ManagementDocument> RequireDocumentAsync(
		ProductionId productionId,
		CancellationToken cancellationToken) =>
		await _managementStore
			.GetDocumentAsync(Area, productionId.ToString(), cancellationToken)
			.ConfigureAwait(false)
			?? throw new InvalidDataException("Durable show project does not exist.");

	private async ValueTask<PersistedShowProject> WriteAsync(
		PersistedShowProject project,
		ulong expectedStorageVersion,
		ProductionSpecification baseline,
		CancellationToken cancellationToken)
	{
		ValidateProject(project, baseline);
		var json = Serialize(project);
		var write = await _managementStore
			.PutDocumentAsync(
				Area,
				project.ProductionId.ToString(),
				json,
				expectedStorageVersion,
				cancellationToken)
			.ConfigureAwait(false);
		if (!write.Written || write.Document is null)
			throw new InvalidOperationException(write.Failure?.Message ?? "Durable show project could not be persisted.");
		return project with { StorageVersion = write.Document.Version };
	}

	private static string Serialize(PersistedShowProject project)
	{
		var document = new ProjectDocument(
			DocumentFormat,
			project.ProjectId.ToString(),
			project.ProductionId.ToString(),
			project.Name,
			project.Scenes.Select(ToDocument).ToArray(),
			ToDocument(project.Graphics),
			project.ShowControlWorkspaceJson,
			project.ShowControlStorageVersion);
		return JsonSerializer.Serialize(document, JsonOptions);
	}

	private static PersistedShowProject Deserialize(
		ManagementDocument persisted,
		ProductionSpecification baseline)
	{
		var document = JsonSerializer.Deserialize<ProjectDocument>(persisted.Json, JsonOptions)
			?? throw new InvalidDataException("Persisted show project is empty.");
		if (!string.Equals(document.Format, DocumentFormat, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported show project format '{document.Format}'.");
		if (!string.Equals(document.ProductionId, baseline.ProductionId.ToString(), StringComparison.Ordinal))
			throw new InvalidDataException("Persisted show project belongs to a different production.");

		var scenes = document.Scenes.Select(FromDocument).ToArray();
		var graphics = FromDocument(document.Graphics);
		var project = new PersistedShowProject(
			Identity.Parse(document.ProjectId),
			baseline.ProductionId,
			document.Name,
			scenes,
			graphics,
			document.ShowControlWorkspaceJson,
			document.ShowControlStorageVersion,
			persisted.Version);
		ValidateProject(project, baseline);
		return project;
	}

	private static void ValidateProject(PersistedShowProject project, ProductionSpecification baseline)
	{
		if (project.ProjectId.IsEmpty)
			throw new InvalidDataException("Durable show project identity is empty.");
		if (project.ProductionId != baseline.ProductionId)
			throw new InvalidDataException("Durable show project production identity does not match the active production.");
		if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > MaximumProjectNameLength)
			throw new InvalidDataException($"Durable show project name must contain 1-{MaximumProjectNameLength} characters.");
		ValidateScenes(baseline, project.Scenes);
		ValidateGraphics(project.Graphics);
		if (project.ShowControlWorkspaceJson is null && project.ShowControlStorageVersion != 0)
			throw new InvalidDataException("Durable show project has a show-control version without a show-control payload.");
		if (project.ShowControlWorkspaceJson is { } json &&
			System.Text.Encoding.UTF8.GetByteCount(json) > MaximumShowControlJsonBytes)
		{
			throw new InvalidDataException($"Persisted show-control workspace exceeds {MaximumShowControlJsonBytes} bytes.");
		}
	}

	private static void ValidateScenes(
		ProductionSpecification baseline,
		IReadOnlyList<ProductionSceneSpecification> scenes)
	{
		if (scenes.Count is 0 or > MaximumScenes)
			throw new InvalidDataException($"Durable show project must contain between 1 and {MaximumScenes} Scenes.");
		if (scenes.Any(scene => scene is null))
			throw new InvalidDataException("Durable show project contains a null Scene.");
		if (scenes.Select(scene => scene.SceneId).Distinct().Count() != scenes.Count)
			throw new InvalidDataException("Durable show project contains duplicate Scene identities.");

		var sourceIds = baseline.Sources.Select(source => source.SourceId).ToHashSet();
		foreach (var scene in scenes)
		{
			if (!sourceIds.Contains(scene.Routing.PreviewSourceId) || !sourceIds.Contains(scene.Routing.ProgramSourceId))
				throw new InvalidDataException($"Scene '{scene.SceneId}' references a source that is not present in the active production specification.");
		}
	}

	private static void ValidateGraphics(DurableGraphicsState graphics)
	{
		if (graphics.Bitmap is { } bitmap)
			ValidateBitmapReference(bitmap);
		if (graphics.CompositingState is { } compositing &&
			compositing.Version != ProductionCompositingState.CurrentVersion)
		{
			throw new InvalidDataException($"Unsupported durable graphics compositing version '{compositing.Version}'.");
		}
	}

	private static void ValidateBitmapReference(DurableBitmapGraphicsReference asset)
	{
		if (asset.AssetId.IsEmpty)
			throw new InvalidDataException("Durable graphics asset identity is empty.");
		if (string.IsNullOrWhiteSpace(asset.Name))
			throw new InvalidDataException("Durable graphics asset name is required.");
		if (asset.Width is 0 or > MaximumBitmapDimension || asset.Height is 0 or > MaximumBitmapDimension)
			throw new InvalidDataException("Durable graphics asset dimensions are invalid.");
		if (string.IsNullOrWhiteSpace(asset.AssetFileName) ||
			!string.Equals(Path.GetFileName(asset.AssetFileName), asset.AssetFileName, StringComparison.Ordinal))
		{
			throw new InvalidDataException("Durable graphics asset file reference is invalid.");
		}
		if (asset.ChecksumSha256.Length != 64 ||
			asset.ChecksumSha256.Any(character => !Uri.IsHexDigit(character)))
		{
			throw new InvalidDataException("Durable graphics asset checksum is invalid.");
		}
		ValidatePlacement(asset.PositionX, asset.PositionY, asset.Scale);
	}

	private static void ValidatePlacement(double positionX, double positionY, double scale)
	{
		if (!double.IsFinite(positionX) || positionX is < 0 or > 1)
			throw new InvalidDataException("Durable graphics X position must be between 0 and 1.");
		if (!double.IsFinite(positionY) || positionY is < 0 or > 1)
			throw new InvalidDataException("Durable graphics Y position must be between 0 and 1.");
		if (!double.IsFinite(scale) || scale is < 0.05 or > 4.0)
			throw new InvalidDataException("Durable graphics scale must be between 0.05 and 4.0.");
	}

	private string ResolveAssetPath(string fileName)
	{
		if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
			throw new InvalidDataException("Durable graphics asset file reference is invalid.");
		return Path.Combine(_assetDirectory, fileName);
	}

	private static SceneDocument ToDocument(ProductionSceneSpecification scene) => new(
		scene.SceneId.ToString(),
		scene.Name,
		scene.Routing.PreviewSourceId.ToString(),
		scene.Routing.ProgramSourceId.ToString(),
		scene.CompositingState is null ? null : ToDocument(scene.CompositingState));

	private static ProductionSceneSpecification FromDocument(SceneDocument scene) => new(
		new SceneId(Identity.Parse(scene.SceneId)),
		scene.Name,
		new ProductionRoutingState(
			new ProductionSourceId(Identity.Parse(scene.PreviewSourceId)),
			new ProductionSourceId(Identity.Parse(scene.ProgramSourceId))),
		scene.CompositingState is null ? null : FromDocument(scene.CompositingState));

	private static CompositingDocument ToDocument(ProductionCompositingState state) => new(
		state.Version.ToString(),
		state.Layers.Select(layer => new LayerDocument(
			layer.LayerId,
			(int)layer.Kind,
			layer.Order,
			layer.Visible,
			layer.Opacity,
			layer.PositionX,
			layer.PositionY,
			layer.Scale,
			layer.ContentIdentity,
			layer.RotationDegrees,
			layer.AnchorX,
			layer.AnchorY,
			layer.CropLeft,
			layer.CropTop,
			layer.CropRight,
			layer.CropBottom,
			layer.ProcessingNode is null
				? null
				: new ProcessingNodeDocument(
					layer.ProcessingNode.NodeId,
					(int)layer.ProcessingNode.Kind,
					layer.ProcessingNode.Enabled,
					new ColorGradeDocument(
						layer.ProcessingNode.ColorGrade.Brightness,
						layer.ProcessingNode.ColorGrade.Contrast,
						layer.ProcessingNode.ColorGrade.Saturation)))).ToArray());

	private static ProductionCompositingState FromDocument(CompositingDocument state)
	{
		var version = CompatibilityVersion.Parse(state.Version);
		if (version != ProductionCompositingState.CurrentVersion)
			throw new InvalidDataException($"Unsupported persisted compositing state version '{version}'.");
		return new ProductionCompositingState(
			version,
			state.Layers.Select(layer => new ProductionCompositingLayerState(
				layer.LayerId,
				Enum.IsDefined(typeof(ProductionCompositingLayerKind), layer.Kind)
					? (ProductionCompositingLayerKind)layer.Kind
					: throw new InvalidDataException($"Persisted compositing layer kind '{layer.Kind}' is invalid."),
				layer.Order,
				layer.Visible,
				layer.Opacity,
				layer.PositionX,
				layer.PositionY,
				layer.Scale,
				layer.ContentIdentity,
				layer.RotationDegrees,
				layer.AnchorX,
				layer.AnchorY,
				layer.CropLeft,
				layer.CropTop,
				layer.CropRight,
				layer.CropBottom,
				layer.ProcessingNode is null
					? null
					: new ProductionCompositingProcessingNodeState(
						layer.ProcessingNode.NodeId,
						Enum.IsDefined(typeof(ProductionCompositingProcessingNodeKind), layer.ProcessingNode.Kind)
							? (ProductionCompositingProcessingNodeKind)layer.ProcessingNode.Kind
							: throw new InvalidDataException($"Persisted processing node kind '{layer.ProcessingNode.Kind}' is invalid."),
						layer.ProcessingNode.Enabled,
						new ProductionColorGradeSettings(
							layer.ProcessingNode.ColorGrade.Brightness,
							layer.ProcessingNode.ColorGrade.Contrast,
							layer.ProcessingNode.ColorGrade.Saturation)))).ToArray());
	}

	private static GraphicsDocument ToDocument(DurableGraphicsState graphics) => new(
		graphics.Bitmap is null ? null : new BitmapDocument(
			graphics.Bitmap.AssetId.ToString(),
			graphics.Bitmap.Name,
			graphics.Bitmap.Width,
			graphics.Bitmap.Height,
			graphics.Bitmap.AssetFileName,
			graphics.Bitmap.ChecksumSha256,
			graphics.Bitmap.Visible,
			graphics.Bitmap.PositionX,
			graphics.Bitmap.PositionY,
			graphics.Bitmap.Scale),
		graphics.ProductionCgText is null ? null : ToDocument(graphics.ProductionCgText),
		graphics.CompositingState is null ? null : ToDocument(graphics.CompositingState));

	private static DurableGraphicsState FromDocument(GraphicsDocument? graphics)
	{
		if (graphics is null)
			return DurableGraphicsState.Empty;
		return new DurableGraphicsState(
			graphics.Bitmap is null ? null : new DurableBitmapGraphicsReference(
				Identity.Parse(graphics.Bitmap.AssetId),
				graphics.Bitmap.Name,
				graphics.Bitmap.Width,
				graphics.Bitmap.Height,
				graphics.Bitmap.AssetFileName,
				graphics.Bitmap.ChecksumSha256,
				graphics.Bitmap.Visible,
				graphics.Bitmap.PositionX,
				graphics.Bitmap.PositionY,
				graphics.Bitmap.Scale),
			graphics.ProductionCgText is null ? null : FromDocument(graphics.ProductionCgText),
			graphics.CompositingState is null ? null : FromDocument(graphics.CompositingState));
	}

	private static CgDocument ToDocument(RuntimeProductionCgTextDefinition definition) => new(
		definition.Text,
		definition.Typeface,
		definition.FallbackTypeface,
		definition.FontSizePixels,
		new ColorDocument(definition.Foreground.Red, definition.Foreground.Green, definition.Foreground.Blue, definition.Foreground.Alpha),
		definition.PositionX,
		definition.PositionY,
		definition.BoxWidth,
		definition.BoxHeight,
		definition.Alignment,
		definition.Anchor,
		new PanelDocument(
			definition.Panel.Enabled,
			new ColorDocument(definition.Panel.Color.Red, definition.Panel.Color.Green, definition.Panel.Color.Blue, definition.Panel.Color.Alpha),
			definition.Panel.CornerRadiusPixels,
			definition.Panel.PaddingPixels),
		definition.Visible,
		definition.Layer,
		definition.ZOrder);

	private static RuntimeProductionCgTextDefinition FromDocument(CgDocument definition) => new(
		definition.Text,
		definition.Typeface,
		definition.FallbackTypeface,
		definition.FontSizePixels,
		new RuntimeCgColor(definition.Foreground.Red, definition.Foreground.Green, definition.Foreground.Blue, definition.Foreground.Alpha),
		definition.PositionX,
		definition.PositionY,
		definition.BoxWidth,
		definition.BoxHeight,
		definition.Alignment,
		definition.Anchor,
		new RuntimeCgPanel(
			definition.Panel.Enabled,
			new RuntimeCgColor(definition.Panel.Color.Red, definition.Panel.Color.Green, definition.Panel.Color.Blue, definition.Panel.Color.Alpha),
			definition.Panel.CornerRadiusPixels,
			definition.Panel.PaddingPixels),
		definition.Visible,
		definition.Layer,
		definition.ZOrder);

	private static string ComputeChecksum(byte[] bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes));

	private sealed record ProjectDocument(
		string Format,
		string ProjectId,
		string ProductionId,
		string Name,
		SceneDocument[] Scenes,
		GraphicsDocument? Graphics,
		string? ShowControlWorkspaceJson,
		ulong ShowControlStorageVersion);

	private sealed record SceneDocument(
		string SceneId,
		string Name,
		string PreviewSourceId,
		string ProgramSourceId,
		CompositingDocument? CompositingState);

	private sealed record GraphicsDocument(
		BitmapDocument? Bitmap,
		CgDocument? ProductionCgText,
		CompositingDocument? CompositingState);

	private sealed record BitmapDocument(
		string AssetId,
		string Name,
		uint Width,
		uint Height,
		string AssetFileName,
		string ChecksumSha256,
		bool Visible,
		double PositionX,
		double PositionY,
		double Scale);

	private sealed record CgDocument(
		string Text,
		string Typeface,
		string? FallbackTypeface,
		float FontSizePixels,
		ColorDocument Foreground,
		double PositionX,
		double PositionY,
		uint BoxWidth,
		uint BoxHeight,
		int Alignment,
		int Anchor,
		PanelDocument Panel,
		bool Visible,
		int Layer,
		int ZOrder);

	private sealed record ColorDocument(byte Red, byte Green, byte Blue, byte Alpha);
	private sealed record PanelDocument(bool Enabled, ColorDocument Color, float CornerRadiusPixels, uint PaddingPixels);

	private sealed record CompositingDocument(string Version, LayerDocument[] Layers);

	private sealed record LayerDocument(
		string LayerId,
		int Kind,
		int Order,
		bool Visible,
		byte Opacity,
		double PositionX,
		double PositionY,
		double Scale,
		string ContentIdentity,
		double RotationDegrees = 0,
		double AnchorX = 0,
		double AnchorY = 0,
		double CropLeft = 0,
		double CropTop = 0,
		double CropRight = 0,
		double CropBottom = 0,
		ProcessingNodeDocument? ProcessingNode = null);

	private sealed record ProcessingNodeDocument(
		string NodeId,
		int Kind,
		bool Enabled,
		ColorGradeDocument ColorGrade);

	private sealed record ColorGradeDocument(
		double Brightness,
		double Contrast,
		double Saturation);
}
