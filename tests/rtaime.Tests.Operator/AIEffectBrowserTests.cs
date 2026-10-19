// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using rtaime.Operator.Controls;
using System.Windows.Controls;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class AIEffectBrowserTests
{
    [Fact]
    public async Task Selection_and_test_require_explicit_approval_and_never_activate_Program()
    {
        var transport = new EvidenceTransport();
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var model = new AIEffectBrowserViewModel(client, () => true, () => false);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        Assert.False(model.CanActivate);
        model.Approved = true;
        Assert.False(model.Approved);
        await model.TestAsync();
        Assert.True(model.CanApprove);
        Assert.False(model.CanActivate);
        model.Approved = true;
        Assert.True(model.CanActivate);
        Assert.Equal(0, transport.Activations);
        Assert.Contains("No visual output", model.TestStatus);
        model.Selected = null;
        Assert.False(model.CanActivate);
    }

    [Fact]
    public async Task Changed_revision_or_stale_operator_invalidates_test_and_approval()
    {
        var transport = new EvidenceTransport();
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var ready = true;
        var model = new AIEffectBrowserViewModel(client, () => ready, () => false);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        await model.TestAsync();
        model.Approved = true;
        transport.Revision++;
        await client.SynchronizeAsync();
        model.Observe(OperatorAIShowcaseDescriptor.Unavailable);
        Assert.False(model.CanActivate);
        await model.TestAsync();
        model.Approved = true;
        ready = false;
        model.Observe(OperatorAIShowcaseDescriptor.Unavailable);
        Assert.False(model.Approved);
        Assert.Contains("STALE", model.Execution);
        Assert.False(model.TestCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("UNAVAILABLE")]
    [InlineData("FAILED")]
    public async Task Failed_test_cannot_enable_approval(string status)
    {
        var transport = new EvidenceTransport { TestState = status };
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var model = new AIEffectBrowserViewModel(client, () => true, () => false);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        await model.TestAsync();
        model.Approved = true;
        Assert.False(model.CanActivate);
        Assert.Contains("REJECTED", model.TestStatus);
        Assert.Equal(0, transport.Activations);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    public void Provider_evidence_does_not_invent_production_qualification(int state, bool available)
    {
        var evidence = Capability(state);
        Assert.Equal(available, evidence.Available);
        Assert.DoesNotContain("QUALIFIED", evidence.Status);
        if (available) Assert.Contains("UNVERIFIED", evidence.Status);
        Assert.False((evidence with { Kind = "future.effect" }).Supported);
    }

    [Fact]
    public void Capability_controls_preserve_keyboard_entry_and_accessible_names()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var control in new System.Windows.Controls.Control[] { new RtaimeButton(), new RtaimeComboBox(), new RtaimeCheckBox() })
                {
                    Assert.True(control.Focusable);
                    Assert.True(control.IsTabStop);
                }
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
                    directory = directory.Parent;
                Assert.NotNull(directory);
                var document = XDocument.Load(Path.Combine(directory.FullName, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml"));
                var controls = document.Descendants().Where(element => element.Attributes().Any(attribute =>
                    attribute.Value.Contains("AIEffects.", StringComparison.Ordinal))).ToArray();
                foreach (var control in controls.Where(element => element.Name.LocalName is "RtaimeButton" or "RtaimeComboBox" or "RtaimeCheckBox"))
                    Assert.Contains(control.Attributes(), attribute => attribute.Name.LocalName == "AutomationProperties.Name" && !string.IsNullOrWhiteSpace(attribute.Value));
                Assert.Contains(document.Descendants(), element => element.Name.LocalName == "RtaimeCheckBox" &&
                    element.Descendants().Any(child => child.Attributes().Any(attribute => attribute.Name.LocalName == "TextWrapping" && attribute.Value == "Wrap")));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Keyboard control qualification exceeded its bounded wait.");
        if (failure is not null) throw failure;
    }


    [Fact]
    public async Task Provider_model_input_resources_and_current_Program_target_are_only_advertised_evidence()
    {
        var transport = new EvidenceTransport();
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var model = new AIEffectBrowserViewModel(client, () => true, () => false);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        Assert.Contains("ai.person-segmentation", model.CapabilityIdentity);
        Assert.Contains("reference", model.ProviderIdentity);
        Assert.Equal("1.0.0-reference", model.ModelEvidence);
        Assert.Equal("video-frame-descriptor.v1", model.InputContract);
        Assert.Equal("segmentation-mask-descriptor.v1", model.OutputContract);
        Assert.Equal("compute=10;vram=67108864;rate<=30", model.ResourceRequirements);
        Assert.Contains("not a trained production model", model.Provenance);
        Assert.Contains(transport.Revision.ToString(), model.ProgramTarget);
        Assert.Contains("TEST", model.NextStep);
        Assert.False(model.CanActivate);
        Assert.Equal(0, transport.Activations);
    }

    [Fact]
    public async Task Same_revision_but_new_Program_source_invalidates_approval_without_activating()
    {
        var transport = new EvidenceTransport();
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var model = new AIEffectBrowserViewModel(client, () => true, () => false);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        await model.TestAsync();
        model.Approved = true;
        Assert.True(model.CanActivate);
        transport.AlternateSource = true;
        await client.SynchronizeAsync();
        model.RaiseCommandState();
        Assert.False(model.Approved);
        Assert.False(model.CanActivate);
        model.Observe(OperatorAIShowcaseDescriptor.Unavailable);
        Assert.Equal(0, transport.Activations);
        Assert.Contains("TEST", model.NextStep);
    }

    [Fact]
    public async Task Missing_test_sample_and_disconnected_runtime_fail_closed_with_guidance()
    {
        var transport = new EvidenceTransport { TestSequence = null };
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var healthy = true;
        var model = new AIEffectBrowserViewModel(client, () => true, () => false, () => healthy);
        await model.RefreshAsync();
        model.Selected = model.Capabilities.Single();
        await model.TestAsync();
        Assert.Contains("REJECTED", model.TestStatus);
        Assert.False(model.CanApprove);
        Assert.False(model.CanActivate);
        healthy = false;
        model.Observe(OperatorAIShowcaseDescriptor.Unavailable);
        Assert.Contains("Restore Control/Runtime", model.Recovery);
        Assert.Contains("Synchronize", model.NextStep);
        Assert.Equal(0, transport.Activations);
    }

    [Fact]
    public async Task Empty_provider_catalogue_shows_unavailable_without_fake_model_or_download()
    {
        var transport = new EvidenceTransport { Advertise = false };
        var client = new OperatorControlClient(transport);
        await client.SynchronizeAsync();
        var model = new AIEffectBrowserViewModel(client, () => true, () => false);
        await model.RefreshAsync();
        Assert.Empty(model.Capabilities);
        Assert.Null(model.Selected);
        Assert.Contains("UNAVAILABLE", model.Status);
        Assert.Contains("no package", model.ResourceRequirements);
        Assert.Contains("installed provider", model.Recovery);
        Assert.False(model.TestCommand.CanExecute(null));
    }

    [Fact]
    public void Dedicated_workspace_uses_shared_Program_monitor_and_explicit_nonvisual_test()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var operatorRoot = Path.Combine(directory.FullName, "src", "Hosts", "rtaime.Operator");
        var content = XDocument.Load(Path.Combine(operatorRoot, "AIProcessingWorkspaceControl.xaml"));
        var window = XDocument.Load(Path.Combine(operatorRoot, "MainWindow.xaml"));
        Assert.Contains(window.Descendants(), element => element.Name.LocalName == "AIProcessingWorkspaceControl"
            && element.Attributes().Any(attribute => attribute.Value.Contains("AIWorkspaceVisibility", StringComparison.Ordinal)));
        Assert.Contains(content.Descendants(), element => element.Name.LocalName == "ProgramViewer"
            && element.Attributes().Any(attribute => attribute.Value.Contains("Monitoring.ProgramImage", StringComparison.Ordinal)));
        Assert.DoesNotContain(content.Descendants(), element => element.Name.LocalName == "PreviewViewer");
        foreach (var command in new[] { "AIEffects.RefreshCommand", "AIEffects.TestCommand",
                     "EnableAIShowcaseCommand", "DisableAIShowcaseCommand" })
        {
            Assert.Contains(content.Descendants(), element => element.Name.LocalName == "RtaimeButton"
                && element.Attributes().Any(attribute => attribute.Value.Contains(command, StringComparison.Ordinal))
                && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                    && attribute.Name.NamespaceName.Contains("automation", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name.ToString().Contains("AutomationProperties.Name", StringComparison.Ordinal)));
        }
        Assert.Contains(content.Descendants(), element => element.Name.LocalName == "RtaimeCheckBox"
            && element.Attributes().Any(attribute => attribute.Value.Contains("AIEffects.CanApprove", StringComparison.Ordinal)));
        Assert.Contains(content.Descendants(), element => element.Name.LocalName == "TextBlock"
            && element.Attributes().Any(attribute => attribute.Value.Contains("A/B IMAGE NOT AVAILABLE", StringComparison.Ordinal)));
    }

    private static OperatorAICapabilityDescriptor Capability(int state = 1) => new(
        "a2000000-0000-0000-0000-000000000001", "ai.person-segmentation", true, "reference",
        "Managed Reference Person Segmentation", state,
        [new("1.0.0-reference", "video-frame-descriptor.v1", "segmentation-mask-descriptor.v1",
            "compute=10;vram=67108864;rate<=30", "hash", "synthetic CI/reference package; not a trained production model")]);

    private sealed class EvidenceTransport : IOperatorControlTransport
    {
        private readonly ProductionId _production = ProductionId.New();
        private readonly ProductionSourceId _source = ProductionSourceId.New();
        public ulong Revision { get; set; } = 1;
        public int Activations { get; private set; }
        public bool AlternateSource { get; set; }
        public bool Advertise { get; set; } = true;
        public ulong? TestSequence { get; set; } = 1;
        private readonly ProductionSourceId _otherSource = ProductionSourceId.New();
        public string TestState { get; init; } = "TESTED";
        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var source = AlternateSource ? _otherSource : _source;
            return ValueTask.FromResult(new OperatorStatusSnapshot(
                new AuthoritativeProductionState(ControlContractVersion.Current, _production, new Revision(Revision),
                    new ProductionRoutingState(source, source)), [new OperatorSourceDescriptor(source.ToString(), "Program")],
                "READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0));
        }
        public ValueTask<IReadOnlyList<OperatorAICapabilityDescriptor>> GetAIEffectCatalogAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperatorAICapabilityDescriptor>>(Advertise ? [Capability()] : []);
        public ValueTask<OperatorAIShowcaseDescriptor> TestAIEffectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(OperatorAIShowcaseDescriptor.Unavailable with {
                Status = TestState, Provider = Capability().Provider, InferenceTime = TimeSpan.FromMilliseconds(5), SourceSequence = TestSequence });
        public ValueTask<OperatorAIShowcaseDescriptor> SetAIShowcaseApprovedAsync(bool enabled, ulong expectedRevision, CancellationToken cancellationToken = default)
        {
            Activations++;
            throw new InvalidOperationException("Selection or tests cannot activate Program.");
        }
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
