// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using Rtaime.ExternalControl.V1;

namespace rtaime.Client;

public enum ExternalControlTrustMode
{
	System = 1,
	PinnedServerCertificate = 2,
	TestOnlyInsecure = 3
}

public sealed record GrpcOperatorControlOptions
{
	public required Uri Endpoint { get; init; }
	public ExternalControlTrustMode TrustMode { get; init; } = ExternalControlTrustMode.System;
	public string? TrustedServerCertificateThumbprint { get; init; }
	public string? ClientCertificatePath { get; init; }
	public string? ClientCertificateKeyPath { get; init; }
	public string? ClientCertificatePassword { get; init; }
	public string? BearerToken { get; init; }
	public string ClientId { get; init; } = "rtaime-client";
	public string ClientName { get; init; } = "rtaime.Client";
	public string ClientVersion { get; init; } = "1.0";
	public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
	public int MaxRequestBytes { get; init; } = 1024 * 1024;
	public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
	public bool RetryNetworkUncertaintyOnce { get; init; } = true;

	public void Validate()
	{
		if (!string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("External control endpoint must use HTTPS.", nameof(Endpoint));
		if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 128)
			throw new ArgumentException("External control ClientId is required and bounded to 128 characters.", nameof(ClientId));
		if (string.IsNullOrWhiteSpace(ClientName) || ClientName.Length > 128)
			throw new ArgumentException("External control client name is required and bounded to 128 characters.", nameof(ClientName));
		if (string.IsNullOrWhiteSpace(ClientVersion) || ClientVersion.Length > 64)
			throw new ArgumentException("External control client version is required and bounded to 64 characters.", nameof(ClientVersion));
		if (RequestTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
		if (MaxRequestBytes <= 0 || MaxResponseBytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(MaxRequestBytes));
		if (TrustMode == ExternalControlTrustMode.PinnedServerCertificate && string.IsNullOrWhiteSpace(TrustedServerCertificateThumbprint))
			throw new ArgumentException("Pinned server-certificate trust requires a thumbprint.", nameof(TrustedServerCertificateThumbprint));
		if (!string.IsNullOrWhiteSpace(ClientCertificateKeyPath) && string.IsNullOrWhiteSpace(ClientCertificatePath))
			throw new ArgumentException("Client certificate key path requires a client certificate path.", nameof(ClientCertificateKeyPath));
	}
}

public sealed class ExternalControlRequestException : IOException
{
	public ExternalControlRequestException(string code, string message) : base(message) => Code = code;
	public string Code { get; }
}

public sealed record ExternalControlStateNotification(
	string HostInstanceId,
	ulong BasedOnStateVersion,
	ulong StateVersion,
	ulong Sequence,
	bool RequiresResynchronization);

public sealed class GrpcOperatorControlTransport : IOperatorControlTransport, IAsyncDisposable
{
	private const string ApiVersion = "1.0";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly object _gate = new();
	private readonly GrpcOperatorControlOptions _options;
	private readonly GrpcChannel _channel;
	private readonly ExternalControl.ExternalControlClient _client;
	private readonly RemoteStateSynchronizer _synchronizer = new();
	private readonly X509Certificate2? _clientCertificate;
	private bool _requiresFullSnapshot;

	public GrpcOperatorControlTransport(GrpcOperatorControlOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_options.Validate();
		var handler = new HttpClientHandler();
		if (_options.TrustMode == ExternalControlTrustMode.PinnedServerCertificate)
		{
			var expected = NormalizeThumbprint(_options.TrustedServerCertificateThumbprint);
			handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
			{
				if (certificate is null) return false;
				var now = DateTimeOffset.UtcNow;
				if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()) return false;
				return string.Equals(NormalizeThumbprint(certificate.GetCertHashString()), expected, StringComparison.OrdinalIgnoreCase);
			};
		}
		else if (_options.TrustMode == ExternalControlTrustMode.TestOnlyInsecure)
		{
			handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
		}

		if (!string.IsNullOrWhiteSpace(_options.ClientCertificatePath))
		{
			_clientCertificate = LoadClientCertificate(_options);
			handler.ClientCertificates.Add(_clientCertificate);
		}

