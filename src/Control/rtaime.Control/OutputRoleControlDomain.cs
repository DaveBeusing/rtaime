// Copyright (c) 2026 Dave Beusing
// david.beusing@gmail.com

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.Control;

public static class ProductionOutputRoleValidator
{
	public static ControlValidationReport Validate(
		ProductionSpecification specification,
		IReadOnlyList<ProductionOutputRoleState> outputRoles,
		ProductionRoutingState routing,
		string pathPrefix)
	{
		ArgumentNullException.ThrowIfNull(specification);
		ArgumentNullException.ThrowIfNull(outputRoles);
		ArgumentNullException.ThrowIfNull(routing);
		if (string.IsNullOrWhiteSpace(pathPrefix))
			throw new ArgumentException("Output role validation path is required.", nameof(pathPrefix));

		var issues = new List<ValidationIssue>();
		if (outputRoles.Count == 0)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.empty",
				"At least the Program output role is required.",
				pathPrefix));
			return new ControlValidationReport(issues);
		}

		var duplicateRoleIds = outputRoles
			.GroupBy(role => role.RoleId)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToArray();
		foreach (var roleId in duplicateRoleIds)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.role_duplicate",
				$"Output role '{roleId}' is declared more than once.",
				pathPrefix));
		}

		var programRoles = outputRoles.Where(role => role.Kind == OutputRoleKind.Program).ToArray();
		if (programRoles.Length != 1)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.program_cardinality",
				"Exactly one Program output role is required.",
				pathPrefix));
		}
		else if (programRoles[0].RoleId != OutputRoleIds.Program)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.program_identity",
				"The Program output role must use the stable 'program' role identity.",
				$"{pathPrefix}.program"));
		}
		else if (programRoles[0].SourceId != routing.ProgramSourceId)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.program_source_mismatch",
				"Program output role source must match authoritative Program routing.",
				$"{pathPrefix}.program.sourceId"));
		}

		var auxRoles = outputRoles.Where(role => role.Kind == OutputRoleKind.Aux).ToArray();
		if (auxRoles.Length > 1)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.aux_cardinality",
				"V1 supports at most one governed Aux output role.",
				pathPrefix));
		}
		else if (auxRoles.Length == 1 && auxRoles[0].RoleId != OutputRoleIds.Aux)
		{
			issues.Add(new ValidationIssue(
				"control.output_roles.aux_identity",
				"The V1 Aux output role must use the stable 'aux' role identity.",
				$"{pathPrefix}.aux"));
		}

		var knownSources = specification.Sources.Select(source => source.SourceId).ToHashSet();
		foreach (var role in outputRoles)
		{
			if (!knownSources.Contains(role.SourceId))
			{
				issues.Add(new ValidationIssue(
					"control.output_roles.source_unknown",
					$"Output role '{role.RoleId}' references a source not declared by the production specification.",
					$"{pathPrefix}.{role.RoleId}.sourceId"));
			}
			if (!role.Enabled)
			{
				issues.Add(new ValidationIssue(
					"control.output_roles.disabled_unsupported",
					$"Configured V1 output role '{role.RoleId}' must be enabled.",
					$"{pathPrefix}.{role.RoleId}.enabled"));
			}
			if (!string.Equals(role.ProviderSelector, "auto", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new ValidationIssue(
					"control.output_roles.provider_selector_unsupported",
					$"Output role '{role.RoleId}' uses unsupported provider selector '{role.ProviderSelector}'. V1 supports only deterministic automatic provider admission.",
					$"{pathPrefix}.{role.RoleId}.providerSelector"));
			}
			if (!string.Equals(role.FormatPolicy, "production", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new ValidationIssue(
					"control.output_roles.format_policy_unsupported",
					$"Output role '{role.RoleId}' uses unsupported format policy '{role.FormatPolicy}'. V1 output roles inherit the production format.",
					$"{pathPrefix}.{role.RoleId}.formatPolicy"));
			}
			if (!string.Equals(role.TimingPolicy, "production", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new ValidationIssue(
					"control.output_roles.timing_policy_unsupported",
					$"Output role '{role.RoleId}' uses unsupported timing policy '{role.TimingPolicy}'. V1 output roles inherit production timing.",
					$"{pathPrefix}.{role.RoleId}.timingPolicy"));
			}
		}

		return new ControlValidationReport(issues);
	}
}

