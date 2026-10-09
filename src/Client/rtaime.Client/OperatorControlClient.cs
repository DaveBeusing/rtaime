using System.Collections.ObjectModel;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public sealed record OperatorSourceDescriptor
{
    public OperatorSourceDescriptor(
        string id,
        string name,
        string type = "LIVE",
        string format = "UNKNOWN",
        string health = "UNKNOWN",
        string mediaState = "—",
        TimeSpan? remaining = null,
        string? mediaFileName = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Source id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Source name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Source type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("Source format is required.", nameof(format));
        if (string.IsNullOrWhiteSpace(health)) throw new ArgumentException("Source health is required.", nameof(health));
        if (string.IsNullOrWhiteSpace(mediaState)) throw new ArgumentException("Media state is required.", nameof(mediaState));
        if (remaining < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(remaining));

        Id = id.Trim();
        Name = name.Trim();
        Type = type.Trim().ToUpperInvariant();
        Format = format.Trim();
        Health = health.Trim().ToUpperInvariant();
        MediaState = mediaState.Trim().ToUpperInvariant();
        Remaining = remaining;
        MediaFileName = string.IsNullOrWhiteSpace(mediaFileName) ? null : mediaFileName.Trim();
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Format { get; }
    public string Health { get; }
    public string MediaState { get; }
    public TimeSpan? Remaining { get; }
    public string? MediaFileName { get; }
}

public sealed record OperatorSceneDescriptor
{
    private readonly ReadOnlyCollection<OperatorCompositingLayerDescriptor> _compositingLayers;

    public OperatorSceneDescriptor(
        string id,
        string name,
        string previewSourceId,
        string programSourceId,
        IReadOnlyList<OperatorCompositingLayerDescriptor>? compositingLayers = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Scene id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Scene name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(previewSourceId)) throw new ArgumentException("Scene Preview source id is required.", nameof(previewSourceId));
        if (string.IsNullOrWhiteSpace(programSourceId)) throw new ArgumentException("Scene Program source id is required.", nameof(programSourceId));
        if (compositingLayers is { Count: > 8 }) throw new ArgumentException("Scene compositing state supports at most eight layers.", nameof(compositingLayers));

        Id = id.Trim();
        Name = name.Trim();
        PreviewSourceId = previewSourceId.Trim();
        ProgramSourceId = programSourceId.Trim();
        _compositingLayers = Array.AsReadOnly((compositingLayers ?? Array.Empty<OperatorCompositingLayerDescriptor>())
            .OrderBy(layer => layer.Order)
            .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
            .ToArray());
    }

    public string Id { get; }
    public string Name { get; }
    public string PreviewSourceId { get; }
    public string ProgramSourceId { get; }
    public IReadOnlyList<OperatorCompositingLayerDescriptor> CompositingLayers => _compositingLayers;
}

public sealed record OperatorNetworkOutputDescriptor
{
    public OperatorNetworkOutputDescriptor(
        string targetId,
        string provider,
        string protocol,
        string safeTargetIdentity,
        string lifecycle,
        bool connected,
        string videoCodec,
        string audioCodec,
        uint audioSampleRate,
        uint audioChannelCount,
        string audioSampleFormat,
        uint videoBitRate,
        uint audioBitRate,
        int latencyMilliseconds,
        ulong acceptedSamples,
        ulong sentSamples,
        ulong droppedSamples,
        ulong rejectedSamples,
        ulong reconnectCount,
        ulong packetsSent,
        ulong bytesSent,
        int queueDepth,
        DateTimeOffset? lastSuccessfulSendUtc,
        Failure? failure)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("Network output target id is required.", nameof(targetId));
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Network output provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(protocol)) throw new ArgumentException("Network output protocol is required.", nameof(protocol));
        if (string.IsNullOrWhiteSpace(safeTargetIdentity)) throw new ArgumentException("Network output target identity is required.", nameof(safeTargetIdentity));
        if (string.IsNullOrWhiteSpace(lifecycle)) throw new ArgumentException("Network output lifecycle is required.", nameof(lifecycle));
        if (string.IsNullOrWhiteSpace(videoCodec)) throw new ArgumentException("Network output video codec is required.", nameof(videoCodec));
        if (string.IsNullOrWhiteSpace(audioCodec)) throw new ArgumentException("Network output audio codec is required.", nameof(audioCodec));
        if (audioSampleRate == 0 || audioChannelCount == 0) throw new ArgumentOutOfRangeException(nameof(audioSampleRate));
        if (string.IsNullOrWhiteSpace(audioSampleFormat)) throw new ArgumentException("Network output audio sample format is required.", nameof(audioSampleFormat));
        if (videoBitRate == 0 || audioBitRate == 0) throw new ArgumentOutOfRangeException(nameof(videoBitRate));
        if (latencyMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        if (queueDepth < 0) throw new ArgumentOutOfRangeException(nameof(queueDepth));

        TargetId = targetId.Trim();
        Provider = provider.Trim();
        Protocol = protocol.Trim().ToUpperInvariant();
        SafeTargetIdentity = safeTargetIdentity.Trim();
        Lifecycle = lifecycle.Trim().ToUpperInvariant();
        Connected = connected;
        VideoCodec = videoCodec.Trim().ToUpperInvariant();
        AudioCodec = audioCodec.Trim().ToUpperInvariant();
        AudioSampleRate = audioSampleRate;
        AudioChannelCount = audioChannelCount;
        AudioSampleFormat = audioSampleFormat.Trim().ToUpperInvariant();
        VideoBitRate = videoBitRate;
        AudioBitRate = audioBitRate;
        LatencyMilliseconds = latencyMilliseconds;
        AcceptedSamples = acceptedSamples;
        SentSamples = sentSamples;
        DroppedSamples = droppedSamples;
        RejectedSamples = rejectedSamples;
        ReconnectCount = reconnectCount;
        PacketsSent = packetsSent;
        BytesSent = bytesSent;
        QueueDepth = queueDepth;
        LastSuccessfulSendUtc = lastSuccessfulSendUtc;
        Failure = failure;
    }

    public string TargetId { get; }
    public string Provider { get; }
    public string Protocol { get; }
    public string SafeTargetIdentity { get; }
    public string Lifecycle { get; }
    public bool Connected { get; }
    public string VideoCodec { get; }
    public string AudioCodec { get; }
    public uint AudioSampleRate { get; }
    public uint AudioChannelCount { get; }
    public string AudioSampleFormat { get; }
    public uint VideoBitRate { get; }
    public uint AudioBitRate { get; }
    public int LatencyMilliseconds { get; }
    public ulong AcceptedSamples { get; }
    public ulong SentSamples { get; }
    public ulong DroppedSamples { get; }
    public ulong RejectedSamples { get; }
    public ulong ReconnectCount { get; }
    public ulong PacketsSent { get; }
    public ulong BytesSent { get; }
    public int QueueDepth { get; }
    public DateTimeOffset? LastSuccessfulSendUtc { get; }
    public Failure? Failure { get; }
}

