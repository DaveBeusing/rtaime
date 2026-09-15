using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.AI.Contracts;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class ContractFoundationTests
{
    private static readonly JsonSerializerOptions TransportJson = CreateTransportJsonOptions();

    [Fact]
    public void All_contract_families_fail_closed_for_unknown_versions()
    {
        var unknown = new CompatibilityVersion(99, 0);

        Assert.False(ControlContractVersion.IsSupported(unknown));
        Assert.False(RuntimeContractVersion.IsSupported(unknown));
        Assert.False(MediaContractVersion.IsSupported(unknown));
        Assert.False(ProviderContractVersion.IsSupported(unknown));
        Assert.False(AIContractVersion.IsSupported(unknown));

        Assert.Throws<NotSupportedException>(() => ControlContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => RuntimeContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => MediaContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => ProviderContractVersion.EnsureSupported(unknown));
        Assert.Throws<NotSupportedException>(() => AIContractVersion.EnsureSupported(unknown));
    }

    [Fact]
    public void Contract_versions_are_explicitly_v1_0()
    {
        var expected = new CompatibilityVersion(1, 0);

        Assert.Equal(expected, ControlContractVersion.Current);
        Assert.Equal(expected, RuntimeContractVersion.Current);
        Assert.Equal(expected, MediaContractVersion.Current);
        Assert.Equal(expected, ProviderContractVersion.Current);
        Assert.Equal(expected, AIContractVersion.Current);
    }

    [Fact]
    public void Transport_profile_uses_canonical_scalar_forms()
    {
        var identity = new Identity(Guid.Parse("12345678-1234-1234-1234-123456789abc"));
        var productionId = new ProductionId(identity);

        Assert.Equal("\"12345678-1234-1234-1234-123456789abc\"", JsonSerializer.Serialize(identity, TransportJson));
        Assert.Equal("\"12345678-1234-1234-1234-123456789abc\"", JsonSerializer.Serialize(productionId, TransportJson));
        Assert.Equal("\"42\"", JsonSerializer.Serialize(new Revision(42), TransportJson));
        Assert.Equal("\"60000/1001\"", JsonSerializer.Serialize(FrameRate.Fps59_94, TransportJson));
        Assert.Equal("\"1.0\"", JsonSerializer.Serialize(new CompatibilityVersion(1, 0), TransportJson));

        Assert.Equal(identity, JsonSerializer.Deserialize<Identity>(JsonSerializer.Serialize(identity, TransportJson), TransportJson));
        Assert.Equal(productionId, JsonSerializer.Deserialize<ProductionId>(JsonSerializer.Serialize(productionId, TransportJson), TransportJson));
    }

    [Fact]
    public void Control_contracts_snapshot_input_collections_and_round_trip()
    {
        var sourceA = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera A");
        var sourceB = new ProductionSourceSpecification(ProductionSourceId.New(), "Camera B");
        var mutableSources = new List<ProductionSourceSpecification> { sourceA, sourceB };
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId.New(),
            "Reference production",
            mutableSources,
            new ProductionRoutingState(sourceA.SourceId, sourceB.SourceId));

        mutableSources.Clear();

        Assert.Equal(2, specification.Sources.Count);
        var copy = RoundTrip(specification);
        Assert.Equal(specification.Version, copy.Version);
        Assert.Equal(specification.ProductionId, copy.ProductionId);
        Assert.Equal(specification.Name, copy.Name);
        Assert.Equal(specification.Sources, copy.Sources);
        Assert.Equal(specification.InitialRouting, copy.InitialRouting);
    }

    [Fact]
    public void Control_command_metadata_preserves_identity_and_revision()
    {
        var metadata = new ControlCommandMetadata(
            ControlContractVersion.Current,
            CommandId.New(),
            ProductionId.New(),
            new Revision(42));
        var command = new SelectPreviewCommand(metadata, ProductionSourceId.New());

        var copy = RoundTrip(command);

        Assert.Equal(command.Metadata.Version, copy.Metadata.Version);
        Assert.Equal(command.Metadata.CommandId, copy.Metadata.CommandId);
        Assert.Equal(command.Metadata.ProductionId, copy.Metadata.ProductionId);
        Assert.Equal(command.Metadata.ExpectedRevision, copy.Metadata.ExpectedRevision);
        Assert.Equal(command.SourceId, copy.SourceId);
    }

    [Fact]
    public void Control_validation_report_is_immutable_and_transportable()
    {
        var issues = new List<ValidationIssue> { new("control.invalid", "Invalid state", "routing.program") };
        var report = new ControlValidationReport(issues);
        issues.Clear();

        Assert.False(report.IsValid);
        Assert.Single(report.Issues);

        var copy = RoundTrip(report);
        Assert.False(copy.IsValid);
        Assert.Equal(report.Issues, copy.Issues);
    }

    [Fact]
    public void Media_video_formats_cover_v1_reference_rates()
    {
        Assert.Equal(FrameRate.Fps50, VideoFormat.Hd1080p50Rgba8.FrameRate);
        Assert.Equal(FrameRate.Fps59_94, VideoFormat.Hd1080p59_94Rgba8.FrameRate);
        Assert.Equal(1920u, VideoFormat.Hd1080p50Rgba8.Width);
        Assert.Equal(1080u, VideoFormat.Hd1080p50Rgba8.Height);
    }

    [Fact]
    public void Media_contract_rejects_unknown_enum_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VideoFormat(1920, 1080, FrameRate.Fps50, (PixelFormat)999, ScanMode.Progressive));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SurfaceDescriptor(
                SurfaceId.New(),
                VideoFormat.Hd1080p50Rgba8,
                (SurfaceStorageDomain)999,
                SurfaceOwnership.ProducerOwned,
                new SurfaceLifetimeDescriptor(Generation.Initial, null),
                null));
    }

    [Fact]
    public void Frame_descriptor_round_trips_without_bulk_media_payload()
    {
        var descriptor = ContractFixtures.Frame();
        var json = JsonSerializer.Serialize(descriptor, TransportJson);
        var copy = JsonSerializer.Deserialize<FrameDescriptor>(json, TransportJson);

        Assert.NotNull(copy);
        Assert.Equal(descriptor.Version, copy.Version);
        Assert.Equal(descriptor.SourceId, copy.SourceId);
        Assert.Equal(descriptor.Surface.SurfaceId, copy.Surface.SurfaceId);
        Assert.Equal(descriptor.Surface.Format, copy.Surface.Format);
        Assert.Equal(descriptor.Surface.StorageDomain, copy.Surface.StorageDomain);
        Assert.Equal(descriptor.Surface.Ownership, copy.Surface.Ownership);
        Assert.Equal(descriptor.Surface.Lifetime, copy.Surface.Lifetime);
        Assert.Equal(descriptor.Surface.Handle, copy.Surface.Handle);
        Assert.Equal(descriptor.Timing, copy.Timing);
        Assert.DoesNotContain("PixelData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BufferData", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_descriptor_round_trips_with_capabilities_resources_and_availability()
    {
        var descriptor = ContractFixtures.Provider();
        var copy = RoundTrip(descriptor);

        Assert.Equal(descriptor.Version, copy.Version);
        Assert.Equal(descriptor.ProviderId, copy.ProviderId);
        Assert.Equal(descriptor.Name, copy.Name);
        Assert.Equal(descriptor.Availability, copy.Availability);
        Assert.Equal(descriptor.Capabilities, copy.Capabilities);
        Assert.Equal(descriptor.Resources, copy.Resources);
    }

    [Fact]
    public void Provider_unavailable_state_requires_failure_and_round_trips()
    {
        Assert.Throws<ArgumentException>(() => new ProviderAvailability(ProviderAvailabilityState.Unavailable));
        var availability = new ProviderAvailability(
            ProviderAvailabilityState.Unavailable,
            new Failure("provider.offline", "Provider is offline."));

        var copy = RoundTrip(availability);

        Assert.Equal(availability, copy);
    }

    [Fact]
    public void Prepared_execution_contract_round_trips_without_control_contract_dependency()
    {
        var prepared = ContractFixtures.PreparedExecution();
        var copy = RoundTrip(prepared);

        Assert.Equal(prepared.Version, copy.Version);
        Assert.Equal(prepared.PreparedExecutionId, copy.PreparedExecutionId);
        Assert.Equal(prepared.AuthoritySnapshot, copy.AuthoritySnapshot);
        Assert.Equal(prepared.PlanGeneration, copy.PlanGeneration);
        Assert.Equal(prepared.Bindings, copy.Bindings);
    }

    [Fact]
    public void Runtime_prepare_and_commit_results_enforce_structural_outcomes()
    {
        var preparedId = PreparedExecutionId.New();
        var reservationId = Identity.New();
        var prepared = new RuntimePrepareResult(
            RuntimeContractVersion.Current,
            preparedId,
            RuntimePrepareStatus.Prepared,
            reservationId,
            null);

        var preparedCopy = RoundTrip(prepared);
        Assert.Equal(prepared.Version, preparedCopy.Version);
        Assert.Equal(prepared.PreparedExecutionId, preparedCopy.PreparedExecutionId);
        Assert.Equal(prepared.ReservationId, preparedCopy.ReservationId);
        Assert.Equal(prepared.Status, preparedCopy.Status);

        Assert.Throws<ArgumentException>(() => new RuntimePrepareResult(
            RuntimeContractVersion.Current,
            preparedId,
            RuntimePrepareStatus.Prepared,
            null,
            null));

        var committed = new RuntimeCommitResult(
            RuntimeContractVersion.Current,
            RuntimeCommitStatus.Committed,
            ExecutionInstanceId.New(),
            new Revision(1),
            null);
        var committedCopy = RoundTrip(committed);

        Assert.Equal(committed.Version, committedCopy.Version);
        Assert.Equal(committed.ExecutionInstanceId, committedCopy.ExecutionInstanceId);
        Assert.Equal(committed.ExecutionRevision, committedCopy.ExecutionRevision);
        Assert.Equal(committed.Status, committedCopy.Status);
    }

    [Fact]
    public void AI_request_canonicalizes_parameter_order_and_round_trips()
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceCapabilityId.New(),
            ContractFixtures.Frame(),
            new UtcTimestamp(DateTimeOffset.Parse("2026-09-15T12:00:00+00:00")),
            new[]
            {
                new InferenceParameter("zeta", "2"),
                new InferenceParameter("alpha", "1")
            });

        Assert.Equal(new[] { "alpha", "zeta" }, request.Parameters.Select(parameter => parameter.Name));

        var copy = RoundTrip(request);
        Assert.Equal(request.Version, copy.Version);
        Assert.Equal(request.RequestId, copy.RequestId);
        Assert.Equal(request.CapabilityId, copy.CapabilityId);
        Assert.Equal(request.Deadline, copy.Deadline);
        Assert.Equal(request.InputFrame, copy.InputFrame);
        Assert.Equal(request.Parameters, copy.Parameters);
    }

    [Fact]
    public void AI_result_requires_failure_for_non_success_status()
    {
        Assert.Throws<ArgumentException>(() => new GovernedInferenceResult(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceExecutionStatus.Failed,
            Array.Empty<InferenceOutput>(),
            null));

        var result = new GovernedInferenceResult(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            InferenceExecutionStatus.Succeeded,
            new[] { new InferenceOutput("label", "person") },
            null);
        var copy = RoundTrip(result);

        Assert.Equal(result.Version, copy.Version);
        Assert.Equal(result.RequestId, copy.RequestId);
        Assert.Equal(result.Status, copy.Status);
        Assert.Equal(result.Outputs, copy.Outputs);
        Assert.Null(copy.Failure);
    }

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, TransportJson);
        return JsonSerializer.Deserialize<T>(json, TransportJson)
            ?? throw new Xunit.Sdk.XunitException($"JSON round-trip returned null for {typeof(T).FullName}.");
    }

    private static JsonSerializerOptions CreateTransportJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ContractScalarJsonConverterFactory());
        return options;
    }
}

