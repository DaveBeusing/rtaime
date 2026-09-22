// Copyright (c) 2026 Dave Beusing
// david.beusing@gmail.com

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Unit;

public sealed class OutputRoleControlTests
{
	[Fact]
	public void Initialization_exposes_stable_Program_and_Aux_roles()
	{
		var fixture = CreateFixture();

		var initialized = ControlDomainEngine.Initialize(fixture.Specification);

		Assert.True(initialized.Succeeded);
		var state = initialized.State!.Authoritative;
		var program = Assert.Single(state.OutputRoles, role => role.RoleId == OutputRoleIds.Program);
		var aux = Assert.Single(state.OutputRoles, role => role.RoleId == OutputRoleIds.Aux);
		Assert.Equal(OutputRoleKind.Program, program.Kind);
		Assert.Equal(fixture.SourceA, program.SourceId);
		Assert.Equal(OutputRoleKind.Aux, aux.Kind);
		Assert.Equal(fixture.SourceB, aux.SourceId);
		Assert.Equal("auto", aux.ProviderSelector);
		Assert.Equal("aux", aux.TargetId);
		Assert.Equal("production", aux.FormatPolicy);
		Assert.Equal("production", aux.TimingPolicy);
	}

	[Fact]
	public void Aux_routing_changes_only_Aux_and_advances_authoritative_revision()
	{
		var fixture = CreateFixture();
		var current = ControlDomainEngine.Initialize(fixture.Specification).State!.Authoritative;
		var command = new RouteOutputRoleCommand(
			Metadata(fixture.ProductionId, current.Revision),
			OutputRoleIds.Aux,
			fixture.SourceA);

		var result = ControlDomainEngine.Apply(fixture.Specification, current, command);

		Assert.True(result.Committed);
		Assert.Equal(current.Revision.Next(), result.AuthoritativeState.Revision);
		Assert.Equal(fixture.SourceA, Assert.Single(result.AuthoritativeState.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId);
		Assert.Equal(fixture.SourceA, Assert.Single(result.AuthoritativeState.OutputRoles, role => role.RoleId == OutputRoleIds.Program).SourceId);
		Assert.Equal(current.Routing, result.AuthoritativeState.Routing);
	}

	[Fact]
	public void Program_role_rejects_generic_output_routing()
	{
		var fixture = CreateFixture();
		var current = ControlDomainEngine.Initialize(fixture.Specification).State!.Authoritative;
		var command = new RouteOutputRoleCommand(
			Metadata(fixture.ProductionId, current.Revision),
			OutputRoleIds.Program,
			fixture.SourceB);

		var result = ControlDomainEngine.Apply(fixture.Specification, current, command);

		Assert.False(result.Committed);
		Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.command.program_requires_transition");
		Assert.Equal(current, result.AuthoritativeState);
	}

	[Fact]
	public void Program_transition_synchronizes_Program_role_without_changing_Aux()
	{
		var fixture = CreateFixture();
		var current = ControlDomainEngine.Initialize(fixture.Specification).State!.Authoritative;
		var command = new CutProgramCommand(
			Metadata(fixture.ProductionId, current.Revision),
			fixture.SourceB);

		var result = ControlDomainEngine.Apply(fixture.Specification, current, command);

		Assert.True(result.Committed);
		Assert.Equal(fixture.SourceB, result.AuthoritativeState.Routing.ProgramSourceId);
		Assert.Equal(fixture.SourceB, Assert.Single(result.AuthoritativeState.OutputRoles, role => role.RoleId == OutputRoleIds.Program).SourceId);
		Assert.Equal(fixture.SourceB, Assert.Single(result.AuthoritativeState.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId);
	}

	[Fact]
	public void Unknown_Aux_source_is_rejected_without_state_change()
	{
		var fixture = CreateFixture();
		var current = ControlDomainEngine.Initialize(fixture.Specification).State!.Authoritative;
		var command = new RouteOutputRoleCommand(
			Metadata(fixture.ProductionId, current.Revision),
			OutputRoleIds.Aux,
			ProductionSourceId.New());

		var result = ControlDomainEngine.Apply(fixture.Specification, current, command);

		Assert.False(result.Committed);
		Assert.Contains(result.Validation.Issues, issue => issue.Code == "control.command.source_unknown");
		Assert.Equal(current, result.AuthoritativeState);
	}

	[Fact]
	public void Unsupported_output_policy_is_rejected_fail_closed()
	{
		var fixture = CreateFixture(
			new ProductionOutputRoleState(
				OutputRoleIds.Aux,
				OutputRoleKind.Aux,
				SourceB,
				"explicit-provider",
				"aux",
				"custom-format",
				"custom-timing"));

		var initialized = ControlDomainEngine.Initialize(fixture.Specification);

		Assert.False(initialized.Succeeded);
		Assert.Contains(initialized.Validation.Issues, issue => issue.Code == "control.output_roles.provider_selector_unsupported");
		Assert.Contains(initialized.Validation.Issues, issue => issue.Code == "control.output_roles.format_policy_unsupported");
		Assert.Contains(initialized.Validation.Issues, issue => issue.Code == "control.output_roles.timing_policy_unsupported");
	}

	[Fact]
	public void Planning_carries_Aux_role_source_and_identity_into_prepared_execution()
	{
		var fixture = CreateFixture();
		var initialized = ControlDomainEngine.Initialize(fixture.Specification).State!.Authoritative;
		var provider = new VirtualMediaReferenceProvider(
			new MediaSourceId(fixture.SourceA.Value),
			new MediaSourceId(fixture.SourceB.Value),
			VideoFormat.Hd1080p50Rgba8);
		var registry = new ProviderRegistry(provider.Descriptor);

		var planning = CapabilityPlanningEngine.Plan(fixture.Specification, initialized, registry);

		Assert.True(planning.Succeeded);
		var program = Assert.Single(planning.PreparedExecution!.Bindings, binding => binding.OutputRoleId == "program");
		var aux = Assert.Single(planning.PreparedExecution.Bindings, binding => binding.OutputRoleId == "aux");
		Assert.Equal(new MediaSourceId(fixture.SourceA.Value), program.MediaSourceId);
		Assert.Equal(new MediaSourceId(fixture.SourceB.Value), aux.MediaSourceId);
		Assert.NotNull(aux.MediaSinkId);
		Assert.Equal(provider.Descriptor.ProviderId, aux.Resource.ProviderId);
		Assert.NotEqual(program.Resource.ResourceId, aux.Resource.ResourceId);
	}

	private static readonly ProductionId ProductionId = new(Identity.Parse("91000000-0000-0000-0000-000000000001"));
	private static readonly ProductionSourceId SourceA = new(Identity.Parse("91000000-0000-0000-0000-00000000000a"));
	private static readonly ProductionSourceId SourceB = new(Identity.Parse("91000000-0000-0000-0000-00000000000b"));

	private static Fixture CreateFixture(ProductionOutputRoleState? auxOverride = null)
	{
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			ProductionId,
			"Output Role Unit Test",
			new[]
			{
				new ProductionSourceSpecification(SourceA, "Input A"),
				new ProductionSourceSpecification(SourceB, "Input B")
			},
			new ProductionRoutingState(SourceA, SourceA),
			initialOutputRoles: new[]
			{
				ProductionOutputRoleState.Program(SourceA),
				auxOverride ?? ProductionOutputRoleState.Aux(SourceB)
			});
		return new Fixture(ProductionId, SourceA, SourceB, specification);
	}

	private static ControlCommandMetadata Metadata(ProductionId productionId, Revision revision) =>
		new(ControlContractVersion.Current, CommandId.New(), productionId, revision);

	private sealed record Fixture(
		ProductionId ProductionId,
		ProductionSourceId SourceA,
		ProductionSourceId SourceB,
		ProductionSpecification Specification);

	private sealed class ProviderRegistry : IProviderCapabilityRegistry
	{
		private readonly ProviderDescriptor[] _providers;

		public ProviderRegistry(params ProviderDescriptor[] providers) => _providers = providers;

		public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
	}
}
