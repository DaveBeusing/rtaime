// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public interface IControlHostClock
{
	UtcTimestamp GetUtcNow();
}

public sealed class SystemControlHostClock : IControlHostClock
{
	public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}

public sealed record ControlHostExecutionPackage(
	AuthoritativeProductionState AuthoritativeState,
	LogicalProductionGraph Graph,
	PreparedExecutionContract PreparedExecution,
	MediaSinkId ProgramSinkId,
	RuntimeProgramTransitionIntent? ProgramTransition);

public sealed record ControlHostOperationResult(
	bool Accepted,
	AuthoritativeProductionState State,
	ControlHostExecutionPackage? Execution,
	Failure? Failure)
{
	public static ControlHostOperationResult Staged(ControlHostExecutionPackage execution) =>
		new(true, execution.AuthoritativeState, execution, null);

	public static ControlHostOperationResult Rejected(AuthoritativeProductionState state, Failure failure) =>
		new(false, state, null, failure);
}

public sealed record ControlHostCommitConfirmation(
	bool Committed,
	AuthoritativeProductionState? State,
	Failure? Failure);

/// <summary>
/// Authoritative V1 ControlHost composition root. It owns validation/state/planning, but never calls RuntimeHost.
/// A proposed authoritative revision is staged with a PreparedExecutionContract and only becomes authoritative
/// after the corresponding Runtime commit is confirmed.
/// </summary>
public sealed class ControlHostService
{
	private readonly object _gate = new();
	private readonly ProductionSpecification _specification;
	private readonly UpdatableProviderRegistry _providers;
	private readonly BoundedProductionJournal _journal;
	private readonly IControlHostClock _clock;
	private AuthoritativeProductionState? _authoritative;
	private PendingControlCommit? _pending;

	public ControlHostService(
		ProductionSpecification specification,
		IReadOnlyList<ProviderDescriptor> providers,
		BoundedProductionJournal journal,
		IControlHostClock? clock = null)
	{
		_specification = specification ?? throw new ArgumentNullException(nameof(specification));
		_providers = new UpdatableProviderRegistry(providers ?? throw new ArgumentNullException(nameof(providers)));
		_journal = journal ?? throw new ArgumentNullException(nameof(journal));
		_clock = clock ?? new SystemControlHostClock();
	}

	public ProductionSpecification Specification => _specification;

	public AuthoritativeProductionState State
	{
		get
		{
			lock (_gate)
				return _authoritative ?? throw new InvalidOperationException("ControlHost has no committed authoritative state.");
		}
	}

	public bool HasAuthoritativeState
	{
		get { lock (_gate) return _authoritative is not null; }
	}

	public bool HasPendingExecution
	{
		get { lock (_gate) return _pending is not null; }
	}

	public ControlHostOperationResult Initialize()
	{
		lock (_gate)
		{
			if (_authoritative is not null || _pending is not null)
				throw new InvalidOperationException("ControlHost has already been initialized or has a pending initialization.");

			var initialized = ControlDomainEngine.Initialize(_specification);
			if (!initialized.Succeeded)
			{
				var failure = FromValidation("control.initialize.rejected", initialized.Validation);
				Journal(Revision.Initial, "control", "control.initialize.rejected", failure.Message, null, failure);
				throw new InvalidOperationException(failure.Message);
			}

			var proposed = initialized.State!.Authoritative;
			var planning = CapabilityPlanningEngine.Plan(_specification, proposed, _providers);
			if (!planning.Succeeded)
			{
				var failure = FromValidation("control.initialize.planning_rejected", planning.Validation);
				Journal(proposed.Revision, "planning", "planning.rejected", failure.Message, null, failure);
				throw new InvalidOperationException(failure.Message);
			}

			var execution = Package(proposed, planning, null);
			_pending = new PendingControlCommit(execution, null);
			Journal(proposed.Revision, "control", "control.initialize.staged", "Initial authoritative state is staged pending Runtime commit.", null, null);
			Journal(proposed.Revision, "planning", "planning.prepared", execution.PreparedExecution.PreparedExecutionId.ToString(), null, null);
			return ControlHostOperationResult.Staged(execution);
		}
	}

	public void RefreshProviderSnapshot(IReadOnlyList<ProviderDescriptor> providers)
	{
		ArgumentNullException.ThrowIfNull(providers);
		lock (_gate)
		{
			EnsureNoPending();
			_providers.Replace(providers);
			Journal(_authoritative?.Revision ?? Revision.Initial, "runtime", "runtime.providers.refreshed", $"Provider snapshot refreshed with {providers.Count} descriptor(s).", null, null);
		}
	}

