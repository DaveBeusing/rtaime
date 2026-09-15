using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class V1TransitionContractTests
{
    private static readonly JsonSerializerOptions TransportJson = CreateOptions();

    [Fact]
    public void Dissolve_command_requires_production_duration_and_round_trips()
    {
        var metadata = new ControlCommandMetadata(
            ControlContractVersion.Current,
            CommandId.New(),
            ProductionId.New(),
            new Revision(7));
        var source = ProductionSourceId.New();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DissolveProgramCommand(metadata, source, 1));

        var command = new DissolveProgramCommand(metadata, source, 12);
        var json = JsonSerializer.Serialize(command, TransportJson);
        var copy = JsonSerializer.Deserialize<DissolveProgramCommand>(json, TransportJson);

        Assert.NotNull(copy);
        Assert.Equal(command.Metadata.CommandId, copy.Metadata.CommandId);
        Assert.Equal(command.Metadata.ExpectedRevision, copy.Metadata.ExpectedRevision);
        Assert.Equal(source, copy.SourceId);
        Assert.Equal(12u, copy.DurationFrames);
    }

    [Fact]
    public void Runtime_transition_intent_is_versioned_and_fail_closed()
    {
        var from = MediaSourceId.New();
        var to = MediaSourceId.New();

        Assert.Throws<NotSupportedException>(() => new RuntimeProgramTransitionIntent(
            new CompatibilityVersion(99, 0),
            RuntimeProgramTransitionKind.Cut,
            from,
            to,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeProgramTransitionIntent.Dissolve(from, to, 1));
        Assert.Throws<ArgumentException>(() => RuntimeProgramTransitionIntent.Cut(from, from));

        var intent = RuntimeProgramTransitionIntent.Dissolve(from, to, 8);
        var json = JsonSerializer.Serialize(intent, TransportJson);
        var copy = JsonSerializer.Deserialize<RuntimeProgramTransitionIntent>(json, TransportJson);

        Assert.NotNull(copy);
        Assert.Equal(RuntimeProgramTransitionKind.Dissolve, copy.Kind);
        Assert.Equal(from, copy.FromSourceId);
        Assert.Equal(to, copy.ToSourceId);
        Assert.Equal(8u, copy.DurationFrames);
        Assert.Equal(RuntimeContractVersion.Current, copy.Version);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ContractScalarJsonConverterFactory());
        return options;
    }
}