public static partial class ControlDomainEngine
{
	public static ControlCommandResult Apply(
		ProductionSpecification specification,
		AuthoritativeProductionState current,
		RouteOutputRoleCommand command)
	{
		ArgumentNullException.ThrowIfNull(specification);
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(command);

		var issues = new List<ValidationIssue>();
		issues.AddRange(ProductionSpecificationValidator.Validate(specification).Issues);
		issues.AddRange(ProductionOutputRoleValidator.Validate(
			specification,
			current.OutputRoles,
			current.Routing,
			"authoritative.outputRoles").Issues);

		if (current.Version != specification.Version)
			issues.Add(new ValidationIssue("control.state.version_mismatch", "Authoritative state version does not match the production specification.", "authoritative.version"));
		if (current.ProductionId != specification.ProductionId)
			issues.Add(new ValidationIssue("control.state.production_mismatch", "Authoritative state belongs to a different production.", "authoritative.productionId"));
		if (command.Metadata.Version != specification.Version)
			issues.Add(new ValidationIssue("control.command.version_mismatch", "Command version does not match the production specification.", "command.metadata.version"));
		if (command.Metadata.ProductionId != specification.ProductionId)
			issues.Add(new ValidationIssue("control.command.production_mismatch", "Command targets a different production.", "command.metadata.productionId"));
		if (command.Metadata.ExpectedRevision != current.Revision)
			issues.Add(new ValidationIssue("control.command.revision_conflict", $"Command expected authoritative revision '{command.Metadata.ExpectedRevision}' but current revision is '{current.Revision}'.", "command.metadata.expectedRevision"));
		if (!specification.Sources.Any(source => source.SourceId == command.SourceId))
			issues.Add(new ValidationIssue("control.command.source_unknown", "Command target source is not declared by the production specification.", "command.sourceId"));

		var role = current.OutputRoles.FirstOrDefault(candidate => candidate.RoleId == command.RoleId);
		if (role is null)
			issues.Add(new ValidationIssue("control.command.output_role_unknown", $"Output role '{command.RoleId}' is not configured.", "command.roleId"));
		else if (!role.Enabled)
			issues.Add(new ValidationIssue("control.command.output_role_disabled", $"Output role '{command.RoleId}' is disabled.", "command.roleId"));
		else if (role.Kind == OutputRoleKind.Program)
			issues.Add(new ValidationIssue("control.command.program_requires_transition", "Program routing must use the governed CUT/DISSOLVE transition path.", "command.roleId"));

		if (current.Revision.Value == ulong.MaxValue)
			issues.Add(new ValidationIssue("control.state.revision_exhausted", "Authoritative revision cannot advance beyond UInt64.MaxValue.", "authoritative.revision"));

		if (issues.Count > 0)
			return ControlCommandResult.Rejected(current, new ControlValidationReport(issues));

		var outputRoles = current.OutputRoles
			.Select(candidate => candidate.RoleId == command.RoleId ? candidate.WithSource(command.SourceId) : candidate)
			.ToArray();
		var outputValidation = ProductionOutputRoleValidator.Validate(
			specification,
			outputRoles,
			current.Routing,
			"desired.outputRoles");
		if (!outputValidation.IsValid)
			return ControlCommandResult.Rejected(current, outputValidation);

		var desired = new DesiredProductionState(
			specification.Version,
			specification.ProductionId,
			current.Revision,
			current.Routing,
			current.ActiveSceneId,
			outputRoles);
		var authoritative = new AuthoritativeProductionState(
			specification.Version,
			specification.ProductionId,
			current.Revision.Next(),
			current.Routing,
			current.ActiveSceneId,
			outputRoles);
		return ControlCommandResult.Accepted(current, desired, authoritative);
	}

	private static IReadOnlyList<ProductionOutputRoleState> SynchronizeProgramOutputRole(
		IReadOnlyList<ProductionOutputRoleState> roles,
		ProductionSourceId programSourceId) =>
		roles.Select(role => role.Kind == OutputRoleKind.Program ? role.WithSource(programSourceId) : role).ToArray();
}