	public ControlHostExecutionPackage PrepareCurrentExecution()
	{
		lock (_gate)
		{
			EnsureNoPending();
			var current = Current();
			var planning = CapabilityPlanningEngine.Plan(_specification, current, _providers);
			if (!planning.Succeeded)
			{
				var failure = FromValidation("control.resync.planning_rejected", planning.Validation);
				Journal(current.Revision, "planning", "planning.resync_rejected", failure.Message, null, failure);
				throw new InvalidOperationException(failure.Message);
			}

			var execution = Package(current, planning, null);
			Journal(current.Revision, "planning", "planning.resync_prepared", execution.PreparedExecution.PreparedExecutionId.ToString(), null, null);
			return execution;
		}
	}

	public ControlHostOperationResult SelectPreview(SelectPreviewCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		lock (_gate)
		{
			EnsureNoPending();
			return Stage(ControlDomainEngine.Apply(_specification, Current(), command), command.Metadata.CommandId.Value, null);
		}
	}

	public ControlHostOperationResult CutProgram(CutProgramCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		lock (_gate)
		{
			EnsureNoPending();
			var current = Current();
			var transition = current.Routing.ProgramSourceId == command.SourceId
				? null
				: RuntimeProgramTransitionIntent.Cut(
					new MediaSourceId(current.Routing.ProgramSourceId.Value),
					new MediaSourceId(command.SourceId.Value));
			return Stage(ControlDomainEngine.Apply(_specification, current, command), command.Metadata.CommandId.Value, transition);
		}
	}

	public ControlHostOperationResult DissolveProgram(DissolveProgramCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		lock (_gate)
		{
			EnsureNoPending();
			var current = Current();
			var transition = current.Routing.ProgramSourceId == command.SourceId
				? null
				: RuntimeProgramTransitionIntent.Dissolve(
					new MediaSourceId(current.Routing.ProgramSourceId.Value),
					new MediaSourceId(command.SourceId.Value),
					command.DurationFrames);
			return Stage(ControlDomainEngine.Apply(_specification, current, command), command.Metadata.CommandId.Value, transition);
		}
	}

	public ControlHostCommitConfirmation ConfirmRuntimeCommit(
		PreparedExecutionId preparedExecutionId,
		RuntimeCommitResult runtimeResult)
	{
		ArgumentNullException.ThrowIfNull(runtimeResult);
		lock (_gate)
		{
			if (_pending is null || _pending.Execution.PreparedExecution.PreparedExecutionId != preparedExecutionId)
				return PendingMismatch("control.commit.pending_mismatch", "Runtime commit confirmation does not match the staged Control execution.");

			var pending = _pending;
			_pending = null;

			if (runtimeResult.Status != RuntimeCommitStatus.Committed)
			{
				var failure = runtimeResult.Failure ?? new Failure("control.commit.runtime_rejected", "Runtime rejected the staged execution.");
				Journal(_authoritative?.Revision ?? Revision.Initial, "runtime", "runtime.commit.rejected", failure.Message, pending.CausationId, failure);
				return new ControlHostCommitConfirmation(false, _authoritative, failure);
			}

			_authoritative = pending.Execution.AuthoritativeState;
			Journal(
				_authoritative.Revision,
				"runtime",
				"runtime.commit.observed",
				$"Runtime execution revision {runtimeResult.ExecutionRevision} committed.",
				pending.CausationId,
				null);
			Journal(
				_authoritative.Revision,
				"control",
				"control.authoritative.committed",
				"Staged authoritative production state crossed the host commit boundary.",
				pending.CausationId,
				null);
			return new ControlHostCommitConfirmation(true, _authoritative, null);
		}
	}

	public ControlHostCommitConfirmation RejectRuntimeCommit(PreparedExecutionId preparedExecutionId, Failure failure)
	{
		if (!failure.IsDefined) throw new ArgumentException("Runtime rejection failure must be defined.", nameof(failure));
		lock (_gate)
		{
			if (_pending is null || _pending.Execution.PreparedExecution.PreparedExecutionId != preparedExecutionId)
				return PendingMismatch("control.commit.pending_mismatch", "Runtime rejection does not match the staged Control execution.");

			var pending = _pending;
			_pending = null;
			Journal(_authoritative?.Revision ?? Revision.Initial, "runtime", "runtime.commit.transport_failed", failure.Message, pending.CausationId, failure);
			return new ControlHostCommitConfirmation(false, _authoritative, failure);
		}
	}