internal sealed class ContractScalarJsonConverterFactory : JsonConverterFactory
{
    private static readonly HashSet<Type> StrongIdentityTypes = new()
    {
        typeof(ProductionId),
        typeof(ProductionSourceId),
        typeof(CommandId),
        typeof(MediaSourceId),
        typeof(MediaSinkId),
        typeof(SurfaceId),
        typeof(ProviderId),
        typeof(CapabilityId),
        typeof(ProviderResourceId),
        typeof(InferenceCapabilityId),
        typeof(InferenceRequestId),
        typeof(PreparedExecutionId),
        typeof(ExecutionInstanceId)
    };

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(Identity) ||
        typeToConvert == typeof(Revision) ||
        typeToConvert == typeof(Generation) ||
        typeToConvert == typeof(UtcTimestamp) ||
        typeToConvert == typeof(Duration) ||
        typeToConvert == typeof(Rational) ||
        typeToConvert == typeof(FrameRate) ||
        typeToConvert == typeof(Timebase) ||
        typeToConvert == typeof(CompatibilityVersion) ||
        typeToConvert == typeof(Failure) ||
        StrongIdentityTypes.Contains(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (StrongIdentityTypes.Contains(typeToConvert))
        {
            var converterType = typeof(StrongIdentityJsonConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)(Activator.CreateInstance(converterType)
                ?? throw new InvalidOperationException($"Could not create converter for {typeToConvert}."));
        }

        if (typeToConvert == typeof(Identity)) return new CanonicalStringJsonConverter<Identity>(Identity.Parse, value => value.ToString());
        if (typeToConvert == typeof(Revision)) return new CanonicalStringJsonConverter<Revision>(Revision.Parse, value => value.ToString());
        if (typeToConvert == typeof(Generation)) return new CanonicalStringJsonConverter<Generation>(Generation.Parse, value => value.ToString());
        if (typeToConvert == typeof(UtcTimestamp)) return new CanonicalStringJsonConverter<UtcTimestamp>(UtcTimestamp.Parse, value => value.ToString());
        if (typeToConvert == typeof(Duration)) return new CanonicalStringJsonConverter<Duration>(Duration.Parse, value => value.ToString());
        if (typeToConvert == typeof(Rational)) return new CanonicalStringJsonConverter<Rational>(Rational.Parse, value => value.ToString());
        if (typeToConvert == typeof(FrameRate)) return new CanonicalStringJsonConverter<FrameRate>(FrameRate.Parse, value => value.ToString());
        if (typeToConvert == typeof(Timebase)) return new CanonicalStringJsonConverter<Timebase>(Timebase.Parse, value => value.ToString());
        if (typeToConvert == typeof(CompatibilityVersion)) return new CanonicalStringJsonConverter<CompatibilityVersion>(CompatibilityVersion.Parse, value => value.ToString());
        if (typeToConvert == typeof(Failure)) return new FailureJsonConverter();

        throw new NotSupportedException($"No contract scalar converter is registered for {typeToConvert}.");
    }
}

