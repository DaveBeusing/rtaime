using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class GovernedAIContractTests
{
    private static readonly JsonSerializerOptions TransportJson = CreateOptions();

    [Fact]
    public void V1_execution_request_round_trips_with_timing_budget_and_resource_handle()
    {
        var request = ExecutionRequest();
        var json = JsonSerializer.Serialize(request, TransportJson);
        var copy = JsonSerializer.Deserialize<GovernedInferenceExecutionRequest>(json, TransportJson);

        Assert.NotNull(copy);
        Assert.Equal(request.Version, copy.Version);
        Assert.Equal(request.Request.RequestId, copy.Request.RequestId);
        Assert.Equal(request.Request.CapabilityId, copy.Request.CapabilityId);
        Assert.Equal(request.Request.InputFrame, copy.Request.InputFrame);
        Assert.Equal(request.Context.TimingDomainId, copy.Context.TimingDomainId);
        Assert.Equal(request.Context.ProductionTime, copy.Context.ProductionTime);
        Assert.Equal(request.Context.ResourceBudget, copy.Context.ResourceBudget);
        Assert.Equal(request.Context.InputResourceHandle, copy.Context.InputResourceHandle);
        Assert.DoesNotContain("PixelData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BufferData", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_descriptor_advertises_controlled_model_package_and_round_trips()
    {
        var capabilityId = InferenceCapabilityId.New();
        var descriptor = new InferenceProviderDescriptor(
            AIContractVersion.Current,
            InferenceProviderId.New(),
            "Reference Provider",
            InferenceProviderState.Ready,
            null,
            new[] { new InferenceCapabilityDescriptor(AIContractVersion.Current, capabilityId, "ai.person-segmentation", true) },
            new[]
            {
                new InferenceModelPackageDescriptor(
                    InferenceModelId.New(),
                    "1.0.0",
                    capabilityId,
                    "managed",
                    "video-frame-descriptor.v1",
                    "segmentation-mask-descriptor.v1",
                    "compute=10;vram=67108864",
                    "reference",
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    "controlled-test-package")
            });

        var json = JsonSerializer.Serialize(descriptor, TransportJson);
        var copy = JsonSerializer.Deserialize<InferenceProviderDescriptor>(json, TransportJson);

        Assert.NotNull(copy);
        Assert.Equal(descriptor.ProviderId, copy.ProviderId);
        Assert.Equal(descriptor.State, copy.State);
        Assert.Single(copy.Capabilities);
        Assert.Single(copy.Models);
        Assert.Equal(descriptor.Models[0].ModelId, copy.Models[0].ModelId);
        Assert.Equal(descriptor.Models[0].ArtifactHashSha256, copy.Models[0].ArtifactHashSha256);
        Assert.Equal(descriptor.Models[0].Provenance, copy.Models[0].Provenance);
    }

    [Fact]
    public void Unavailable_provider_requires_failure()
    {
        Assert.Throws<ArgumentException>(() => new InferenceProviderDescriptor(
            AIContractVersion.Current,
            InferenceProviderId.New(),
            "Unavailable",
            InferenceProviderState.Unavailable,
            null,
            Array.Empty<InferenceCapabilityDescriptor>(),
            Array.Empty<InferenceModelPackageDescriptor>()));
    }

    [Fact]
    public void Successful_provider_result_requires_model_payload_and_resource_handle()
    {
        Assert.Throws<ArgumentException>(() => new InferenceProviderExecutionResult(
            InferenceExecutionStatus.Succeeded,
            Array.Empty<InferenceOutput>(),
            null,
            null,
            0.9,
            0.1,
            Duration.Zero,
            null,
            null,
            null));
    }

    [Fact]
    public void Governed_execution_result_requires_metadata_only_for_success()
    {
        var requestId = InferenceRequestId.New();
        var providerId = InferenceProviderId.New();
        var admission = new InferenceAdmissionDecision(requestId, InferenceAdmissionStatus.Admitted, providerId, null);
        var failed = new GovernedInferenceResult(
            AIContractVersion.Current,
            requestId,
            InferenceExecutionStatus.Failed,
            Array.Empty<InferenceOutput>(),
            new Failure("ai.failed", "Failure."));

        Assert.Throws<ArgumentException>(() => new GovernedInferenceExecutionResult(
            AIContractVersion.Current,
            admission,
            failed,
            new InferenceResultMetadata(
                InferenceResultId.New(),
                InferenceCapabilityId.New(),
                SurfaceId.New(),
                InferenceModelId.New(),
                "1.0.0",
                providerId,
                UtcTimestamp.UnixEpoch,
                new InferenceProductionTime(0, new Timebase(1, 50)),
                Duration.Zero,
                0.9,
                0.1,
                new InferencePayloadDescriptor("mask", "application/x-mask"),
                new InferenceResourceHandle("test", "handle"))));
    }

    private static GovernedInferenceExecutionRequest ExecutionRequest()
    {
        var frame = new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            new SurfaceDescriptor(
                SurfaceId.New(),
                VideoFormat.Hd1080p50Rgba8,
                SurfaceStorageDomain.Shared,
                SurfaceOwnership.SharedLease,
                new SurfaceLifetimeDescriptor(Generation.Initial, Identity.New()),
                new OpaqueSurfaceHandle("test.surface", "surface-0")),
            new FrameTiming(0, 0, new Timebase(1, 50)));
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceCapabilityId.New(),
            frame,
            new UtcTimestamp(new DateTimeOffset(2030, 1, 1, 0, 0, 1, TimeSpan.Zero)),
            new[] { new InferenceParameter("mode", "person") });
        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                Identity.New(),
                new InferenceProductionTime(0, new Timebase(1, 50)),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new GovernedAIStrongIdentityConverterFactory());
        options.Converters.Add(new InferenceProductionTimeJsonConverter());
        options.Converters.Add(new ContractScalarJsonConverterFactory());
        return options;
    }
}