	public void RecordObservation(string category, string code, string detail, Failure? failure = null)
	{
		lock (_gate)
			Journal(_authoritative?.Revision ?? Revision.Initial, category, code, detail, null, failure);
	}

	private ControlHostCommitConfirmation PendingMismatch(string code, string message)
	{
		var failure = new Failure(code, message);
		Journal(_authoritative?.Revision ?? Revision.Initial, "control", "control.commit.rejected", failure.Message, null, failure);
		return new ControlHostCommitConfirmation(false, _authoritative, failure);
	}

	private ControlHostOperationResult Stage(
		ControlCommandResult domainResult,
		Identity commandId,
		RuntimeProgramTransitionIntent? transition)
	{
		var current = Current();
		if (!domainResult.Committed)
		{
			var failure = FromValidation("control.command.rejected", domainResult.Validation);
			Journal(current.Revision, "control", "control.command.rejected", failure.Message, commandId, failure);
			return ControlHostOperationResult.Rejected(current, failure);
		}

		var proposed = domainResult.AuthoritativeState;
		var planning = CapabilityPlanningEngine.Plan(_specification, proposed, _providers);
		if (!planning.Succeeded)
		{
			var failure = FromValidation("control.command.planning_rejected", planning.Validation);
			Journal(current.Revision, "planning", "planning.rejected", failure.Message, commandId, failure);
			return ControlHostOperationResult.Rejected(current, failure);
		}

		var execution = Package(proposed, planning, transition);
		_pending = new PendingControlCommit(execution, commandId);
		Journal(proposed.Revision, "control", "control.command.staged", "Validated production command is staged pending Runtime commit.", commandId, null);
		Journal(proposed.Revision, "planning", "planning.prepared", execution.PreparedExecution.PreparedExecutionId.ToString(), commandId, null);
		return ControlHostOperationResult.Staged(execution);
	}

	private static ControlHostExecutionPackage Package(
		AuthoritativeProductionState state,
		ExecutionPlanningResult planning,
		RuntimeProgramTransitionIntent? transition)
	{
		var graph = planning.Graph ?? throw new InvalidOperationException("Successful planning must expose a graph.");
		var programSink = graph.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink).MediaSinkId
			?? throw new InvalidOperationException("Program sink node must expose a media sink identity.");

		return new ControlHostExecutionPackage(
			state,
			graph,
			planning.PreparedExecution ?? throw new InvalidOperationException("Successful planning must expose a prepared execution."),
			programSink,
			transition);
	}

	private AuthoritativeProductionState Current() =>
		_authoritative ?? throw new InvalidOperationException("ControlHost has no committed authoritative state.");

	private void EnsureNoPending()
	{
		if (_pending is not null)
			throw new InvalidOperationException("ControlHost accepts only one staged production mutation at a time.");
	}

	private void Journal(
		Revision revision,
		string category,
		string code,
		string detail,
		Identity? causationId,
		Failure? failure)
	{
		_journal.TryAppend(new ProductionJournalEvent(
			_specification.ProductionId.Value,
			revision,
			_clock.GetUtcNow(),
			category,
			code,
			detail,
			causationId,
			failure));
	}

	private static Failure FromValidation(string code, ControlValidationReport validation)
	{
		var message = validation.Issues.Count == 0
			? "Control validation failed without a diagnostic issue."
			: string.Join("; ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
		return new Failure(code, message);
	}

	private sealed record PendingControlCommit(ControlHostExecutionPackage Execution, Identity? CausationId);

	private sealed class UpdatableProviderRegistry : IProviderCapabilityRegistry
	{
		private ProviderDescriptor[] _providers;

		public UpdatableProviderRegistry(IReadOnlyList<ProviderDescriptor> providers) =>
			_providers = Validate(providers);

		public IReadOnlyList<ProviderDescriptor> GetProviders() => Array.AsReadOnly(_providers.ToArray());

		public void Replace(IReadOnlyList<ProviderDescriptor> providers) =>
			_providers = Validate(providers);

		private static ProviderDescriptor[] Validate(IReadOnlyList<ProviderDescriptor> providers)
		{
			ArgumentNullException.ThrowIfNull(providers);
			if (providers.Any(provider => provider is null))
				throw new ArgumentException("Provider snapshot must not contain null values.", nameof(providers));
			return providers.ToArray();
		}
	}
}