internal sealed class CanonicalStringJsonConverter<T> : JsonConverter<T>
{
    private readonly Func<string, T> _parse;
    private readonly Func<T, string> _format;

    public CanonicalStringJsonConverter(Func<string, T> parse, Func<T, string> format)
    {
        _parse = parse;
        _format = format;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeof(T).Name} must be encoded as a canonical string.");

        var value = reader.GetString() ?? throw new JsonException($"{typeof(T).Name} must not be null.");
        try
        {
            return _parse(value);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw new JsonException($"Invalid canonical {typeof(T).Name} value.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(_format(value));
}

internal sealed class StrongIdentityJsonConverter<T> : JsonConverter<T>
    where T : struct
{
    private static readonly ConstructorInfo Constructor = typeof(T).GetConstructor(new[] { typeof(Identity) })
        ?? throw new InvalidOperationException($"{typeof(T).Name} must expose a public constructor accepting Identity.");

    private static readonly PropertyInfo ValueProperty = typeof(T).GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{typeof(T).Name} must expose a public Value property.");

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeof(T).Name} must be encoded as a canonical GUID string.");

        var text = reader.GetString();
        if (!Identity.TryParse(text, out var identity))
            throw new JsonException($"{typeof(T).Name} must contain a non-empty GUID in D format.");

        return (T)(Constructor.Invoke(new object[] { identity })
            ?? throw new JsonException($"Could not construct {typeof(T).Name}."));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var identity = ValueProperty.GetValue(value) is Identity typedIdentity
            ? typedIdentity
            : throw new JsonException($"{typeof(T).Name}.Value must be an Identity.");

        writer.WriteStringValue(identity.ToString());
    }
}