public sealed record OperatorOutputRoleDescriptor
{
    public OperatorOutputRoleDescriptor(
        string roleId,
        string roleKind,
        string sourceId,
        string targetId,
        string providerId,
        uint? width,
        uint? height,
        string? frameRate,
        string? pixelFormat,
        string? timing,
        string lifecycleState,
        bool authoritativeActive,
        string healthState,
        string evidence,
        Failure? error,
        OperatorNetworkOutputDescriptor? networkOutput = null,
        string audioBusId = "program")
    {
        if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Output role id is required.", nameof(roleId));
        if (string.IsNullOrWhiteSpace(roleKind)) throw new ArgumentException("Output role kind is required.", nameof(roleKind));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Output role source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("Output role target id is required.", nameof(targetId));
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Output role provider id is required.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(lifecycleState)) throw new ArgumentException("Output role lifecycle state is required.", nameof(lifecycleState));
        if (healthState is not ("PASS" or "FAIL" or "UNVERIFIED")) throw new ArgumentException("Output role health must be PASS, FAIL or UNVERIFIED.", nameof(healthState));
        if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException("Output role evidence is required.", nameof(evidence));
        if (healthState == "FAIL" && error is null) throw new ArgumentException("Failed output roles require an error reason.", nameof(error));
        if (string.IsNullOrWhiteSpace(audioBusId)) throw new ArgumentException("Output role audio bus id is required.", nameof(audioBusId));
        RoleId = roleId.Trim().ToLowerInvariant();
        RoleKind = roleKind.Trim().ToUpperInvariant();
        SourceId = sourceId.Trim();
        TargetId = targetId.Trim();
        ProviderId = providerId.Trim();
        Width = width;
        Height = height;
        FrameRate = string.IsNullOrWhiteSpace(frameRate) ? null : frameRate.Trim();
        PixelFormat = string.IsNullOrWhiteSpace(pixelFormat) ? null : pixelFormat.Trim().ToUpperInvariant();
        Timing = string.IsNullOrWhiteSpace(timing) ? null : timing.Trim();
        LifecycleState = lifecycleState.Trim().ToUpperInvariant();
        AuthoritativeActive = authoritativeActive;
        HealthState = healthState;
        Evidence = evidence.Trim();
        Error = error;
        NetworkOutput = networkOutput;
        AudioBusId = audioBusId.Trim().ToLowerInvariant();
    }
    public string RoleId { get; }
    public string RoleKind { get; }
    public string SourceId { get; }
    public string TargetId { get; }
    public string ProviderId { get; }
    public uint? Width { get; }
    public uint? Height { get; }
    public string? FrameRate { get; }
    public string? PixelFormat { get; }
    public string? Timing { get; }
    public string LifecycleState { get; }
    public bool AuthoritativeActive { get; }
    public string HealthState { get; }
    public string Evidence { get; }
    public Failure? Error { get; }
    public OperatorNetworkOutputDescriptor? NetworkOutput { get; }
    public string AudioBusId { get; }
}

public sealed record OperatorGraphicsAsset
{
    public OperatorGraphicsAsset(string name, uint width, uint height, byte[] rgbaPixels)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Graphics asset name is required.", nameof(name));
        if (width == 0 || height == 0 || width > 384 || height > 384)
            throw new ArgumentOutOfRangeException(nameof(width), "Graphics assets must be between 1x1 and 384x384 pixels.");
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        var expected = checked((int)((ulong)width * height * 4UL));
        if (rgbaPixels.Length != expected)
            throw new ArgumentException($"Graphics RGBA payload requires exactly '{expected}' bytes.", nameof(rgbaPixels));

        Name = name.Trim();
        Width = width;
        Height = height;
        RgbaPixels = rgbaPixels.ToArray();
    }

    public string Name { get; }
    public uint Width { get; }
    public uint Height { get; }
    public byte[] RgbaPixels { get; }
}

public sealed record OperatorGraphicsOverlayDescriptor(
    bool AssetLoaded,
    string? AssetName,
    uint AssetWidth,
    uint AssetHeight,
    bool Visible,
    double PositionX,
    double PositionY,
    double Scale)
{
    public static OperatorGraphicsOverlayDescriptor Empty { get; } =
        new(false, null, 0, 0, false, 0.72, 0.06, 1.0);
}

public sealed record OperatorColorGradeDescriptor
{
    public OperatorColorGradeDescriptor(double brightness, double contrast, double saturation)
    {
        if (!double.IsFinite(brightness) || brightness is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(brightness));
        if (!double.IsFinite(contrast) || contrast is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(contrast));
        if (!double.IsFinite(saturation) || saturation is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(saturation));
        Brightness = brightness;
        Contrast = contrast;
        Saturation = saturation;
    }

    public double Brightness { get; }
    public double Contrast { get; }
    public double Saturation { get; }
}

public sealed record OperatorChromaKeyDescriptor
{
    public OperatorChromaKeyDescriptor(byte keyRed, byte keyGreen, byte keyBlue, double tolerance, double softness, double spillSuppression)
    {
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!double.IsFinite(softness) || softness is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(softness));
        if (!double.IsFinite(spillSuppression) || spillSuppression is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(spillSuppression));
        KeyRed = keyRed;
        KeyGreen = keyGreen;
        KeyBlue = keyBlue;
        Tolerance = tolerance;
        Softness = softness;
        SpillSuppression = spillSuppression;
    }

    public byte KeyRed { get; }
    public byte KeyGreen { get; }
    public byte KeyBlue { get; }
    public double Tolerance { get; }
    public double Softness { get; }
    public double SpillSuppression { get; }
}

public sealed record OperatorCompositingProcessingNodeDescriptor
{
    public OperatorCompositingProcessingNodeDescriptor(
        string nodeId,
        int kind,
        bool enabled,
        OperatorColorGradeDescriptor? colorGrade = null,
        OperatorChromaKeyDescriptor? chromaKey = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 64)
            throw new ArgumentException("Processing node identity is required and must not exceed 64 characters.", nameof(nodeId));
        if (kind is not 1 and not 2) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == 1 && (colorGrade is null || chromaKey is not null))
            throw new ArgumentException("Color Grade nodes require only Color Grade settings.");
        if (kind == 2 && (chromaKey is null || colorGrade is not null))
            throw new ArgumentException("Chroma Key nodes require only Chroma Key settings.");
        NodeId = nodeId.Trim();
        Kind = kind;
        Enabled = enabled;
        ColorGrade = colorGrade;
        ChromaKey = chromaKey;
    }

    public string NodeId { get; }
    public int Kind { get; }
    public bool Enabled { get; }
    public OperatorColorGradeDescriptor? ColorGrade { get; }
    public OperatorChromaKeyDescriptor? ChromaKey { get; }
}

public sealed record OperatorCompositingLayerDescriptor
{
    public const int MaximumProcessingNodeCount = 4;
    private readonly ReadOnlyCollection<OperatorCompositingProcessingNodeDescriptor> _processingStack;