		_channel = GrpcChannel.ForAddress(_options.Endpoint, new GrpcChannelOptions
		{
			HttpHandler = handler,
			MaxReceiveMessageSize = _options.MaxResponseBytes,
			MaxSendMessageSize = _options.MaxRequestBytes
		});
		_client = new ExternalControl.ExternalControlClient(_channel);
	}

	public string? HostInstanceId => _synchronizer.HostInstanceId;
	public ulong StateVersion => _synchronizer.StateVersion;
	public bool RequiresFullSnapshot { get { lock (_gate) return _requiresFullSnapshot; } }

	public async ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var request = Request(Identity.New().ToString());
		request.Snapshot = new EmptyRequest();
		var reply = await ExecuteAsync(request, snapshot: true, cancellationToken).ConfigureAwait(false);
		var snapshot = NamedPipeOperatorControlTransport.DecodeExternalSnapshot(reply.PayloadJson.Span);
		_synchronizer.AcceptFullSnapshot(reply.HostInstanceId, reply.StateVersion);
		lock (_gate) _requiresFullSnapshot = false;
		return snapshot;
	}

	public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
		ProductionMutationAsync(command.Metadata, production => production.SourceId = command.SourceId.ToString(), request => request.SelectPreview = production, cancellationToken);

	public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
		ProductionMutationAsync(command.Metadata, production => production.SourceId = command.SourceId.ToString(), request => request.CutProgram = production, cancellationToken);

	public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
		ProductionMutationAsync(command.Metadata, production =>
		{
			production.SourceId = command.SourceId.ToString();
			production.DurationFrames = command.DurationFrames;
		}, request => request.DissolveProgram = production, cancellationToken);

	public ValueTask<OperatorMutationResponse> ActivateSceneAsync(ActivateSceneCommand command, CancellationToken cancellationToken = default) =>
		ProductionMutationAsync(command.Metadata, production => production.SceneId = command.SceneId.ToString(), request => request.ActivateScene = production, cancellationToken);

	public ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(RouteOutputRoleCommand command, CancellationToken cancellationToken = default) =>
		ProductionMutationAsync(command.Metadata, production =>
		{
			production.SourceId = command.SourceId.ToString();
			production.OutputRoleId = command.RoleId.ToString();
		}, request => request.RouteOutputRole = production, cancellationToken);

	public async ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(string sourceId, double gain, bool muted, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetAudioInput = new AudioInputRequest { SourceId = sourceId, Gain = gain, Muted = muted };
		return NamedPipeOperatorControlTransport.DecodeExternalAudioInput((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorAudioProgramDescriptor> SetAudioRoutingAsync(OperatorAudioRoutingMode mode, string? breakawaySourceId, ulong expectedRoutingRevision, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetAudioRouting = new AudioRoutingRequest { Mode = (int)mode, BreakawaySourceId = breakawaySourceId ?? string.Empty, ExpectedRoutingRevision = expectedRoutingRevision };
		return NamedPipeOperatorControlTransport.DecodeExternalAudioProgram((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorAudioInputDescriptor> SetAudioTestSignalAsync(string sourceId, bool enabled, int mode, double frequencyHz, double peakLevel, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetAudioTestSignal = new AudioTestSignalRequest { SourceId = sourceId, Enabled = enabled, Mode = mode, FrequencyHz = frequencyHz, PeakLevel = peakLevel };
		return NamedPipeOperatorControlTransport.DecodeExternalAudioInput((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public ValueTask<bool> SetBroadcastTestPatternAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default) =>
		SetBroadcastTestPatternAsync(sourceId, enabled, false, cancellationToken);

	public async ValueTask<bool> SetBroadcastTestPatternAsync(string sourceId, bool enabled, bool motionTiming, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetTestPattern = new TestPatternRequest { SourceId = sourceId, Enabled = enabled, MotionTiming = motionTiming };
		return NamedPipeOperatorControlTransport.DecodeExternalTestPattern((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span, sourceId.Trim());
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(OperatorGraphicsAsset asset, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(asset);
		var request = Request();
		request.LoadGraphicsOverlay = Raw(new { asset.Name, asset.Width, asset.Height, RgbaPixels = asset.RgbaPixels });
		return NamedPipeOperatorControlTransport.DecodeExternalGraphicsOverlay((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> ApplyProductionCgTextAsync(OperatorProductionCgText definition, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definition);
		var request = Request();
		request.ApplyProductionCg = Raw(new
		{
			definition.Text,
			definition.Typeface,
			definition.FallbackTypeface,
			definition.FontSizePixels,
			Foreground = new { definition.Foreground.Red, definition.Foreground.Green, definition.Foreground.Blue, definition.Foreground.Alpha },
			definition.PositionX,
			definition.PositionY,
			definition.BoxWidth,
			definition.BoxHeight,
			Alignment = (int)definition.Alignment,
			Anchor = (int)definition.Anchor,
			Panel = new
			{
				definition.Panel.Enabled,
				Color = new { definition.Panel.Color.Red, definition.Panel.Color.Green, definition.Panel.Color.Blue, definition.Panel.Color.Alpha },
				definition.Panel.CornerRadiusPixels,
				definition.Panel.PaddingPixels
			},
			definition.Visible,
			Layer = (int)definition.Layer,
			definition.ZOrder
		});
		return NamedPipeOperatorControlTransport.DecodeExternalGraphicsOverlay((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(bool visible, double positionX, double positionY, double scale, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetGraphicsOverlay = Raw(new { Visible = visible, PositionX = positionX, PositionY = positionY, Scale = scale });
		return NamedPipeOperatorControlTransport.DecodeExternalGraphicsOverlay((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.ClearGraphicsOverlay = new EmptyRequest();
		return NamedPipeOperatorControlTransport.DecodeExternalGraphicsOverlay((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerStateAsync(string layerId, bool visible, byte opacity, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetCompositingLayer = Raw(new { LayerId = layerId, Visible = visible, Opacity = opacity });
		return NamedPipeOperatorControlTransport.DecodeExternalCompositingLayers((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerTransformAsync(
		string layerId, double positionX, double positionY, double scale, double rotationDegrees, double anchorX, double anchorY,
		double cropLeft, double cropTop, double cropRight, double cropBottom, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.TransformCompositingLayer = Raw(new
		{
			LayerId = layerId, PositionX = positionX, PositionY = positionY, Scale = scale, RotationDegrees = rotationDegrees,
			AnchorX = anchorX, AnchorY = anchorY, CropLeft = cropLeft, CropTop = cropTop, CropRight = cropRight, CropBottom = cropBottom
		});
		return NamedPipeOperatorControlTransport.DecodeExternalCompositingLayers((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerProcessingNodeAsync(string layerId, OperatorCompositingProcessingNodeDescriptor? processingNode, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetCompositingProcessing = Raw(new
		{
			LayerId = layerId,
			ProcessingNode = processingNode is null ? null : new
			{
				processingNode.NodeId,
				processingNode.Kind,
				processingNode.Enabled,
				ColorGrade = new { processingNode.ColorGrade.Brightness, processingNode.ColorGrade.Contrast, processingNode.ColorGrade.Saturation }
			}
		});
		return NamedPipeOperatorControlTransport.DecodeExternalCompositingLayers((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> ReorderCompositingLayersAsync(IReadOnlyList<string> orderedLayerIds, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.ReorderCompositingLayers = Raw(new { OrderedLayerIds = orderedLayerIds.ToArray() });
		return NamedPipeOperatorControlTransport.DecodeExternalCompositingLayers((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(string destinationDirectory, string fileName, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.StartRecording = new RecordingStartRequest { DestinationDirectory = destinationDirectory, FileName = fileName };
		return NamedPipeOperatorControlTransport.DecodeExternalRecording((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.StopRecording = new EmptyRequest();
		return NamedPipeOperatorControlTransport.DecodeExternalRecording((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<OperatorAIShowcaseDescriptor> SetAIShowcaseEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.SetAiShowcase = Raw(new { Enabled = enabled });
		return NamedPipeOperatorControlTransport.DecodeExternalAIShowcase((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default)
	{
		const int pageSize = 256;
		for (var attempt = 0; attempt < 3; attempt++)
		{
			var assets = new List<MediaAssetDescriptor>();
			ulong? revision = null;
			CompatibilityVersion? version = null;
			var offset = 0;
			var restart = false;
			while (true)
			{
				var request = Request();
				request.GetMediaAssetCatalog = new MediaAssetCatalogRequest { Offset = offset, Limit = pageSize };
				var page = NamedPipeOperatorControlTransport.DecodeExternalMediaAssetCatalogPage((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
				if (revision is not null && page.Revision != revision.Value) { restart = true; break; }
				revision ??= page.Revision;
				version ??= page.Version;
				assets.AddRange(page.Assets);
				offset += page.Assets.Count;
				if (offset >= page.TotalCount) return new MediaAssetCatalogSnapshot(version.Value, revision.Value, assets);
				if (page.Assets.Count == 0) throw new InvalidDataException("Media asset catalogue paging made no forward progress.");
			}
			if (!restart) break;
		}
		throw new InvalidOperationException("Media asset catalogue changed repeatedly while a consistent snapshot was being read.");
	}

	public async ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(IReadOnlyList<string> sourceLocations, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.ImportMediaAssets = new MediaAssetImportRequest();
		request.ImportMediaAssets.SourceLocations.Add(sourceLocations);
		var items = NamedPipeOperatorControlTransport.DecodeExternalMediaAssetMutationItems((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
		return new MediaAssetCatalogMutationResult(await GetMediaAssetCatalogAsync(cancellationToken).ConfigureAwait(false), items);
	}

	public async ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId assetId, string sourceLocation, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.RelinkMediaAsset = new MediaAssetRelinkRequest { AssetId = assetId.ToString(), SourceLocation = sourceLocation };
		var items = NamedPipeOperatorControlTransport.DecodeExternalMediaAssetMutationItems((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
		return new MediaAssetCatalogMutationResult(await GetMediaAssetCatalogAsync(cancellationToken).ConfigureAwait(false), items);
	}

	public async ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId assetId, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.RemoveMediaAsset = new MediaAssetRemoveRequest { AssetId = assetId.ToString() };
		var items = NamedPipeOperatorControlTransport.DecodeExternalMediaAssetMutationItems((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
		return new MediaAssetCatalogMutationResult(await GetMediaAssetCatalogAsync(cancellationToken).ConfigureAwait(false), items);
	}

	public async ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.RefreshMediaAssets = new EmptyRequest();
		_ = await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false);
		return await GetMediaAssetCatalogAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var request = Request(); request.GetMediaDeck = new EmptyRequest();
		return NamedPipeOperatorControlTransport.DecodeExternalMediaDeck((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(MediaDeckOpenRequest command, CancellationToken cancellationToken = default)
	{
		var request = Request();
		request.OpenMediaDeck = new Rtaime.ExternalControl.V1.MediaDeckOpenRequest { ContractVersion = command.Version.ToString(), SourceId = command.SourceId.ToString(), Path = command.Path, AssetId = command.AssetId?.ToString() ?? string.Empty };
		return NamedPipeOperatorControlTransport.DecodeExternalMediaDeck((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(MediaTransportCommand command, CancellationToken cancellationToken = default)
	{
		var operation = new MediaDeckTransportRequest { ContractVersion = command.Version.ToString(), AssetId = command.AssetId.ToString(), Kind = (int)command.Kind };
		if (command.TargetFrame is { } target) operation.TargetFrame = target;
		if (command.AutoPlayOnProgram is { } autoPlay) operation.AutoPlayOnProgram = autoPlay;
		if (command.EndBehavior is { } endBehavior) operation.EndBehavior = (int)endBehavior;
		if (command.InPointFrame is { } inPoint) operation.InPointFrame = inPoint;
		if (command.OutPointFrame is { } outPoint) operation.OutPointFrame = outPoint;
		var request = Request(); request.ApplyMediaDeckTransport = operation;
		return NamedPipeOperatorControlTransport.DecodeExternalMediaDeck((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(MediaMarkerCommand command, CancellationToken cancellationToken = default)
	{
		var operation = new MediaDeckMarkerRequest { ContractVersion = command.Version.ToString(), AssetId = command.AssetId.ToString(), Kind = (int)command.Kind, CuePointId = command.CuePointId?.ToString() ?? string.Empty, Name = command.Name ?? string.Empty };
		if (command.PositionFrame is { } position) operation.PositionFrame = position;
		var request = Request(); request.ApplyMediaDeckMarker = operation;
		return NamedPipeOperatorControlTransport.DecodeExternalMediaDeck((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default)
	{
		var request = Request(); request.CloseMediaDeck = new EmptyRequest();
		return NamedPipeOperatorControlTransport.DecodeExternalMediaDeck((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> GetShowControlSnapshotAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.GetShowControl = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> SaveShowControlCueListAsync(ShowControlCueList cueList, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.SaveShowControlCueList = new ShowControlCueListRequest { CanonicalJson = ShowControlCanonicalSerializer.Serialize(cueList) }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> SelectShowControlCueListAsync(ShowControlCueListId cueListId, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.SelectShowControlCueList = new ShowControlSelectionRequest { CueListId = cueListId.ToString() }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> ArmShowControlAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.ArmShowControl = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> GoShowControlAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.GoShowControl = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> CancelShowControlAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.CancelShowControl = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<ShowControlWorkspaceSnapshot> AcknowledgeShowControlRecoveryAsync(bool resume, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalShowControl((await ExecuteShowRequestAsync(request => request.AcknowledgeShowControlRecovery = new RecoveryAcknowledgementRequest { Resume = resume }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> GetRundownSnapshotAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.GetRundown = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> SaveRundownAsync(RundownDefinition rundown, ulong expectedStorageVersion, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.SaveRundown = new RundownSaveRequest { CanonicalJson = RundownCanonicalSerializer.Serialize(rundown), ExpectedStorageVersion = expectedStorageVersion }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> PrepareRundownItemAsync(RundownItemId itemId, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.PrepareRundown = new RundownItemRequest { ItemId = itemId.ToString() }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> GoRundownAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.GoRundown = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> NextRundownAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.NextRundown = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> PreviousRundownAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.PreviousRundown = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> HoldRundownAsync(CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.HoldRundown = new EmptyRequest(), cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async ValueTask<RundownWorkspaceSnapshot> AcknowledgeRundownRecoveryAsync(bool resume, CancellationToken cancellationToken = default) =>
		NamedPipeOperatorControlTransport.DecodeExternalRundown((await ExecuteRundownRequestAsync(request => request.AcknowledgeRundownRecovery = new RecoveryAcknowledgementRequest { Resume = resume }, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);

	public async IAsyncEnumerable<ExternalControlStateNotification> WatchStateAsync(
		TimeSpan? minimumInterval = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var interval = minimumInterval ?? TimeSpan.FromMilliseconds(250);
		var request = new StateSubscriptionRequest
		{
			Context = Context(Identity.New().ToString()),
			MinimumIntervalMs = checked((uint)Math.Clamp((long)interval.TotalMilliseconds, 100, 5000))
		};
		using var call = _client.SubscribeState(request, Headers(), cancellationToken: cancellationToken);
		await foreach (var notification in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
		{
			var currentHost = _synchronizer.HostInstanceId;
			var currentVersion = _synchronizer.StateVersion;
			var requires = currentHost is not null &&
				(!string.Equals(currentHost, notification.HostInstanceId, StringComparison.Ordinal) ||
				 (notification.BasedOnStateVersion != 0 && notification.BasedOnStateVersion != currentVersion));
			if (requires)
			{
				lock (_gate) _requiresFullSnapshot = true;
				_synchronizer.Reset();
			}
			yield return new ExternalControlStateNotification(
				notification.HostInstanceId,
				notification.BasedOnStateVersion,
				notification.StateVersion,
				notification.Sequence,
				requires);
		}
	}

	private async ValueTask<OperatorMutationResponse> ProductionMutationAsync(
		ControlCommandMetadata metadata,
		Action<ProductionCommand> populate,
		Action<ExternalControlRequest> assign,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(metadata);
		EnsureSnapshotNotRequired();
		var production = new ProductionCommand
		{
			ContractVersion = metadata.Version.ToString(),
			CommandId = metadata.CommandId.ToString(),
			ProductionId = metadata.ProductionId.ToString(),
			ExpectedRevision = metadata.ExpectedRevision.Value
		};
		populate(production);
		var request = Request(metadata.CommandId.ToString());
		assign(request);
		return NamedPipeOperatorControlTransport.DecodeExternalMutation((await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false)).PayloadJson.Span);
	}

	private async ValueTask<ExternalControlReply> ExecuteShowRequestAsync(Action<ExternalControlRequest> configure, CancellationToken cancellationToken)
	{
		var request = Request(); configure(request);
		return await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<ExternalControlReply> ExecuteRundownRequestAsync(Action<ExternalControlRequest> configure, CancellationToken cancellationToken)
	{
		var request = Request(); configure(request);
		return await ExecuteAsync(request, false, cancellationToken).ConfigureAwait(false);
	}

	private ExternalControlRequest Request(string? requestId = null) =>
		new() { Context = Context(requestId ?? Identity.New().ToString()) };

	private RequestContext Context(string requestId) => new()
	{
		ApiVersion = ApiVersion,
		RequestId = requestId,
		CorrelationId = Identity.New().ToString(),
		ClientName = _options.ClientName,
		ClientVersion = _options.ClientVersion
	};

	private Metadata Headers()
	{
		var headers = new Metadata
		{
			{ "x-rtaime-client-id", _options.ClientId },
			{ "x-rtaime-client-name", _options.ClientName },
			{ "x-rtaime-client-version", _options.ClientVersion }
		};
		if (!string.IsNullOrEmpty(_options.BearerToken))
			headers.Add("authorization", "Bearer " + _options.BearerToken);
		return headers;
	}

	private async ValueTask<ExternalControlReply> ExecuteAsync(ExternalControlRequest request, bool snapshot, CancellationToken cancellationToken)
	{
		if (!snapshot) EnsureSnapshotNotRequired();
		RpcException? last = null;
		var attempts = _options.RetryNetworkUncertaintyOnce ? 2 : 1;
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			try
			{
				var deadline = DateTime.UtcNow + _options.RequestTimeout;
				var reply = await _client.ExecuteAsync(request, Headers(), deadline, cancellationToken).ResponseAsync.ConfigureAwait(false);
				ValidateReply(request, reply, snapshot);
				return reply;
			}
			catch (RpcException exception) when (attempt + 1 < attempts && exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
			{
				last = exception;
			}
		}
		throw last ?? new IOException("External control request failed without a response.");
	}

	private void ValidateReply(ExternalControlRequest request, ExternalControlReply reply, bool snapshot)
	{
		if (!string.Equals(reply.ApiVersion, ApiVersion, StringComparison.Ordinal))
			throw new InvalidDataException("External control response API version is incompatible.");
		if (!string.Equals(reply.RequestId, request.Context.RequestId, StringComparison.Ordinal) ||
			!string.Equals(reply.CorrelationId, request.Context.CorrelationId, StringComparison.Ordinal))
			throw new InvalidDataException("External control response correlation is invalid.");
		if (reply.Error is not null && !string.IsNullOrWhiteSpace(reply.Error.Code))
			throw new ExternalControlRequestException(reply.Error.Code, reply.Error.Message);
		if (string.IsNullOrWhiteSpace(reply.HostInstanceId) || reply.StateVersion == 0)
			throw new InvalidDataException("External control response state identity is invalid.");

		var previousHost = _synchronizer.HostInstanceId;
		if (previousHost is not null && !string.Equals(previousHost, reply.HostInstanceId, StringComparison.Ordinal))
		{
			_synchronizer.Reset();
			lock (_gate) _requiresFullSnapshot = true;
			if (!snapshot) throw new RemoteHostSessionChangedException(previousHost, reply.HostInstanceId);
		}
	}

	private void EnsureSnapshotNotRequired()
	{
		if (RequiresFullSnapshot)
			throw new InvalidOperationException("External ControlHost state requires a full snapshot before another mutation.");
	}

	private static JsonPayloadRequest Raw(object payload) =>
		new() { Json = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonOptions)) };

	private static X509Certificate2 LoadClientCertificate(GrpcOperatorControlOptions options)
	{
		var path = Path.GetFullPath(options.ClientCertificatePath!);
		if (!File.Exists(path)) throw new FileNotFoundException("External control client certificate was not found.", path);
		if (!string.IsNullOrWhiteSpace(options.ClientCertificateKeyPath))
		{
			var keyPath = Path.GetFullPath(options.ClientCertificateKeyPath);
			if (!File.Exists(keyPath)) throw new FileNotFoundException("External control client certificate key was not found.", keyPath);
			return X509Certificate2.CreateFromPemFile(path, keyPath);
		}
		return X509CertificateLoader.LoadPkcs12FromFile(path, options.ClientCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
	}

	private static string NormalizeThumbprint(string? thumbprint) =>
		(thumbprint ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

	public ValueTask DisposeAsync()
	{
		_channel.Dispose();
		_clientCertificate?.Dispose();
		return ValueTask.CompletedTask;
	}
}