internal sealed class FailureJsonConverter : JsonConverter<Failure>
{
    public override Failure Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("code", out var codeProperty) ||
            !root.TryGetProperty("message", out var messageProperty))
        {
            throw new JsonException("Failure must contain code and message properties.");
        }

        try
        {
            return new Failure(
                codeProperty.GetString() ?? throw new JsonException("Failure code must not be null."),
                messageProperty.GetString() ?? throw new JsonException("Failure message must not be null."));
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Failure code and message must be non-empty.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, Failure value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("code", value.Code);
        writer.WriteString("message", value.Message);
        writer.WriteEndObject();
    }
}

internal static class ContractFixtures
{
    public static FrameDescriptor Frame()
    {
        var surface = new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(new Generation(5), Identity.New()),
            new OpaqueSurfaceHandle("fixture", "surface-001"));

        return new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            surface,
            new FrameTiming(7, 12_600, new Timebase(1, 90_000)));
    }

    public static ProviderDescriptor Provider()
    {
        var providerId = ProviderId.New();
        var capability = new ProviderCapabilityDescriptor(
            CapabilityId.New(),
            "video.source",
            new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 });
        var resource = new ProviderResourceDescriptor(
            ProviderResourceId.New(),
            providerId,
            "video.source",
            2,
            true);

        return new ProviderDescriptor(
            ProviderContractVersion.Current,
            providerId,
            "Fixture Provider",
            new ProviderAvailability(ProviderAvailabilityState.Available),
            new[] { capability },
            new[] { resource });
    }

    public static PreparedExecutionContract PreparedExecution()
    {
        var provider = Provider();
        var capability = provider.Capabilities[0];
        var binding = new PreparedExecutionBinding(
            Identity.New(),
            capability.CapabilityId,
            provider.Resources[0],
            MediaSourceId.New(),
            null);

        return new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(3)),
            new Generation(2),
            new[] { binding });
    }
}