    public OperatorCompositingLayerDescriptor(
        string layerId,
        int kind,
        int order,
        bool visible,
        byte opacity,
        double positionX,
        double positionY,
        double scale,
        string contentIdentity,
        double rotationDegrees = 0,
        double anchorX = 0,
        double anchorY = 0,
        double cropLeft = 0,
        double cropTop = 0,
        double cropRight = 0,
        double cropBottom = 0,
        OperatorCompositingProcessingNodeDescriptor? processingNode = null,
        IReadOnlyList<OperatorCompositingProcessingNodeDescriptor>? processingStack = null)
    {
        if (string.IsNullOrWhiteSpace(layerId)) throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
        if (kind is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(kind));
        if (order is < 0 or >= 8) throw new ArgumentOutOfRangeException(nameof(order));
        if (!double.IsFinite(positionX) || positionX is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(positionX));
        if (!double.IsFinite(positionY) || positionY is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(positionY));
        if (!double.IsFinite(scale) || scale is < 0.05 or > 4.0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!double.IsFinite(rotationDegrees) || rotationDegrees is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        if (!double.IsFinite(anchorX) || anchorX is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(anchorX));
        if (!double.IsFinite(anchorY) || anchorY is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(anchorY));
        if (!double.IsFinite(cropLeft) || cropLeft is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cropLeft));
        if (!double.IsFinite(cropTop) || cropTop is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cropTop));
        if (!double.IsFinite(cropRight) || cropRight is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cropRight));
        if (!double.IsFinite(cropBottom) || cropBottom is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cropBottom));
        if (cropLeft + cropRight >= 1) throw new ArgumentOutOfRangeException(nameof(cropRight));
        if (cropTop + cropBottom >= 1) throw new ArgumentOutOfRangeException(nameof(cropBottom));
        if (string.IsNullOrWhiteSpace(contentIdentity)) throw new ArgumentException("Compositing layer content identity is required.", nameof(contentIdentity));
        if (processingNode is not null && processingStack is not null &&
            (processingStack.Count == 0 || !Equals(processingNode, processingStack[0])))
        {
            throw new ArgumentException("Legacy processing-node projection must match the first canonical processing-stack node.", nameof(processingStack));
        }

        var canonical = processingStack is null
            ? processingNode is null ? Array.Empty<OperatorCompositingProcessingNodeDescriptor>() : new[] { processingNode }
            : processingStack.ToArray();
        if (canonical.Length > MaximumProcessingNodeCount)
            throw new ArgumentException($"Compositing processing stack supports at most {MaximumProcessingNodeCount} nodes.", nameof(processingStack));
        if (canonical.Any(node => node is null))
            throw new ArgumentException("Compositing processing stack must not contain null nodes.", nameof(processingStack));
        if (canonical.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != canonical.Length)
            throw new ArgumentException("Compositing processing node identities must be unique within a layer.", nameof(processingStack));

        LayerId = layerId.Trim();
        Kind = kind;
        Order = order;
        Visible = visible;
        Opacity = opacity;
        PositionX = positionX;
        PositionY = positionY;
        Scale = scale;
        ContentIdentity = contentIdentity.Trim();
        RotationDegrees = rotationDegrees;
        AnchorX = anchorX;
        AnchorY = anchorY;
        CropLeft = cropLeft;
        CropTop = cropTop;
        CropRight = cropRight;
        CropBottom = cropBottom;
        _processingStack = Array.AsReadOnly(canonical);
    }

    public string LayerId { get; }
    public int Kind { get; }
    public int Order { get; }
    public bool Visible { get; }
    public byte Opacity { get; }
    public double PositionX { get; }
    public double PositionY { get; }
    public double Scale { get; }
    public string ContentIdentity { get; }
    public double RotationDegrees { get; }
    public double AnchorX { get; }
    public double AnchorY { get; }
    public double CropLeft { get; }
    public double CropTop { get; }
    public double CropRight { get; }
    public double CropBottom { get; }
    public IReadOnlyList<OperatorCompositingProcessingNodeDescriptor> ProcessingStack => _processingStack;
    public OperatorCompositingProcessingNodeDescriptor? ProcessingNode => _processingStack.Count == 0 ? null : _processingStack[0];
}

public sealed record OperatorAudioInputDescriptor
{
    public OperatorAudioInputDescriptor(
        string sourceId,
        string streamId,
        double gain,
        bool muted,
        double leftPeak,
        double rightPeak,
        double masterPeak,
        bool clipping,
        string health,
        bool testSignalEnabled = false,
        int? testSignalMode = null,
        string? testSignalActiveChannel = null,
        double? testSignalFrequencyHz = null,
        double? testSignalPeakLevel = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Audio source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(streamId)) throw new ArgumentException("Audio stream id is required.", nameof(streamId));
        if (!double.IsFinite(gain) || gain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(gain));
        ValidatePeak(leftPeak, nameof(leftPeak));
        ValidatePeak(rightPeak, nameof(rightPeak));
        ValidatePeak(masterPeak, nameof(masterPeak));
        if (string.IsNullOrWhiteSpace(health)) throw new ArgumentException("Audio health is required.", nameof(health));

        SourceId = sourceId.Trim();
        StreamId = streamId.Trim();
        Gain = gain;
        Muted = muted;
        LeftPeak = leftPeak;
        RightPeak = rightPeak;
        MasterPeak = masterPeak;
        Clipping = clipping;
        Health = health.Trim().ToUpperInvariant();
        TestSignalEnabled = testSignalEnabled;
        TestSignalMode = testSignalMode;
        TestSignalActiveChannel = string.IsNullOrWhiteSpace(testSignalActiveChannel) ? null : testSignalActiveChannel.Trim().ToUpperInvariant();
        TestSignalFrequencyHz = testSignalFrequencyHz;
        TestSignalPeakLevel = testSignalPeakLevel;
    }

    public string SourceId { get; }
    public string StreamId { get; }
    public double Gain { get; }
    public bool Muted { get; }
    public double LeftPeak { get; }
    public double RightPeak { get; }
    public double MasterPeak { get; }
    public bool Clipping { get; }
    public string Health { get; }
    public bool TestSignalEnabled { get; }
    public int? TestSignalMode { get; }
    public string? TestSignalActiveChannel { get; }
    public double? TestSignalFrequencyHz { get; }
    public double? TestSignalPeakLevel { get; }

    private static void ValidatePeak(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "Audio peak must be finite and in the inclusive range 0..1.");
    }
}

public enum OperatorAudioRoutingMode
{
    FollowVideo = 1,
    Breakaway = 2
}

public sealed record OperatorAudioProgramDescriptor(
    string ActiveVideoSourceId,
    string ActiveStreamId,
    double Gain,
    bool Muted,
    double LeftPeak,
    double RightPeak,
    double MasterPeak,
    bool Clipping,
    string Health,
    OperatorAudioRoutingMode RoutingMode = OperatorAudioRoutingMode.FollowVideo,
    ulong RoutingRevision = 0,
    string? ActiveAudioSourceId = null)
{
    public bool IsBreakaway => RoutingMode == OperatorAudioRoutingMode.Breakaway;
    public static OperatorAudioProgramDescriptor Unknown { get; } =
        new("—", "—", 1, false, 0, 0, 0, false, "UNKNOWN");
}

public sealed record OperatorAudioProductionBusDescriptor(
    string BusId,
    double MasterGain,
    bool Muted,
    double LeftPeak,
    double RightPeak,
    double PreClipPeak,
    bool Clipping,
    ulong ClippedSampleValues,
    int ActiveSourceCount,
    int MissingSourceCount,
    double PreDynamicsPeak = 0,
    double CompressorGainReductionDb = 0,
    double LimiterGainReductionDb = 0,
    ulong LimiterHitCount = 0,
    AudioBusDynamicsConfiguration? Dynamics = null);

public sealed record OperatorAudioProductionDescriptor(
    AudioProductionConfiguration Configuration,
    double LeftPeak,
    double RightPeak,
    double PreClipPeak,
    bool Clipping,
    ulong ClippedSampleValues,
    double DuckingGain,
    double DuckingReduction,
    bool SidechainAvailable,
    double? CrossfadeProgress,
    int ActiveSourceCount,
    int MissingSourceCount,
    IReadOnlyList<OperatorAudioProductionBusDescriptor>? Buses = null,
    double PreDynamicsPeak = 0,
    double CompressorGainReductionDb = 0,
    double LimiterGainReductionDb = 0,
    ulong LimiterHitCount = 0)
{
    public static OperatorAudioProductionDescriptor? Unavailable => null;
}


public sealed record OperatorRecordingProfileDescriptor(
    string ProfileId,
    string DisplayName,
    string Container,
    string FileExtension,
    string VideoCodec,
    string VideoProfile,
    string? VideoLevel,
    string AudioCodec,
    IReadOnlyList<VideoFormat> SupportedInputVideoFormats,
    AudioFormat RequiredInputAudioFormat,
    uint VideoBitRate,
    uint AudioBitRate,
    string AccelerationClass,
    string ProviderId,
    bool Available,
    string? UnavailableReason,
    string EvidenceState,
    string Evidence)
{
    public string Label => $"{DisplayName} · {AccelerationClass}";
    public string CodecSummary => $"{VideoCodec} {VideoProfile} / {AudioCodec}";
}

