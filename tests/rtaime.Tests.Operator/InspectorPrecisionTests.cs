// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class InspectorPrecisionTests
{
	[Theory]
	[InlineData("NaN")]
	[InlineData("Infinity")]
	[InlineData("1e999")]
	[InlineData("")]
	[InlineData("-0.01")]
	[InlineData("1.001")]
	[InlineData("0,125")]
	public void Invalid_numeric_input_is_rejected_without_clamping(string text) =>
		Assert.False(InspectorProperties.LayerPositionX.TryParse(text, CultureInfo.InvariantCulture, out _));

	[Theory]
	[InlineData("en-US", "0.123456789012345", 0.123456789012345)]
	[InlineData("de-DE", "0,123456789012345", 0.123456789012345)]
	[InlineData("en-US", "1e-6", 0.000001)]
	[InlineData("en-US", "0", 0)]
	[InlineData("en-US", "1", 1)]
	public void Typed_precision_and_current_culture_are_preserved(string culture, string text, double expected)
	{
		Assert.True(InspectorProperties.LayerPositionX.TryParse(text, CultureInfo.GetCultureInfo(culture), out var value));
		Assert.Equal(expected, value);
	}

	[Fact]
	public void Fine_coarse_integer_and_range_steps_follow_shared_metadata()
	{
		Assert.Equal(0.5001, InspectorProperties.LayerPositionX.Adjust(0.5, 1, true, false), 10);
		Assert.Equal(0.51, InspectorProperties.LayerPositionX.Adjust(0.5, 1, false, true), 10);
		Assert.Equal(1, InspectorProperties.LayerPositionX.Adjust(1, 10, false, false));
		Assert.Equal(0, InspectorProperties.KeyRed.Adjust(0, -1, true, false));
		Assert.Equal(1, InspectorProperties.KeyRed.Adjust(0, 1, true, false));
		Assert.False(InspectorProperties.KeyRed.Accepts(1.5));
		Assert.False(InspectorProperties.LayerScale.Accepts(double.NaN));
	}

	[Fact]
	public async Task Compatible_copy_paste_and_refresh_change_only_draft_until_explicit_confirmation()
	{
		var transport = new InspectorTransport();
		var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var inspector = new MediaPoolInspectorViewModel(owner, deck);
		owner.SynchronizeCommand.Execute(null);
		inspector.SelectCompositingNode(Node("layer:bitmap-graphics"));
		var program = owner.ProgramSourceId;
		inspector.SelectedLayerPositionX = 0.6789012345;
		inspector.CopyAttributesCommand.Execute(null);
		owner.SynchronizeCommand.Execute(null);
		Assert.Equal(0.6789012345, inspector.SelectedLayerPositionX);
		Assert.Equal(0.1, owner.CompositingLayers[0].PositionX);
		Assert.Contains("DRAFT", inspector.InspectorEditState);
		inspector.SelectedLayerPositionX = 0.2;
		Assert.True(inspector.PasteAttributesCommand.CanExecute(null));
		inspector.PasteAttributesCommand.Execute(null);
		Assert.Equal(0.6789012345, inspector.SelectedLayerPositionX);
		Assert.Equal(0, transport.MutationCalls);
		Assert.Equal(program, owner.ProgramSourceId);

		transport.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		inspector.ApplyCompositingTransformCommand.Execute(null);
		Assert.True(owner.IsBusy);
		Assert.False(inspector.PasteAttributesCommand.CanExecute(null));
		Assert.Contains("PENDING", inspector.InspectorEditState);
		Assert.Equal(0.1, owner.CompositingLayers[0].PositionX);
		transport.Hold.SetResult();
		await UntilAsync(() => !owner.IsBusy);
		Assert.Equal(0.6789012345, owner.CompositingLayers[0].PositionX);
		Assert.Equal(1, transport.MutationCalls);
		Assert.Equal(program, owner.ProgramSourceId);
	}

	[Fact]
	public async Task Incompatible_stale_invalid_and_rejected_edits_never_become_confirmed_truth()
	{
		var transport = new InspectorTransport();
		var client = new OperatorControlClient(transport);
		await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
		await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
		using var inspector = new MediaPoolInspectorViewModel(owner, deck);
		owner.SynchronizeCommand.Execute(null);
		inspector.SelectCompositingNode(Node("layer:bitmap-graphics"));
		inspector.CopyAttributesCommand.Execute(null);
		inspector.SelectCompositingNode(Node("processing:bitmap-graphics:grade"));
		Assert.False(inspector.PasteAttributesCommand.CanExecute(null));
		inspector.PasteAttributesCommand.Execute(null);
		Assert.Equal(0, transport.MutationCalls);
		Assert.Contains("Incompatible", inspector.AttributeCompatibilityLabel);

		inspector.SelectCompositingNode(Node("layer:bitmap-graphics"));
		inspector.InspectorInputsValid = false;
		Assert.False(inspector.ApplyCompositingTransformCommand.CanExecute(null));
		Assert.False(inspector.PasteAttributesCommand.CanExecute(null));
		inspector.InspectorInputsValid = true;
		transport.FailSnapshots = true;
		owner.SynchronizeCommand.Execute(null);
		Assert.True(owner.IsStale);
		Assert.False(inspector.ApplyCompositingTransformCommand.CanExecute(null));
		transport.FailSnapshots = false;
		owner.SynchronizeCommand.Execute(null);
		inspector.SelectedLayerPositionX = 0.8;
		transport.Reject = true;
		inspector.ApplyCompositingTransformCommand.Execute(null);
		await UntilAsync(() => !owner.IsBusy);
		Assert.Equal(0.1, owner.CompositingLayers[0].PositionX);
		Assert.Equal(0.8, inspector.SelectedLayerPositionX);
		Assert.Contains("REJECTED", inspector.InspectorEditState);
	}


    [Fact]
    public async Task Compositing_refresh_preserves_graph_objects_geometry_selection_and_dirty_processing_values()
    {
        var transport = new InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        var selected = graph.Nodes.Single(node => node.Id == "processing:bitmap-graphics:grade");
        graph.SelectNodeCommand.Execute(selected);
        inspector.SelectedColorGradeBrightness = 0.314159265;
        var nodes = graph.Nodes.ToDictionary(node => node.Id);
        var positions = graph.Nodes.ToDictionary(node => node.Id, node => (node.X, node.Y));
        var geometry = graph.Connections.ToDictionary(connection => connection.Id, connection => connection.Geometry);
        var resets = 0;
        graph.Nodes.CollectionChanged += (_, change) => { if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        for (var refresh = 0; refresh < 20; refresh++) owner.SynchronizeCommand.Execute(null);
        Assert.Same(selected, graph.SelectedNode);
        Assert.Equal(0.314159265, inspector.SelectedColorGradeBrightness);
        foreach (var node in graph.Nodes) { Assert.Same(nodes[node.Id], node); Assert.Equal(positions[node.Id], (node.X, node.Y)); }
        foreach (var connection in graph.Connections) Assert.Same(geometry[connection.Id], connection.Geometry);
        Assert.Equal(0, resets);
        Assert.Equal(0, transport.MutationCalls);
        Assert.Same(owner.ActivateSceneCommand, graph.RecallSceneCommand);
    }

    [Fact]
    public async Task Compositing_maximum_layer_projection_filters_collapses_and_reviews_without_batch_mutations()
    {
        var transport = new InspectorTransport { ManyLayers = true };
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        Assert.Equal(8, graph.Layers.Count);
        graph.UpdateViewport(300, 220);
        Assert.True(graph.VisibleNodes.Count < graph.Nodes.Count);
        var first = graph.Layers[0];
        graph.SelectNodeCommand.Execute(first);
        graph.SearchText = "layer7";
        Assert.Same(first, graph.SelectedNode);
        Assert.False(first.MatchesSearch);
        graph.SearchText = "";
        first.IsCollapsed = true;
        graph.CollapseLayerCommand.Execute(first);
        Assert.False(first.IsCollapsed);
        graph.SelectLayers(graph.Layers.Take(2).ToArray());
        Assert.True(inspector.HasMultipleSelection);
        Assert.Equal(2, inspector.SelectionCount);
        Assert.Contains("review", graph.SelectionEvidence);
        Assert.False(inspector.ApplyCompositingTransformCommand.CanExecute(null));
        Assert.False(graph.ApplyLayerDraftCommand.CanExecute(null));
        Assert.Equal(0, transport.MutationCalls);
        graph.SelectNodeCommand.Execute(first);
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(node => node.Id == "transform:layer0"));
        Assert.Same(first, graph.SelectedLayerRow);
        Assert.Equal(1, graph.SelectedLayerCount);
        inspector.SelectedLayerPositionX = 0.42;
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(node => node.Id == "transform:layer7"));
        Assert.Contains("transform:layer0:", inspector.CompositingRecoveryStatus);
        Assert.DoesNotContain("transform:layer7:", inspector.CompositingRecoveryStatus);
        Assert.Equal(0.1, inspector.SelectedLayerPositionX);
        Assert.False(first.IsSelected);
        Assert.Equal(1, graph.SelectedLayerCount);
        graph.StageLayerOrder("layer7", "layer0");
        Assert.Equal("layer:layer7", graph.Layers[0].Id);
        Assert.Equal("layer0", owner.CompositingLayers[0].LayerId);
        Assert.Contains("DRAFT", graph.OrderEvidence);
        graph.DiscardOrderDraftCommand.Execute(null);
        Assert.Equal("layer:layer0", graph.Layers[0].Id);
        graph.ZoomAt(1.12, 150, 110); graph.PanBy(-40, -20); graph.Fit();
        Assert.Equal(0, transport.MutationCalls);
    }

    [Fact]
    public async Task Compositing_visibility_opacity_and_order_are_local_until_apply_and_rejected_drafts_survive()
    {
        var transport = new InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(node => node.Id == "layer:bitmap-graphics"));
        graph.SelectedLayerRow!.OpacityDraft = 96;
        graph.SelectedLayerRow.VisibleDraft = false;
        Assert.Equal(255, owner.CompositingLayers[0].Opacity);
        Assert.Equal(0, transport.MutationCalls);
        graph.LayerDraftInputsValid = false;
        Assert.False(graph.ApplyLayerDraftCommand.CanExecute(null));
        graph.LayerDraftInputsValid = true;
        transport.Reject = true;
        graph.ApplyLayerDraftCommand.Execute(null);
        await UntilAsync(() => !owner.IsBusy);
        Assert.Equal(96, graph.SelectedLayerRow.OpacityDraft);
        Assert.Equal(255, owner.CompositingLayers[0].Opacity);
        Assert.Contains("REJECTED", graph.LayerCommandStatus);
        transport.Reject = false;
        graph.ApplyLayerDraftCommand.Execute(null);
        await UntilAsync(() => !owner.IsBusy);
        Assert.Equal(96, owner.CompositingLayers[0].Opacity);
        Assert.False(owner.CompositingLayers[0].Visible);
        Assert.False(graph.SelectedLayerRow.HasStateDraft);
    }

    [Fact]
    public async Task Compositing_missing_bitmap_retains_selection_and_dirty_intent_until_identity_returns()
    {
        var transport = new InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(node => node.Id == "layer:bitmap-graphics"));
        graph.SelectedLayerRow!.OpacityDraft = 96;
        transport.OmitBitmap = true;
        owner.SynchronizeCommand.Execute(null);
        Assert.Null(graph.SelectedNode);
        Assert.Contains("opacity=96", graph.WorkspaceNotice);
        Assert.False(graph.ApplyLayerDraftCommand.CanExecute(null));
        owner.SynchronizeCommand.Execute(null);
        transport.OmitBitmap = false;
        owner.SynchronizeCommand.Execute(null);
        Assert.Equal("layer:bitmap-graphics", graph.SelectedNode!.Id);
        Assert.Contains("opacity=96", graph.WorkspaceNotice);
        Assert.Equal(0, transport.MutationCalls);
    }

    [Fact]
    public async Task Compositing_stale_revision_retains_intent_for_explicit_matching_recovery_and_local_undo()
    {
        var transport = new InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(node => node.Id == "transform:bitmap-graphics"));
        inspector.SelectedLayerPositionX = 0.72;
        transport.FailSnapshots = true;
        owner.SynchronizeCommand.Execute(null);
        Assert.True(owner.IsStale);
        Assert.Contains("RECOVERY", inspector.CompositingRecoveryStatus);
        Assert.False(inspector.RestoreCompositingIntentCommand.CanExecute(null));
        transport.FailSnapshots = false; transport.RevisionValue++;
        owner.SynchronizeCommand.Execute(null);
        Assert.True(inspector.RestoreCompositingIntentCommand.CanExecute(null));
        inspector.RestoreCompositingIntentCommand.Execute(null);
        Assert.Equal(0.72, inspector.SelectedLayerPositionX);
        Assert.Equal(0.1, owner.CompositingLayers[0].PositionX);
        inspector.UndoInspectorDraftCommand.Execute(null);
        Assert.Equal(0.1, inspector.SelectedLayerPositionX);
        Assert.Equal(0, transport.MutationCalls);
    }

	internal static async Task VerifyWindowAsync(MainWindow owner, Dispatcher dispatcher)
	{
		owner.Shell.SelectWorkspace("MEDIA");
		if (owner.Shell.IsRightCollapsed) owner.Shell.ToggleRightPanelCommand.Execute(null);
		await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
		var inspector = Descendants(owner).OfType<OperatorInspectorControl>().First(control => control.IsVisible);
		var surface = Assert.IsType<Grid>(Assert.IsType<Border>(inspector.Content).Child);
		var field = new RtaimeNumericField
		{
			Definition = InspectorProperties.LayerPositionX, Value = 0.5,
			Width = 180, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
		};
		Panel.SetZIndex(field, 100);
		var preview = ((OperatorViewModel)owner.DataContext).PreviewSourceId;
		var program = ((OperatorViewModel)owner.DataContext).ProgramSourceId;
		surface.Children.Add(field);
		try
		{
			await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
			field.ApplyTemplate();
			Assert.NotNull(field.Template.FindName("PART_ContentHost", field));
			Assert.True(field.Focus());
			Assert.Same(field, Keyboard.FocusedElement);
			var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field), 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			field.RaiseEvent(up);
			Assert.True(up.Handled);
			Assert.Equal(0.501, field.Value, 10);
			var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			field.RaiseEvent(down);
			Assert.Equal(0.5, field.Value, 10);
			field.Text = "NaN";
			Assert.True(field.HasInputError);
			Assert.False(inspector.NumericInputsValid);
			Assert.Equal(0.5, field.Value);
			var fullscreen = owner.Shell.IsFullscreen;
			var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
			field.RaiseEvent(escape);
			Assert.True(escape.Handled);
			Assert.Equal(fullscreen, owner.Shell.IsFullscreen);
			Assert.True(inspector.NumericInputsValid);
			field.Adjust(1, fine: true);
			Assert.Equal(0.5001, field.Value, 10);
			field.Text = 0.123456789012345.ToString("G17", CultureInfo.CurrentCulture);
			Assert.Equal(0.123456789012345, field.Value);
			field.ResetDraft();
			Assert.Equal(0, field.Value);
			Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
			Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
			Console.WriteLine("PRECISION INSPECTOR PASS: real custom template, validation/apply gate, fine steps, typed precision, reset and unchanged source identities.");
		}
		finally { surface.Children.Remove(field); }
	}
	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i); yield return child;
			foreach (var descendant in Descendants(child)) yield return descendant;
		}
	}
	private static CompositingGraphNodeProjection Node(string id) =>
		new(id, CompositingGraphNodeKind.Layer, id, "", "CONFIRMED", CompositingGraphHealth.Normal, []);
	private static async Task UntilAsync(Func<bool> condition)
	{
		var end = DateTime.UtcNow.AddSeconds(5);
		while (!condition() && DateTime.UtcNow < end) await Task.Delay(10);
		Assert.True(condition());
	}
	internal sealed class InspectorTransport : IOperatorControlTransport
	{
		private readonly ProductionSourceId _source = ProductionSourceId.New();
		private readonly ProductionId _production = ProductionId.New();
		private OperatorCompositingLayerDescriptor _layer = new("bitmap-graphics", 2, 0, true, 255, 0.1, 0.2, 1, "logo.rgba",
			processingStack: [new("grade", 1, true, colorGrade: new(0, 1, 1)), new("key", 2, true, chromaKey: new(0, 255, 0, 0.1, 0.1, 0))]);
		internal int MutationCalls;
        internal int RejectMutationCall;
        internal Action<int>? OnMutation;
		internal TaskCompletionSource? Hold;
		internal bool Reject, FailSnapshots, ManyLayers, RemoteCut, OmitSecondCue, OmitBitmap;
        internal ulong RevisionValue = 1;
        internal string SourceId => _source.ToString();
        internal readonly ProductionSourceId OtherSource = ProductionSourceId.New();
        internal Dictionary<string, double> Gains = new(StringComparer.Ordinal);
        internal string[] CueNames = ["Same name", "Same name"];
        internal static readonly MediaAssetId Asset = new(Identity.Parse("45000000-0000-0000-0000-000000000001"));
        internal static readonly MediaCuePointId[] CueIds = [
            new(Identity.Parse("45000000-0000-0000-0000-000000000003")), new(Identity.Parse("45000000-0000-0000-0000-000000000004"))];
        internal MediaDeckSnapshot MediaSnapshot()
        {
            var source = new MediaSourceId(Identity.Parse(SourceId)); var duration = TimeSpan.FromSeconds(2);
            var transport = new MediaTransportSnapshot(MediaContractVersion.Current, Asset, source, MediaTransportState.Paused,
                new MediaTransportPosition(10, 100, TimeSpan.FromMilliseconds(200), duration, duration - TimeSpan.FromMilliseconds(200), FrameRate.Fps50), null);
            var probe = new LocalMediaProbe(MediaContractVersion.Current, Asset, source, "reference.mp4", MediaContainerFormat.Mp4,
                MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, duration);
            var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, Asset, 100, 0, 99,
                CueIds.Take(OmitSecondCue ? 1 : 2).Select((id, i) => new MediaCuePoint(id, CueNames[i], 20 + i)).ToArray());
            return new(MediaContractVersion.Current, MediaDeckState.Paused, source, probe, transport, markers);
        }
        private OperatorAudioInputDescriptor Audio(string id) => new(id, "audio-stream", Gains.GetValueOrDefault(id, 1), false, 0, 0, 0, false, "HEALTHY");
        public ValueTask<OperatorAudioInputDescriptor> SetAudioInputStateAsync(string sourceId, double gain, bool muted, CancellationToken cancellationToken = default)
        {
            MutationCalls++; OnMutation?.Invoke(MutationCalls);
            if (Reject || MutationCalls == RejectMutationCall) throw new InvalidOperationException("rejected audio");
            Gains[sourceId] = gain; return ValueTask.FromResult(Audio(sourceId));
        }
        public ValueTask<MediaDeckSnapshot> ApplyMediaDeckMarkerAsync(MediaMarkerCommand command, CancellationToken cancellationToken = default)
        {
            MutationCalls++; if (Reject) throw new InvalidOperationException("rejected cue");
            var index = Array.IndexOf(CueIds, command.CuePointId!.Value);
            CueNames[index] = command.Name!;
            return ValueTask.FromResult(MediaSnapshot());
        }
        private IReadOnlyList<OperatorCompositingLayerDescriptor> Layers => ManyLayers ?
            Enumerable.Range(0, 8).Select(i => new OperatorCompositingLayerDescriptor("layer" + i, 2, i, true, 255, 0.1, 0.2, 1, "logo.rgba",
                processingStack: [new("grade", 1, true, colorGrade: new(0, 1, 1))])).ToArray() :
            (OmitBitmap ? new OperatorCompositingLayerDescriptor[] { new("other", 2, 1, true, 255, 0.1, 0.2, 1, "logo.rgba") } :
            [_layer, new("other", 2, 1, true, 255, 0.1, 0.2, 1, "logo.rgba")]);
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			FailSnapshots ? ValueTask.FromException<OperatorStatusSnapshot>(new TimeoutException("Unavailable")) :
			ValueTask.FromResult(new OperatorStatusSnapshot(
				new AuthoritativeProductionState(ControlContractVersion.Current, _production, new Revision(RevisionValue), new ProductionRoutingState(_source, RemoteCut ? OtherSource : _source)),
				[new(_source.ToString(), "Camera"), new(OtherSource.ToString(), "Camera 2")], "READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0,
				compositingLayers: Layers, audioInputs: [Audio(SourceId), Audio(OtherSource.ToString())]));

        public ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerStateAsync(
            string layerId, bool visible, byte opacity, CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            if (Reject) throw new InvalidOperationException("control.compositing.rejected");
            _layer = new(layerId, 2, _layer.Order, visible, opacity, _layer.PositionX, _layer.PositionY,
                _layer.Scale, _layer.ContentIdentity, _layer.RotationDegrees, _layer.AnchorX, _layer.AnchorY,
                _layer.CropLeft, _layer.CropTop, _layer.CropRight, _layer.CropBottom, processingStack: _layer.ProcessingStack);
            return ValueTask.FromResult(Layers);
        }
		public async ValueTask<IReadOnlyList<OperatorCompositingLayerDescriptor>> SetCompositingLayerTransformAsync(
			string layerId, double x, double y, double scale, double rotation, double ax, double ay,
			double left, double top, double right, double bottom, CancellationToken cancellationToken = default)
		{
			MutationCalls++;
			if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
			if (Reject) throw new InvalidOperationException("control.compositing.rejected");
			_layer = new(layerId, 2, 0, true, 255, x, y, scale, "logo.rgba", rotation, ax, ay, left, top, right, bottom, processingStack: _layer.ProcessingStack);
			return [_layer];
		}
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
	}
	internal sealed class ReadyService : IRuntimeReadinessService
	{
		public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Ready, DateTimeOffset.UnixEpoch,
			DateTimeOffset.UnixEpoch, Array.Empty<ReadinessReason>(), true, RuntimePerformanceVerificationSnapshot.Unverified);
		public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add { } remove { } }
		public void Observe(RuntimeReadinessObservation observation) { }
		public void InvalidatePerformance(string detail) { }
	}
}
