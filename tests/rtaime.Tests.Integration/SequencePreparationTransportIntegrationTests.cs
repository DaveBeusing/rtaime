// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using Grpc.Core;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.ControlHost;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed partial class ExternalControlIntegrationTests
{
    private static SequenceDefinition PreparedVideoSequence(MediaAssetDescriptor asset)
    {
        var track = new SequenceTrack(Guid.NewGuid(), "Video", SequenceMediaKind.Video, 0);
        var clip = new SequenceClip(Guid.NewGuid(), track.TrackId,
            new(Guid.Parse(asset.AssetId.ToString()), SequenceMediaKind.Video,
                asset.FingerprintSha256), 0, 4 * 4800, 0, 4 * 4800);
        return new SequenceDefinition(SequenceContract.Version, Guid.NewGuid(), "Native preview preparation",
            new(50, 1), [track], [clip], []);
    }

    private static async Task CommitSequencePreparationAuthorityAsync(GrpcOperatorControlTransport transport)
    {
        var snapshot = await transport.GetSnapshotAsync();
        var selected = new ProductionSourceId(Identity.Parse(snapshot.Sources[1].Id));
        var result = await transport.SelectPreviewAsync(new SelectPreviewCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                snapshot.Production.ProductionId, snapshot.Production.Revision), selected));
        Assert.True(result.Accepted);
        Assert.True(result.State.Revision.Value > 0);
    }

    [Fact]
    public async Task Real_native_sequence_prepare_get_cancel_roundtrips_client_Control_and_Runtime_without_Program_mutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var grpcTransport = fixture.CreateClient();
        await CommitSequencePreparationAuthorityAsync(grpcTransport);
        var before = await grpcTransport.GetSnapshotAsync();
        var imported = await grpcTransport.ImportMediaAssetsAsync([asset.Path]);
        Assert.True(imported.Succeeded);
        var media = Assert.Single(imported.Snapshot.Assets);
        var sequence = PreparedVideoSequence(media);
        var authoring = new SequenceAuthoringClient(grpcTransport);
        var saved = await authoring.SaveAsync(new(
            SequenceContract.Version, Guid.NewGuid(), 0, sequence.SequenceId, sequence));
        Assert.True(saved.Saved, string.Join("; ", saved.Issues.Select(i => i.Message)));
        var external = new SequencePreparationClient(grpcTransport);
        var context = await external.ContextAsync();
        Assert.Equal(saved.AuthoringRevision, context.AuthoringRevision);
        var expectedHost = context.RuntimeHostInstanceId;
        var local = new SequencePreparationClient(
            new NamedPipeOperatorControlTransport(fixture.ControlEndpoint));
        var admitted = await external.PrepareAsync(new(SequencePreparationContract.Version,
            sequence.SequenceId, saved.AuthoringRevision, expectedHost));
        Assert.Equal("PREPARED", admitted.State);
        Assert.Equal(1, admitted.DecoderOwners);
        var reference = new SequencePreparationReference(admitted.Version,
            admitted.SequenceId, admitted.AuthoringRevision, admitted.RuntimeHostInstanceId,
            admitted.PreparedExecutionId, admitted.Generation);
        reference.Validate();
        var inspected = await local.GetAsync(reference);
        Assert.Equal("PREPARED", inspected.State);
        Assert.Equal(admitted.PreparedExecutionId, inspected.PreparedExecutionId);
        Assert.Equal("UNAVAILABLE", (await authoring.GetAsync(sequence.SequenceId)).PlaybackState);
        Assert.Equal(before.Production, (await grpcTransport.GetSnapshotAsync()).Production);

        var canceled = await local.CancelAsync(reference);
        Assert.Equal("CANCELED", canceled.State);
        Assert.Equal(0, canceled.DecoderOwners);
        Assert.Equal(before.Production, (await grpcTransport.GetSnapshotAsync()).Production);
        await Assert.ThrowsAnyAsync<Exception>(async () => await external.GetAsync(reference));
    }

    [Fact]
    public async Task Sequence_prepare_rejects_stale_Runtime_and_authoring_revisions_without_native_admission()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var external = fixture.CreateClient();
        await CommitSequencePreparationAuthorityAsync(external);
        var imported = await external.ImportMediaAssetsAsync([asset.Path]);
        Assert.True(imported.Succeeded);
        var media = Assert.Single(imported.Snapshot.Assets);
        var sequence = PreparedVideoSequence(media);
        var authoring = new SequenceAuthoringClient(external);
        var saved = await authoring.SaveAsync(new(
            SequenceContract.Version, Guid.NewGuid(), 0, sequence.SequenceId, sequence));
        Assert.True(saved.Saved);
        var preparation = new SequencePreparationClient(external);
        var context = await preparation.ContextAsync();
        Assert.Equal(saved.AuthoringRevision, context.AuthoringRevision);
        var hostId = context.RuntimeHostInstanceId;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await preparation.PrepareAsync(new(SequencePreparationContract.Version,
                sequence.SequenceId, saved.AuthoringRevision, Guid.NewGuid().ToString())));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await preparation.PrepareAsync(new(SequencePreparationContract.Version,
                sequence.SequenceId, saved.AuthoringRevision + 1, hostId)));

        var admitted = await preparation.PrepareAsync(new(SequencePreparationContract.Version,
            sequence.SequenceId, saved.AuthoringRevision, hostId));
        Assert.Equal("PREPARED", admitted.State);
        var reference = new SequencePreparationReference(admitted.Version,
            admitted.SequenceId, admitted.AuthoringRevision, admitted.RuntimeHostInstanceId,
            admitted.PreparedExecutionId, admitted.Generation);

        var edited = await authoring.SaveAsync(new(
            SequenceContract.Version, Guid.NewGuid(), saved.AuthoringRevision,
            sequence.SequenceId, sequence with { Name = "Edited while prepared" }));
        Assert.True(edited.Saved);
        var stale = await preparation.GetAsync(reference);
        Assert.Equal("STALE", stale.State);
        Assert.Equal(0, stale.DecoderOwners);
        Assert.Equal("UNAVAILABLE", (await authoring.GetAsync(sequence.SequenceId)).PlaybackState);
        Assert.Equal(edited.AuthoringRevision, (await authoring.ListAsync()).AuthoringRevision);
    }

    [Fact]
    public async Task Removing_catalogued_media_invalidates_native_sequence_preparation_without_changing_Program()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var external = fixture.CreateClient();
        await CommitSequencePreparationAuthorityAsync(external);
        var before = await external.GetSnapshotAsync();
        var imported = await external.ImportMediaAssetsAsync([asset.Path]);
        Assert.True(imported.Succeeded);
        var media = Assert.Single(imported.Snapshot.Assets);
        var sequence = PreparedVideoSequence(media);
        var authoring = new SequenceAuthoringClient(external);
        var saved = await authoring.SaveAsync(new(
            SequenceContract.Version, Guid.NewGuid(), 0, sequence.SequenceId, sequence));
        Assert.True(saved.Saved);

        var preparation = new SequencePreparationClient(external);
        var context = await preparation.ContextAsync();
        var admitted = await preparation.PrepareAsync(new(SequencePreparationContract.Version,
            sequence.SequenceId, saved.AuthoringRevision, context.RuntimeHostInstanceId));
        Assert.Equal("PREPARED", admitted.State);
        var reference = new SequencePreparationReference(admitted.Version, admitted.SequenceId,
            admitted.AuthoringRevision, admitted.RuntimeHostInstanceId,
            admitted.PreparedExecutionId, admitted.Generation);

        // Catalogue identity changes independently of sequence authoring revision.
        var removed = await external.RemoveMediaAssetAsync(media.AssetId);
        Assert.True(removed.Succeeded);
        Assert.Equal(saved.AuthoringRevision, (await authoring.ListAsync()).AuthoringRevision);
        var stale = await preparation.GetAsync(reference);
        Assert.Equal("STALE", stale.State);
        Assert.Equal(0, stale.DecoderOwners);
        Assert.Equal(before.Production, (await external.GetSnapshotAsync()).Production);
        await Assert.ThrowsAnyAsync<Exception>(async () => await preparation.GetAsync(reference));
    }

    [Fact]
    public async Task Sequence_preparation_requires_Operator_role_and_does_not_advertise_playback()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer);
        await using var observer = fixture.CreateClient();
        var discovered = await observer.DiscoverAsync();
        Assert.Contains("control.sequence.prepare", discovered.Capabilities);
        Assert.Contains("control.sequence.preparation.get", discovered.Capabilities);
        Assert.Contains("control.sequence.preparation.context.get", discovered.Capabilities);
        Assert.DoesNotContain(discovered.Capabilities, c => c.StartsWith("control.sequence.play"));
        var preparation = new SequencePreparationClient(observer);
        var context = await preparation.ContextAsync();
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await preparation.PrepareAsync(new(SequencePreparationContract.Version,
                Guid.NewGuid(), 1, context.RuntimeHostInstanceId)));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }
}