public sealed record OperatorRecordingDescriptor
{
    public OperatorRecordingDescriptor(
        string state,
        TimeSpan elapsed,
        string? destination,
        string? fileName,
        string? finalPath,
        ulong accepted,
        ulong written,
        ulong dropped,
        ulong rejected,
        ulong writerFailures,
        Failure? failure,
        string? activeProfileId = null,
        string? activeProviderId = null,
        string? defaultProfileId = null,
        IReadOnlyList<OperatorRecordingProfileDescriptor>? profiles = null)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("Recording state is required.", nameof(state));
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));

        State = state.Trim().ToUpperInvariant();
        Elapsed = elapsed;
        Destination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        FinalPath = string.IsNullOrWhiteSpace(finalPath) ? null : finalPath.Trim();
        Accepted = accepted;
        Written = written;
        Dropped = dropped;
        Rejected = rejected;
        WriterFailures = writerFailures;
        Failure = failure;
        ActiveProfileId = string.IsNullOrWhiteSpace(activeProfileId) ? null : activeProfileId.Trim();
        ActiveProviderId = string.IsNullOrWhiteSpace(activeProviderId) ? null : activeProviderId.Trim();
        DefaultProfileId = string.IsNullOrWhiteSpace(defaultProfileId) ? null : defaultProfileId.Trim();
        Profiles = Array.AsReadOnly((profiles ?? Array.Empty<OperatorRecordingProfileDescriptor>()).ToArray());
    }

    public string State { get; }
    public TimeSpan Elapsed { get; }
    public string? Destination { get; }
    public string? FileName { get; }
    public string? FinalPath { get; }
    public ulong Accepted { get; }
    public ulong Written { get; }
    public ulong Dropped { get; }
    public ulong Rejected { get; }
    public ulong WriterFailures { get; }
    public Failure? Failure { get; }
    public string? ActiveProfileId { get; }
    public string? ActiveProviderId { get; }
    public string? DefaultProfileId { get; }
    public IReadOnlyList<OperatorRecordingProfileDescriptor> Profiles { get; }

    public static OperatorRecordingDescriptor Unavailable { get; } =
        new("UNAVAILABLE", TimeSpan.Zero, null, null, null, 0, 0, 0, 0, 0, null);
}

public sealed record OperatorRecordingCommandResult(
    bool Succeeded,
    OperatorRecordingDescriptor Snapshot,
    Failure? Failure);

public static class OperatorHealthStates
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Unverified = "UNVERIFIED";
}

public sealed record OperatorHealthMetricDescriptor
{
    public OperatorHealthMetricDescriptor(string state, string detail)
    {
        if (state is not (OperatorHealthStates.Pass or OperatorHealthStates.Fail or OperatorHealthStates.Unverified))
            throw new ArgumentException("Health state must be PASS, FAIL or UNVERIFIED.", nameof(state));
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("Health detail is required.", nameof(detail));
        State = state;
        Detail = detail.Trim();
    }

    public string State { get; }
    public string Detail { get; }

    public static OperatorHealthMetricDescriptor Unverified(string detail) =>
        new(OperatorHealthStates.Unverified, detail);
}

public sealed record OperatorHealthDescriptor(
    OperatorHealthMetricDescriptor Engine,
    OperatorHealthMetricDescriptor Control,
    OperatorHealthMetricDescriptor Runtime,
    OperatorHealthMetricDescriptor Media,
    OperatorHealthMetricDescriptor Provider,
    OperatorHealthMetricDescriptor GpuProvider,
    string CurrentFormat,
    TimeSpan FrameTime,
    TimeSpan FrameBudget,
    ulong DroppedFrames,
    TimeSpan Uptime,
    string GpuUtilization,
    string Vram,
    DateTimeOffset ObservedAtUtc,
    string CpuDeviceName = "UNVERIFIED",
    string CpuUtilization = "UNVERIFIED",
    string SystemMemory = "UNVERIFIED",
    string GpuDeviceName = "UNVERIFIED",
    double? OutputFramesPerSecond = null,
    string AvSyncState = "UNAVAILABLE",
    string AvSyncEvent = "UNAVAILABLE",
    string AvSyncScheduledOffset = "UNAVAILABLE",
    string AvSyncSubmitOffset = "UNAVAILABLE",
    string AvSyncDrift = "UNAVAILABLE",
    string AvSyncDetail = "A/V sync diagnostics are unavailable.")
{
    public static OperatorHealthDescriptor Unavailable { get; } = new(
        OperatorHealthMetricDescriptor.Unverified("Health snapshot unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Control health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Runtime health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Media health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("Provider health unavailable."),
        OperatorHealthMetricDescriptor.Unverified("GPU provider health unavailable."),
        "UNVERIFIED",
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        TimeSpan.Zero,
        "UNVERIFIED",
        "UNVERIFIED",
        DateTimeOffset.MinValue,
        "UNVERIFIED",
        "UNVERIFIED",
        "UNVERIFIED",
        "UNVERIFIED",
        null);
}

public sealed record OperatorAIShowcaseDescriptor(
    bool Enabled,
    string Feature,
    string Status,
    string Provider,
    TimeSpan InferenceTime,
    uint PersonRegionCount,
    ulong? SourceSequence,
    ulong? AppliedSequence,
    double? Confidence,
    bool EffectVisible,
    Failure? Failure,
    DateTimeOffset? UpdatedAtUtc)
{
    public static OperatorAIShowcaseDescriptor Unavailable { get; } = new(
        false,
        "Person Segmentation Highlight",
        "UNAVAILABLE",
        "UNVERIFIED",
        TimeSpan.Zero,
        0,
        null,
        null,
        null,
        false,
        null,
        null);
}

public sealed record OperatorShowProjectDescriptor
{
    public OperatorShowProjectDescriptor(string projectId, string name, string state, string detail)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("Show project id is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Show project name is required.", nameof(name));
        if (state is not ("LOADED" or "SAVED" or "RESTORED" or "STALE" or "RECOVERY_REQUIRED" or "UNAVAILABLE"))
            throw new ArgumentException("Show project state is invalid.", nameof(state));
        if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("Show project detail is required.", nameof(detail));

        ProjectId = projectId.Trim();
        Name = name.Trim();
        State = state;
        Detail = detail.Trim();
    }

    public string ProjectId { get; }
    public string Name { get; }
    public string State { get; }
    public string Detail { get; }

    public static OperatorShowProjectDescriptor Unavailable { get; } =
        new("unavailable", "Unavailable", "UNAVAILABLE", "Durable show project state is unavailable.");
}

public sealed record OperatorMutationResponse
{
    public OperatorMutationResponse(bool accepted, AuthoritativeProductionState state, Failure? failure)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        if (accepted && failure is not null)
            throw new ArgumentException("Accepted operator mutations must not carry a failure.", nameof(failure));
        if (!accepted && failure is null)
            throw new ArgumentException("Rejected operator mutations require a failure.", nameof(failure));

        Accepted = accepted;
        Failure = failure;
    }

    public bool Accepted { get; }
    public AuthoritativeProductionState State { get; }
    public Failure? Failure { get; }
}

public sealed record OperatorStatusSnapshot
{
    private readonly ReadOnlyCollection<OperatorSourceDescriptor> _sources;
    private readonly ReadOnlyCollection<OperatorSceneDescriptor> _scenes;
    private readonly ReadOnlyCollection<OperatorOutputRoleDescriptor> _outputRoles;
    private readonly ReadOnlyCollection<OperatorAudioInputDescriptor> _audioInputs;
    private readonly ReadOnlyCollection<OperatorCompositingLayerDescriptor> _compositingLayers;

