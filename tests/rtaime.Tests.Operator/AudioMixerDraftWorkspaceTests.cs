// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Operator;
using rtaime.Core;
using rtaime.Client;
using rtaime.Media.Contracts;
using Xunit;
namespace rtaime.Tests.Operator;

public sealed partial class AudioMixerMutationTests
{
    [Fact]
    public async Task Mixer_mute_mode_and_routing_are_one_local_draft_until_apply()
    {
        var transport = new AudioTransport();
        await using var owner = new OperatorViewModel(new OperatorControlClient(transport), runtimeReadiness: new ReadyService());
        owner.SynchronizeCommand.Execute(null);
        var source = owner.AudioMixer.Sources[0];
        var route = Assert.Single(source.Assignments);
        source.ToggleMuteCommand.Execute(null);
        source.ToggleContributionCommand.Execute(null);
        route.ToggleCommand.Execute(null);
        Assert.True(source.Muted); Assert.False(source.FollowRoutedSource);
        Assert.True(route.IsAssigned); Assert.False(route.DraftAssigned);
        Assert.Equal("DRAFT", route.State);
        Assert.Equal(0, transport.MutationCalls);
        Assert.False(transport.Configuration.Sources[0].Muted);
        Assert.True(await owner.AudioMixer.ApplySourceAsync(source));
        Assert.True(transport.Configuration.Sources[0].Muted);
        Assert.Empty(transport.Configuration.Sources[0].BusAssignments);
        Assert.Equal("CONFIRMED", source.DraftState);
        Assert.False(route.IsAssigned); Assert.False(route.HasDraftChanges);
        source.ToggleMuteCommand.Execute(null);
        route.ToggleCommand.Execute(null);
        source.DiscardDraftCommand.Execute(null);
        Assert.True(source.Muted); Assert.False(route.DraftAssigned);
        Assert.Equal(1, transport.MutationCalls);
    }

    [Fact]
    public async Task Mixer_precision_clipboard_and_invalid_input_preserve_explicit_commit_boundary()
    {
        var transport = new AudioTransport();
        await using var owner = new OperatorViewModel(new OperatorControlClient(transport), runtimeReadiness: new ReadyService());
        owner.SynchronizeCommand.Execute(null);
        var source = owner.AudioMixer.Sources[0]; var other = owner.AudioMixer.Sources[1];
        source.GainDb = -6.020599913279624;
        Assert.Equal(0.5, source.Gain, 8);
        var payload = Assert.IsType<InspectorAttributePayload>(source.CopyGain());
        Assert.True(other.PasteGain(payload)); Assert.Equal(0.5, other.Gain, 8);
        Assert.False(other.PasteGain(payload with { RevisionToken = "stale" }));
        Assert.False(other.PasteGain(payload with { Values = new InspectorGradeAttributes(true, 0, 1, 1) }));
        Assert.False(other.PasteGain(payload with { Values = new InspectorGainAttributes(double.NaN) }));
        other.UnityDraft(); Assert.Equal(1, other.Gain);
        source.InputsValid = false;
        Assert.False(source.ApplyCommand.CanExecute(null));
        Assert.False(await owner.AudioMixer.ApplySourceAsync(source));
        Assert.Equal(0, transport.MutationCalls);
        source.InputsValid = true;
        Assert.True(await owner.AudioMixer.ApplySourceAsync(source));
        Assert.Equal(0.5, transport.Configuration.Sources[0].Gain, 8);
    }
}

public sealed partial class AudioMixerOperatorSurfaceTests
{
    [Theory]
    [InlineData(0, "−∞", true)]
    [InlineData(0.5, "-6.0", true)]
    [InlineData(double.NaN, "—", false)]
    [InlineData(double.PositiveInfinity, "—", false)]
    [InlineData(-1, "—", false)]
    public async Task Mixer_meter_invalid_values_never_become_live_silence(double peak, string label, bool hasValue)
    {
        await using var owner = new OperatorViewModel(runtimeReadiness: new PassiveReadinessService());
        var id = new MediaSourceId(Identity.New());
        var config = new AudioProductionConfiguration(1, [new(AudioBusId.Program, 1, false)],
            [new(id, 1, false, true, [AudioBusId.Program])]);
        var buses = new[] { new OperatorAudioProductionBusDescriptor("program", 1, false, peak, peak, peak, false, 0, 1, 0) };
        owner.AudioMixer.ApplyProjection(Descriptor(config,buses), [new(id.ToString(), "Real source identity")],
            [new(id.ToString(), "stream",1,false,peak,peak,peak,false,"HEALTHY")], [], true);
        var strip = Assert.Single(owner.AudioMixer.Sources);
        Assert.Equal(hasValue, strip.HasMeterEvidence);
        Assert.Equal(label, strip.LeftPeakLabel);
        Assert.True(double.IsFinite(strip.DisplayLeftPeak));
        Assert.Equal(hasValue, owner.AudioMixer.Buses[0].HasMeterEvidence);
        owner.AudioMixer.SetEvidenceAvailability("STALE");
        Assert.Equal("—", strip.LeftPeakLabel); Assert.Equal("STALE", strip.RuntimeState);
    }
}