internal sealed class InferenceProductionTimeJsonConverter : JsonConverter<InferenceProductionTime>
{
    public override InferenceProductionTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        try
        {
            return new InferenceProductionTime(
                root.GetProperty("timestamp").GetInt64(),
                Timebase.Parse(root.GetProperty("timebase").GetString() ?? throw new JsonException("Inference production timebase must not be null.")));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new JsonException("Invalid inference production time representation.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        InferenceProductionTime value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("timestamp", value.Timestamp);
        writer.WriteString("timebase", value.Timebase.ToString());
        writer.WriteEndObject();
    }
}

internal sealed class GovernedAIStrongIdentityConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(InferenceResultId) ||
        typeToConvert == typeof(InferenceModelId) ||
        typeToConvert == typeof(InferenceProviderId);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(InferenceResultId)) return new InferenceResultIdConverter();
        if (typeToConvert == typeof(InferenceModelId)) return new InferenceModelIdConverter();
        if (typeToConvert == typeof(InferenceProviderId)) return new InferenceProviderIdConverter();
        throw new NotSupportedException();
    }

    private sealed class InferenceResultIdConverter : JsonConverter<InferenceResultId>
    {
        public override InferenceResultId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(Identity.Parse(reader.GetString() ?? throw new JsonException()));
        public override void Write(Utf8JsonWriter writer, InferenceResultId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class InferenceModelIdConverter : JsonConverter<InferenceModelId>
    {
        public override InferenceModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(Identity.Parse(reader.GetString() ?? throw new JsonException()));
        public override void Write(Utf8JsonWriter writer, InferenceModelId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class InferenceProviderIdConverter : JsonConverter<InferenceProviderId>
    {
        public override InferenceProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(Identity.Parse(reader.GetString() ?? throw new JsonException()));
        public override void Write(Utf8JsonWriter writer, InferenceProviderId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