    public OperatorStatusSnapshot(
        AuthoritativeProductionState production,
        IReadOnlyList<OperatorSourceDescriptor> sources,
        string runtimeStatus,
        string timingStatus,
        string inputStatus,
        string aiStatus,
        string recordingStatus,
        bool visualLayerEnabled,
        double audioPeakLevel,
        OperatorGraphicsOverlayDescriptor? graphicsOverlay = null,
        IReadOnlyList<OperatorAudioInputDescriptor>? audioInputs = null,
        OperatorAudioProgramDescriptor? audioProgram = null,
        OperatorRecordingDescriptor? recording = null,
        OperatorHealthDescriptor? health = null,
        OperatorAIShowcaseDescriptor? aiShowcase = null,
        MediaDeckSnapshot? mediaDeck = null,
        OperatorProductionCgTextDescriptor? productionCgText = null,
        IReadOnlyList<OperatorSceneDescriptor>? scenes = null,
        IReadOnlyList<OperatorOutputRoleDescriptor>? outputRoles = null,
        IReadOnlyList<OperatorCompositingLayerDescriptor>? compositingLayers = null,
        ShowControlWorkspaceSnapshot? showControl = null,
        OperatorShowProjectDescriptor? showProject = null,
        OperatorAudioProductionDescriptor? audioProduction = null,
        ReplayControlSnapshot? replay = null)
    {
        Production = production ?? throw new ArgumentNullException(nameof(production));
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Any(source => source is null))
            throw new ArgumentException("Operator snapshot requires at least one non-null source.", nameof(sources));
        if (string.IsNullOrWhiteSpace(runtimeStatus)) throw new ArgumentException("Runtime status is required.", nameof(runtimeStatus));
        if (string.IsNullOrWhiteSpace(timingStatus)) throw new ArgumentException("Timing status is required.", nameof(timingStatus));
        if (string.IsNullOrWhiteSpace(inputStatus)) throw new ArgumentException("Input status is required.", nameof(inputStatus));
        if (string.IsNullOrWhiteSpace(aiStatus)) throw new ArgumentException("AI status is required.", nameof(aiStatus));
        if (string.IsNullOrWhiteSpace(recordingStatus)) throw new ArgumentException("Recording status is required.", nameof(recordingStatus));
        if (!double.IsFinite(audioPeakLevel) || audioPeakLevel is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(audioPeakLevel));

        _sources = Array.AsReadOnly(sources.ToArray());
        _scenes = Array.AsReadOnly((scenes ?? Array.Empty<OperatorSceneDescriptor>()).ToArray());
        _outputRoles = Array.AsReadOnly((outputRoles ?? Array.Empty<OperatorOutputRoleDescriptor>()).ToArray());
        _audioInputs = Array.AsReadOnly((audioInputs ?? Array.Empty<OperatorAudioInputDescriptor>()).ToArray());
        _compositingLayers = Array.AsReadOnly((compositingLayers ?? Array.Empty<OperatorCompositingLayerDescriptor>())
            .OrderBy(layer => layer.Order)
            .ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
            .ToArray());
        RuntimeStatus = runtimeStatus.Trim();
        TimingStatus = timingStatus.Trim();
        InputStatus = inputStatus.Trim();
        AIStatus = aiStatus.Trim();
        RecordingStatus = recordingStatus.Trim();
        VisualLayerEnabled = visualLayerEnabled;
        AudioPeakLevel = audioPeakLevel;
        GraphicsOverlay = graphicsOverlay ?? OperatorGraphicsOverlayDescriptor.Empty;
        AudioProgram = audioProgram ?? OperatorAudioProgramDescriptor.Unknown;
        Recording = recording ?? OperatorRecordingDescriptor.Unavailable;
        Health = health ?? OperatorHealthDescriptor.Unavailable;
        AIShowcase = aiShowcase ?? OperatorAIShowcaseDescriptor.Unavailable;
        MediaDeck = mediaDeck ?? MediaDeckSnapshot.Unloaded;
        ProductionCgText = productionCgText ?? OperatorProductionCgTextDescriptor.Empty;
        ShowControl = showControl ?? new ShowControlWorkspaceSnapshot(Array.Empty<ShowControlCueList>(), null, ShowControlExecutionSnapshot.Idle);
        ShowProject = showProject ?? OperatorShowProjectDescriptor.Unavailable;
        AudioProduction = audioProduction;
        Replay = replay ?? ReplayControlSnapshot.Unavailable;
    }

    public AuthoritativeProductionState Production { get; }
    public IReadOnlyList<OperatorSourceDescriptor> Sources => _sources;
    public IReadOnlyList<OperatorSceneDescriptor> Scenes => _scenes;
    public IReadOnlyList<OperatorOutputRoleDescriptor> OutputRoles => _outputRoles;
    public IReadOnlyList<OperatorCompositingLayerDescriptor> CompositingLayers => _compositingLayers;
    public string RuntimeStatus { get; }
    public string TimingStatus { get; }
    public string InputStatus { get; }
    public string AIStatus { get; }
    public string RecordingStatus { get; }
    public bool VisualLayerEnabled { get; }
    public double AudioPeakLevel { get; }
    public OperatorGraphicsOverlayDescriptor GraphicsOverlay { get; }
    public IReadOnlyList<OperatorAudioInputDescriptor> AudioInputs => _audioInputs;
    public OperatorAudioProgramDescriptor AudioProgram { get; }
    public OperatorRecordingDescriptor Recording { get; }
    public OperatorHealthDescriptor Health { get; }
    public OperatorAIShowcaseDescriptor AIShowcase { get; }
    public MediaDeckSnapshot MediaDeck { get; }
    public OperatorProductionCgTextDescriptor ProductionCgText { get; }
    public ShowControlWorkspaceSnapshot ShowControl { get; }
    public OperatorShowProjectDescriptor ShowProject { get; }
    public OperatorAudioProductionDescriptor? AudioProduction { get; }
    public ReplayControlSnapshot Replay { get; }
}

/// <summary>
/// Transport seam for the versioned remote-control path. Implementations may use IPC/network transports;
/// tests may use an in-process adapter, but the Operator never gains production authority.
/// </summary>
public interface IOperatorControlTransport
{
    ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default);
    ValueTask<OperatorMutationResponse> ActivateSceneAsync(ActivateSceneCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException("Operator transport does not expose scene activation."));
    ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(RouteOutputRoleCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException("Operator transport does not expose output-role routing."));

    ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(
        string sourceId,
        double gain,
        bool muted,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAudioInputDescriptor>(new NotSupportedException("Operator transport does not expose audio input control."));

    ValueTask<OperatorAudioProgramDescriptor> SetAudioRoutingAsync(
        OperatorAudioRoutingMode mode,
        string? breakawaySourceId,
        ulong expectedRoutingRevision,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAudioProgramDescriptor>(new NotSupportedException("Operator transport does not expose audio routing control."));

    ValueTask<OperatorAudioProductionDescriptor> SetAudioProductionAsync(
        AudioProductionConfiguration configuration,
        ulong expectedRevision,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAudioProductionDescriptor>(
            new NotSupportedException("Operator transport does not expose advanced audio production control."));

    ValueTask<OperatorAudioInputDescriptor> SetAudioTestSignalAsync(
        string sourceId,
        bool enabled,
        int mode,
        double frequencyHz,
        double peakLevel,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAudioInputDescriptor>(new NotSupportedException("Operator transport does not expose generated audio test signal control."));

    ValueTask<bool> SetBroadcastTestPatternAsync(
        string sourceId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<bool>(new NotSupportedException("Operator transport does not expose broadcast test pattern control."));

    ValueTask<bool> SetBroadcastTestPatternAsync(
        string sourceId,
        bool enabled,
        bool motionTiming,
        CancellationToken cancellationToken = default) =>
        motionTiming
            ? ValueTask.FromException<bool>(new NotSupportedException("Operator transport does not expose motion/timing test pattern control."))
            : SetBroadcastTestPatternAsync(sourceId, enabled, cancellationToken);

    ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(
        OperatorGraphicsAsset asset,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> ApplyProductionCgTextAsync(
        OperatorProductionCgText definition,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose Production CG text control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(
        bool visible,
        double positionX,
        double positionY,
        double scale,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorGraphicsOverlayDescriptor>(new NotSupportedException("Operator transport does not expose graphics overlay control."));

    ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerStateAsync(
        string layerId,
        bool visible,
        byte opacity,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<OperatorCompositingLayerDescriptor>>(new NotSupportedException("Operator transport does not expose compositing layer control."));

    ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerTransformAsync(
        string layerId,
        double positionX,
        double positionY,
        double scale,
        double rotationDegrees,
        double anchorX,
        double anchorY,
        double cropLeft,
        double cropTop,
        double cropRight,
        double cropBottom,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<OperatorCompositingLayerDescriptor>>(new NotSupportedException("Operator transport does not expose compositing layer transform control."));

    ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerProcessingNodeAsync(
        string layerId,
        OperatorCompositingProcessingNodeDescriptor? processingNode,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<OperatorCompositingLayerDescriptor>>(new NotSupportedException("Operator transport does not expose compositing layer processing control."));

    ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerProcessingStackAsync(
        string layerId,
        IReadOnlyList<OperatorCompositingProcessingNodeDescriptor> processingStack,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<OperatorCompositingLayerDescriptor>>(new NotSupportedException("Operator transport does not expose compositing processing-stack control."));

    ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> ReorderCompositingLayersAsync(
        IReadOnlyList<string> orderedLayerIds,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<OperatorCompositingLayerDescriptor>>(new NotSupportedException("Operator transport does not expose compositing layer reorder control."));

    ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorRecordingCommandResult>(new NotSupportedException("Operator transport does not expose recording control."));

    ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        string? profileId,
        CancellationToken cancellationToken = default) =>
        StartRecordingAsync(destinationDirectory, fileName, cancellationToken);

    ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorRecordingCommandResult>(new NotSupportedException("Operator transport does not expose recording control."));

    ValueTask<ReplayControlSnapshot> GetReplaySnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplayControlSnapshot>(new NotSupportedException("Operator transport does not expose replay state."));

    ValueTask<ReplayControlSnapshot> MarkReplayInAsync(
        TimeSpan? lookback = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplayControlSnapshot>(new NotSupportedException("Operator transport does not expose replay MARK IN."));

    ValueTask<ReplayControlSnapshot> MarkReplayOutAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplayControlSnapshot>(new NotSupportedException("Operator transport does not expose replay MARK OUT."));

    ValueTask<ReplayControlSnapshot> SetReplayRangeAsync(
        TimeSpan @in,
        TimeSpan @out,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplayControlSnapshot>(new NotSupportedException("Operator transport does not expose replay range control."));

    ValueTask<ReplayClipAssetResult> CreateReplayClipAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplayClipAssetResult>(new NotSupportedException("Operator transport does not expose replay clip creation."));

    ValueTask<OperatorAIShowcaseDescriptor> SetAIShowcaseEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OperatorAIShowcaseDescriptor>(new NotSupportedException("Operator transport does not expose AI showcase control."));

    ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetCatalogSnapshot>(new NotSupportedException("Operator transport does not expose the media asset catalogue."));

    ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(
        IReadOnlyList<string> sourceLocations,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException("Operator transport does not expose media asset import."));

    ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(
        MediaAssetId assetId,
        string sourceLocation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException("Operator transport does not expose media asset relink."));

    ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(
        MediaAssetId assetId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException("Operator transport does not expose media asset removal."));

    ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetCatalogSnapshot>(new NotSupportedException("Operator transport does not expose media asset availability refresh."));

    ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(MediaDeckOpenRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(MediaTransportCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(MediaMarkerCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckSnapshot>(new NotSupportedException("Operator transport does not expose media-deck control."));

    ValueTask<ShowControlWorkspaceSnapshot> GetShowControlSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control state."));

    ValueTask<ShowControlWorkspaceSnapshot> SaveShowControlCueListAsync(ShowControlCueList cueList, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control authoring."));

    ValueTask<ShowControlWorkspaceSnapshot> SelectShowControlCueListAsync(ShowControlCueListId cueListId, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control selection."));

    ValueTask<ShowControlWorkspaceSnapshot> ArmShowControlAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control arming."));

    ValueTask<ShowControlWorkspaceSnapshot> GoShowControlAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control GO."));

    ValueTask<ShowControlWorkspaceSnapshot> CancelShowControlAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control cancellation."));

    ValueTask<ShowControlWorkspaceSnapshot> AcknowledgeShowControlRecoveryAsync(bool resume, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShowControlWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose show-control recovery."));

    ValueTask<RundownWorkspaceSnapshot> GetRundownSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown state."));

    ValueTask<RundownWorkspaceSnapshot> SaveRundownAsync(
        RundownDefinition rundown,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown authoring."));

    ValueTask<RundownWorkspaceSnapshot> PrepareRundownItemAsync(
        RundownItemId itemId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown preparation."));

    ValueTask<RundownWorkspaceSnapshot> GoRundownAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown GO."));

    ValueTask<RundownWorkspaceSnapshot> NextRundownAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown navigation."));

    ValueTask<RundownWorkspaceSnapshot> PreviousRundownAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown navigation."));

    ValueTask<RundownWorkspaceSnapshot> HoldRundownAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown hold."));

    ValueTask<RundownWorkspaceSnapshot> AcknowledgeRundownRecoveryAsync(
        bool resume,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose rundown recovery."));

    ValueTask<ProductionMacroWorkspaceSnapshot> GetProductionMacroSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro state."));

    ValueTask<ProductionMacroDefinition> GetProductionMacroAsync(
        ProductionMacroId macroId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroDefinition>(new NotSupportedException("Operator transport does not expose Production Macro lookup."));

    ValueTask<ProductionMacroWorkspaceSnapshot> SaveProductionMacroAsync(
        ProductionMacroDefinition macro,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro authoring."));

    ValueTask<ProductionMacroWorkspaceSnapshot> DeleteProductionMacroAsync(
        ProductionMacroId macroId,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro deletion."));

    ValueTask<ProductionMacroValidationResult> ValidateProductionMacroAsync(
        ProductionMacroDefinition macro,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroValidationResult>(new NotSupportedException("Operator transport does not expose Production Macro validation."));

    ValueTask<ProductionMacroWorkspaceSnapshot> ExecuteProductionMacroAsync(
        ProductionMacroId macroId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro execution."));

    ValueTask<ProductionMacroWorkspaceSnapshot> CancelProductionMacroAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro cancellation."));

    ValueTask<ProductionMacroWorkspaceSnapshot> AcknowledgeProductionMacroRecoveryAsync(
        bool resume,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProductionMacroWorkspaceSnapshot>(new NotSupportedException("Operator transport does not expose Production Macro recovery."));
}

public interface IMediaAssetCatalogClient
{
    ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default);
    ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(
        IReadOnlyList<string> sourceLocations,
        CancellationToken cancellationToken = default);
    ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(
        MediaAssetId assetId,
        string sourceLocation,
        CancellationToken cancellationToken = default);
    ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(
        MediaAssetId assetId,
        CancellationToken cancellationToken = default);
    ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default);
}

public sealed class OperatorControlClient : IMediaAssetCatalogClient
{
    private readonly IOperatorControlTransport _transport;
    private OperatorStatusSnapshot? _snapshot;

    public OperatorControlClient(IOperatorControlTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public OperatorStatusSnapshot? Snapshot => _snapshot;
    public bool Connected => _snapshot is not null;

    public async ValueTask<OperatorStatusSnapshot> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        _snapshot = await _transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return _snapshot;
    }

    public ValueTask<OperatorMutationResponse> SelectPreviewAsync(string sourceId, CancellationToken cancellationToken = default) =>
        SelectPreviewAsync(ParseSourceId(sourceId), cancellationToken);

    public async ValueTask<OperatorMutationResponse> SelectPreviewAsync(
        ProductionSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new SelectPreviewCommand(Metadata(current.Production), sourceId);
        var result = await _transport.SelectPreviewAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> ActivateSceneAsync(string sceneId, CancellationToken cancellationToken = default) =>
        ActivateSceneAsync(ParseSceneId(sceneId), cancellationToken);

    public async ValueTask<OperatorMutationResponse> ActivateSceneAsync(
        SceneId sceneId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new ActivateSceneCommand(Metadata(current.Production), sceneId);
        var result = await _transport.ActivateSceneAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(string roleId, string sourceId, CancellationToken cancellationToken = default) =>
        RouteOutputRoleAsync(new OutputRoleId(roleId), ParseSourceId(sourceId), null, cancellationToken);

    public ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(
        string roleId,
        string sourceId,
        string audioBusId,
        CancellationToken cancellationToken = default) =>
        RouteOutputRoleAsync(new OutputRoleId(roleId), ParseSourceId(sourceId), audioBusId, cancellationToken);

    public ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(
        OutputRoleId roleId,
        ProductionSourceId sourceId,
        CancellationToken cancellationToken = default) =>
        RouteOutputRoleAsync(roleId, sourceId, null, cancellationToken);

    public async ValueTask<OperatorMutationResponse> RouteOutputRoleAsync(
        OutputRoleId roleId,
        ProductionSourceId sourceId,
        string? audioBusId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new RouteOutputRoleCommand(Metadata(current.Production), roleId, sourceId, audioBusId);
        var result = await _transport.RouteOutputRoleAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> CutAsync(string sourceId, CancellationToken cancellationToken = default) =>
        CutAsync(ParseSourceId(sourceId), cancellationToken);

    public ValueTask<OperatorMutationResponse> CutPreviewAsync(CancellationToken cancellationToken = default) =>
        CutAsync(RequireSnapshot().Production.Routing.PreviewSourceId, cancellationToken);

    public async ValueTask<OperatorMutationResponse> CutAsync(
        ProductionSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new CutProgramCommand(Metadata(current.Production), sourceId);
        var result = await _transport.CutProgramAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorMutationResponse> DissolveAsync(
        string sourceId,
        uint durationFrames,
        CancellationToken cancellationToken = default) =>
        DissolveAsync(ParseSourceId(sourceId), durationFrames, cancellationToken);

    public ValueTask<OperatorMutationResponse> DissolvePreviewAsync(
        uint durationFrames,
        CancellationToken cancellationToken = default) =>
        DissolveAsync(RequireSnapshot().Production.Routing.PreviewSourceId, durationFrames, cancellationToken);

    public async ValueTask<OperatorMutationResponse> DissolveAsync(
        ProductionSourceId sourceId,
        uint durationFrames,
        CancellationToken cancellationToken = default)
    {
        var current = RequireSnapshot();
        var command = new DissolveProgramCommand(Metadata(current.Production), sourceId, durationFrames);
        var result = await _transport.DissolveProgramAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
            await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(
        string sourceId,
        double gain,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Audio source id is required.", nameof(sourceId));
        if (!double.IsFinite(gain) || gain is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(gain), "Audio gain must be finite and in the inclusive range 0..4.");

        RequireSnapshot();
        var result = await _transport
            .SetAudioInputStateAsync(sourceId.Trim(), gain, muted, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAudioProgramDescriptor> SetAudioRoutingAsync(
        OperatorAudioRoutingMode mode,
        string? breakawaySourceId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == OperatorAudioRoutingMode.Breakaway && string.IsNullOrWhiteSpace(breakawaySourceId))
            throw new ArgumentException("Breakaway audio routing requires an explicit source.", nameof(breakawaySourceId));
        if (mode == OperatorAudioRoutingMode.FollowVideo && !string.IsNullOrWhiteSpace(breakawaySourceId))
            throw new ArgumentException("FOLLOW_VIDEO audio routing must not declare a breakaway source.", nameof(breakawaySourceId));

        var current = RequireSnapshot();
        var result = await _transport
            .SetAudioRoutingAsync(
                mode,
                string.IsNullOrWhiteSpace(breakawaySourceId) ? null : breakawaySourceId.Trim(),
                current.AudioProgram.RoutingRevision,
                cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAudioProductionDescriptor> SetAudioProductionAsync(
        AudioProductionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var current = RequireSnapshot();
        var confirmed = current.AudioProduction
            ?? throw new InvalidOperationException("Advanced audio production state is not available from Runtime.");
        if (confirmed.Configuration.Revision == ulong.MaxValue)
            throw new InvalidOperationException("Audio production revision cannot advance beyond UInt64.MaxValue.");
        if (configuration.Revision != confirmed.Configuration.Revision + 1)
            throw new ArgumentException(
                $"Audio production configuration revision must be {confirmed.Configuration.Revision + 1}.",
                nameof(configuration));

        var result = await _transport
            .SetAudioProductionAsync(configuration, confirmed.Configuration.Revision, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAudioInputDescriptor> SetAudioTestSignalAsync(
        string sourceId,
        bool enabled,
        int mode,
        double frequencyHz,
        double peakLevel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Audio source id is required.", nameof(sourceId));
        if (mode is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(mode), "Generated audio test signal mode must be in the supported range 1..5.");
        if (!double.IsFinite(frequencyHz) || frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (!double.IsFinite(peakLevel) || peakLevel is < 0 or > 0.5)
            throw new ArgumentOutOfRangeException(nameof(peakLevel));

        RequireSnapshot();
        var result = await _transport
            .SetAudioTestSignalAsync(sourceId.Trim(), enabled, mode, frequencyHz, peakLevel, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<bool> SetBroadcastTestPatternAsync(
        string sourceId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SetBroadcastTestPatternAsync(sourceId, enabled, false, cancellationToken);

    public async ValueTask<bool> SetBroadcastTestPatternAsync(
        string sourceId,
        bool enabled,
        bool motionTiming,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Broadcast test pattern source id is required.", nameof(sourceId));

        RequireSnapshot();
        var result = await _transport
            .SetBroadcastTestPatternAsync(sourceId.Trim(), enabled, motionTiming, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> LoadGraphicsOverlayAsync(
        OperatorGraphicsAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var result = await _transport.LoadGraphicsOverlayAsync(asset, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> ApplyProductionCgTextAsync(
        OperatorProductionCgText definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = await _transport.ApplyProductionCgTextAsync(definition, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> SetGraphicsOverlayAsync(
        bool visible,
        double positionX,
        double positionY,
        double scale,
        CancellationToken cancellationToken = default)
    {
        var result = await _transport.SetGraphicsOverlayAsync(visible, positionX, positionY, scale, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorGraphicsOverlayDescriptor> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default)
    {
        var result = await _transport.ClearGraphicsOverlayAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerStateAsync(
        string layerId,
        bool visible,
        byte opacity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layerId))
            throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
        RequireSnapshot();
        var result = await _transport
            .SetCompositingLayerStateAsync(layerId.Trim(), visible, opacity, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerTransformAsync(
        string layerId,
        double positionX,
        double positionY,
        double scale,
        double rotationDegrees,
        double anchorX,
        double anchorY,
        double cropLeft,
        double cropTop,
        double cropRight,
        double cropBottom,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layerId))
            throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
        RequireSnapshot();
        var result = await _transport.SetCompositingLayerTransformAsync(
            layerId.Trim(),
            positionX,
            positionY,
            scale,
            rotationDegrees,
            anchorX,
            anchorY,
            cropLeft,
            cropTop,
            cropRight,
            cropBottom,
            cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerProcessingNodeAsync(
        string layerId,
        OperatorCompositingProcessingNodeDescriptor? processingNode,
        CancellationToken cancellationToken = default) =>
        SetCompositingLayerProcessingStackAsync(
            layerId,
            processingNode is null
                ? Array.Empty<OperatorCompositingProcessingNodeDescriptor>()
                : new[] { processingNode },
            cancellationToken);

    public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerProcessingStackAsync(
        string layerId,
        IReadOnlyList<OperatorCompositingProcessingNodeDescriptor> processingStack,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layerId))
            throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
        ArgumentNullException.ThrowIfNull(processingStack);
        if (processingStack.Count > OperatorCompositingLayerDescriptor.MaximumProcessingNodeCount)
            throw new ArgumentException($"Compositing processing stack supports at most {OperatorCompositingLayerDescriptor.MaximumProcessingNodeCount} nodes.", nameof(processingStack));
        RequireSnapshot();
        var result = await _transport
            .SetCompositingLayerProcessingStackAsync(layerId.Trim(), processingStack, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> ReorderCompositingLayersAsync(
        IReadOnlyList<string> orderedLayerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedLayerIds);
        RequireSnapshot();
        var result = await _transport
            .ReorderCompositingLayersAsync(orderedLayerIds, cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        CancellationToken cancellationToken = default) =>
        StartRecordingAsync(destinationDirectory, fileName, null, cancellationToken);

    public async ValueTask<OperatorRecordingCommandResult> StartRecordingAsync(
        string destinationDirectory,
        string fileName,
        string? profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Recording destination directory is required.", nameof(destinationDirectory));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Recording file name is required.", nameof(fileName));

        RequireSnapshot();
        var result = await _transport
            .StartRecordingAsync(
                destinationDirectory.Trim(),
                fileName.Trim(),
                string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<ReplayControlSnapshot> GetReplaySnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetReplaySnapshotAsync(cancellationToken);

    public ValueTask<ReplayControlSnapshot> MarkReplayInAsync(
        TimeSpan? lookback = null,
        CancellationToken cancellationToken = default) =>
        _transport.MarkReplayInAsync(lookback, cancellationToken);

    public ValueTask<ReplayControlSnapshot> MarkReplayOutAsync(CancellationToken cancellationToken = default) =>
        _transport.MarkReplayOutAsync(cancellationToken);

    public ValueTask<ReplayControlSnapshot> SetReplayRangeAsync(
        TimeSpan @in,
        TimeSpan @out,
        CancellationToken cancellationToken = default) =>
        _transport.SetReplayRangeAsync(@in, @out, cancellationToken);

    public async ValueTask<ReplayClipAssetResult> CreateReplayClipAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Replay clip name is required.", nameof(name));
        RequireSnapshot();
        var result = await _transport.CreateReplayClipAsync(name.Trim(), cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<OperatorAIShowcaseDescriptor> SetAIShowcaseEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.SetAIShowcaseEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) =>
        _transport.GetMediaAssetCatalogAsync(cancellationToken);

    public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(
        IReadOnlyList<string> sourceLocations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLocations);
        return _transport.ImportMediaAssetsAsync(sourceLocations, cancellationToken);
    }

    public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(
        MediaAssetId assetId,
        string sourceLocation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceLocation))
            throw new ArgumentException("Media asset source location is required.", nameof(sourceLocation));
        return _transport.RelinkMediaAssetAsync(assetId, sourceLocation, cancellationToken);
    }

    public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(
        MediaAssetId assetId,
        CancellationToken cancellationToken = default) =>
        _transport.RemoveMediaAssetAsync(assetId, cancellationToken);

    public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        _transport.RefreshMediaAssetAvailabilityAsync(cancellationToken);

    public ValueTask<MediaDeckSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetMediaDeckSnapshotAsync(cancellationToken);

    public ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(
        string path,
        MediaSourceId sourceId,
        CancellationToken cancellationToken = default) =>
        OpenMediaDeckAsync(path, sourceId, null, cancellationToken);

    public ValueTask<MediaDeckSnapshot> OpenMediaDeckAsync(
        string path,
        MediaSourceId sourceId,
        MediaAssetId? assetId,
        CancellationToken cancellationToken = default) =>
        _transport.OpenMediaDeckAsync(
            new MediaDeckOpenRequest(MediaContractVersion.Current, sourceId, path, assetId),
            cancellationToken);

    public ValueTask<MediaDeckSnapshot> ApplyMediaDeckTransportAsync(
        MediaTransportCommand command,
        CancellationToken cancellationToken = default) =>
        _transport.ApplyMediaDeckTransportAsync(command, cancellationToken);

    public ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(
        MediaMarkerCommand command,
        CancellationToken cancellationToken = default) =>
        _transport.ApplyMediaDeckMarkerAsync(command, cancellationToken);

    public ValueTask<MediaDeckSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
        _transport.CloseMediaDeckAsync(cancellationToken);

    public ValueTask<ShowControlWorkspaceSnapshot> GetShowControlSnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetShowControlSnapshotAsync(cancellationToken);

    public async ValueTask<ShowControlWorkspaceSnapshot> SaveShowControlCueListAsync(
        ShowControlCueList cueList,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cueList);
        RequireSnapshot();
        var result = await _transport.SaveShowControlCueListAsync(cueList, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ShowControlWorkspaceSnapshot> SelectShowControlCueListAsync(
        ShowControlCueListId cueListId,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.SelectShowControlCueListAsync(cueListId, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ShowControlWorkspaceSnapshot> ArmShowControlAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.ArmShowControlAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ShowControlWorkspaceSnapshot> GoShowControlAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.GoShowControlAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ShowControlWorkspaceSnapshot> CancelShowControlAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.CancelShowControlAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ShowControlWorkspaceSnapshot> AcknowledgeShowControlRecoveryAsync(
        bool resume,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.AcknowledgeShowControlRecoveryAsync(resume, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<RundownWorkspaceSnapshot> GetRundownSnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetRundownSnapshotAsync(cancellationToken);

    public async ValueTask<RundownWorkspaceSnapshot> SaveRundownAsync(
        RundownDefinition rundown,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rundown);
        RequireSnapshot();
        var result = await _transport.SaveRundownAsync(rundown, expectedStorageVersion, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> PrepareRundownItemAsync(
        RundownItemId itemId,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.PrepareRundownItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> GoRundownAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.GoRundownAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> NextRundownAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.NextRundownAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> PreviousRundownAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.PreviousRundownAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> HoldRundownAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.HoldRundownAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<RundownWorkspaceSnapshot> AcknowledgeRundownRecoveryAsync(
        bool resume,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.AcknowledgeRundownRecoveryAsync(resume, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<ProductionMacroWorkspaceSnapshot> GetProductionMacroSnapshotAsync(CancellationToken cancellationToken = default) =>
        _transport.GetProductionMacroSnapshotAsync(cancellationToken);

    public ValueTask<ProductionMacroDefinition> GetProductionMacroAsync(
        ProductionMacroId macroId,
        CancellationToken cancellationToken = default) =>
        _transport.GetProductionMacroAsync(macroId, cancellationToken);

    public ValueTask<ProductionMacroValidationResult> ValidateProductionMacroAsync(
        ProductionMacroDefinition macro,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return _transport.ValidateProductionMacroAsync(macro, cancellationToken);
    }

    public async ValueTask<ProductionMacroWorkspaceSnapshot> SaveProductionMacroAsync(
        ProductionMacroDefinition macro,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        RequireSnapshot();
        var result = await _transport.SaveProductionMacroAsync(macro, expectedStorageVersion, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ProductionMacroWorkspaceSnapshot> DeleteProductionMacroAsync(
        ProductionMacroId macroId,
        ulong expectedStorageVersion,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.DeleteProductionMacroAsync(macroId, expectedStorageVersion, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<ProductionMacroWorkspaceSnapshot> ExecuteProductionMacroAsync(
        string macroId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(macroId))
            throw new ArgumentException("Production Macro identity is required.", nameof(macroId));
        return ExecuteProductionMacroAsync(new ProductionMacroId(Identity.Parse(macroId.Trim())), cancellationToken);
    }

    public async ValueTask<ProductionMacroWorkspaceSnapshot> ExecuteProductionMacroAsync(
        ProductionMacroId macroId,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.ExecuteProductionMacroAsync(macroId, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ProductionMacroWorkspaceSnapshot> CancelProductionMacroAsync(CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.CancelProductionMacroAsync(cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ProductionMacroWorkspaceSnapshot> AcknowledgeProductionMacroRecoveryAsync(
        bool resume,
        CancellationToken cancellationToken = default)
    {
        RequireSnapshot();
        var result = await _transport.AcknowledgeProductionMacroRecoveryAsync(resume, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public void Disconnect() => _snapshot = null;

    private OperatorStatusSnapshot RequireSnapshot() =>
        _snapshot ?? throw new InvalidOperationException("Operator client must synchronize authoritative state before issuing commands.");

    private static ProductionSourceId ParseSourceId(string sourceId) =>
        new(Identity.Parse(sourceId));

    private static SceneId ParseSceneId(string sceneId) =>
        new(Identity.Parse(sceneId));

    private static ControlCommandMetadata Metadata(AuthoritativeProductionState state) =>
        new(ControlContractVersion.Current, CommandId.New(), state.ProductionId, state.Revision);
}
