// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

public sealed class ProductionMacroCoordinator : IAsyncDisposable
{
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly ShowProjectPersistenceStore _persistence;
	private readonly ShowControlActionExecutor _actionExecutor;
	private readonly ShowControlFrameObserver _frameObserver;
	private readonly MediaAssetCatalogService? _mediaCatalog;
	private readonly Action _stateChanged;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Dictionary<ProductionMacroId, ProductionMacroDefinition> _macros = new();
	private readonly ShowControlExecutionMachine _machine = new();
	private ProductionMacroExecutionId? _executionId;
	private ProductionMacroActionId? _lastCompletedActionId;
	private ulong _storageVersion;
	private ulong _executionStorageVersion;
	private bool _restored;
	private CancellationTokenSource? _waitCancellation;
	private Task? _waitWorker;

	public ProductionMacroCoordinator(
		Func<ControlHostService?> controlAccessor,
		ShowProjectPersistenceStore persistence,
		ShowControlActionExecutor actionExecutor,
		ShowControlFrameObserver frameObserver,
		MediaAssetCatalogService? mediaCatalog,
		Action stateChanged)
	{
		_controlAccessor = controlAccessor ?? throw new ArgumentNullException(nameof(controlAccessor));
		_persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
		_actionExecutor = actionExecutor ?? throw new ArgumentNullException(nameof(actionExecutor));
		_frameObserver = frameObserver ?? throw new ArgumentNullException(nameof(frameObserver));
		_mediaCatalog = mediaCatalog;
		_stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroDefinition> GetAsync(
		ProductionMacroId macroId,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			return _macros.TryGetValue(macroId, out var macro)
				? macro
				: throw new KeyNotFoundException($"Production Macro '{macroId}' does not exist.");
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroValidationResult> ValidateAsync(
		ProductionMacroDefinition macro,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(macro);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			return await ValidateDefinitionAsync(macro, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> SaveAsync(
		ProductionMacroDefinition macro,
		ulong expectedStorageVersion,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(macro);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			EnsureDefinitionEditable(macro.MacroId);
			var validation = await ValidateDefinitionAsync(macro, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
				throw new InvalidDataException(string.Join(" | ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

			var prior = _macros.TryGetValue(macro.MacroId, out var existing) ? existing : null;
			_macros[macro.MacroId] = macro;
			try
			{
				await PersistDefinitionsAsync(expectedStorageVersion, cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				if (prior is null)
					_macros.Remove(macro.MacroId);
				else
					_macros[macro.MacroId] = prior;
				throw;
			}

			Journal("production_macro.saved", $"Production Macro '{macro.Name}' ({macro.MacroId}) was saved.", macro.MacroId.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> DeleteAsync(
		ProductionMacroId macroId,
		ulong expectedStorageVersion,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			EnsureDefinitionEditable(macroId);
			if (!_macros.Remove(macroId, out var removed))
				throw new KeyNotFoundException($"Production Macro '{macroId}' does not exist.");
			try
			{
				await PersistDefinitionsAsync(expectedStorageVersion, cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				_macros[macroId] = removed;
				throw;
			}

			Journal("production_macro.deleted", $"Production Macro '{removed.Name}' ({macroId}) was deleted.", macroId.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> ExecuteAsync(
		ProductionMacroId macroId,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			if (_machine.Snapshot.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired)
				throw new InvalidOperationException("A Production Macro is already active or awaiting recovery.");
			if (!_macros.TryGetValue(macroId, out var macro))
				throw new KeyNotFoundException($"Production Macro '{macroId}' does not exist.");

			var validation = await ValidateDefinitionAsync(macro, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
				throw new InvalidDataException(string.Join(" | ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

			CancelWaitWorker();
			var cueList = ProductionMacroShowControlAdapter.BuildCueList(macro);
			_machine.Arm(cueList);
			_executionId = ProductionMacroExecutionId.New();
			_lastCompletedActionId = null;
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.execution.started", $"Production Macro '{macro.Name}' ({macro.MacroId}) execution started.", _executionId.Value.Value);
			_machine.BeginGo();
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			_stateChanged();
			await ExecuteActiveActionsLockedAsync(cancellationToken).ConfigureAwait(false);
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> CancelAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			CancelWaitWorker();
			_machine.Cancel();
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.cancelled", "Production Macro execution was cancelled; already committed Production mutations were retained.", _executionId?.Value);
			_stateChanged();
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ProductionMacroWorkspaceSnapshot> AcknowledgeRecoveryAsync(
		bool resume,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureRestoredAsync(cancellationToken).ConfigureAwait(false);
			_machine.AcknowledgeRecovery(resume);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal(
				resume ? "production_macro.recovery.resume_acknowledged" : "production_macro.recovery.cancel_acknowledged",
				resume ? "Production Macro recovery resume was explicitly acknowledged." : "Production Macro recovery was cancelled.",
				_executionId?.Value);
			_stateChanged();
			if (resume)
			{
				_machine.BeginGo();
				await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
				await ExecuteActiveActionsLockedAsync(cancellationToken).ConfigureAwait(false);
			}
			return WorkspaceSnapshot();
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		CancelWaitWorker();
		var worker = _waitWorker;
		if (worker is not null)
		{
			try { await worker.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_waitCancellation?.Dispose();
		_gate.Dispose();
	}

	private async ValueTask ExecuteActiveActionsLockedAsync(CancellationToken cancellationToken)
	{
		while (_machine.Snapshot.State == ShowControlExecutionState.Executing)
		{
			var action = _machine.CurrentAction();
			if (action.Kind == ShowControlActionKind.WaitFrames)
			{
				await BeginFrameWaitLockedAsync(action, cancellationToken).ConfigureAwait(false);
				return;
			}

			Journal("production_macro.action.started", CurrentActionDetail($"Action {action.Kind} started"), action.ActionId.Value);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);

			Failure? failure;
			try
			{
				failure = await _actionExecutor(action, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				failure = new Failure("production_macro.action.exception", exception.Message);
			}

			if (failure is { } actionFailure)
			{
				_machine.Fail(actionFailure);
				await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
				Journal("production_macro.action.failed", CurrentActionDetail(actionFailure.Message), action.ActionId.Value, actionFailure);
				_stateChanged();
				return;
			}

			_lastCompletedActionId = new ProductionMacroActionId(action.ActionId.Value);
			Journal("production_macro.action.completed", CurrentActionDetail($"Action {action.Kind} completed"), action.ActionId.Value);
			_machine.CompleteCurrentAction();
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			_stateChanged();
		}
	}

	private async ValueTask BeginFrameWaitLockedAsync(ShowControlAction action, CancellationToken cancellationToken)
	{
		var frames = action.WaitFrames ?? throw new InvalidOperationException("Production Macro frame wait action has no frame count.");
		ShowControlFrameObservation observation;
		try
		{
			observation = await _frameObserver(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			var failure = new Failure("production_macro.wait.runtime_unavailable", $"Runtime frame timing is unavailable: {exception.Message}");
			_machine.Fail(failure);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.wait.failed", failure.Message, action.ActionId.Value, failure);
			_stateChanged();
			return;
		}

		ulong target;
		try { target = checked(observation.NextFrameSequence + frames); }
		catch (OverflowException)
		{
			var failure = new Failure("production_macro.wait.sequence_exhausted", "Runtime frame sequence cannot represent the requested Macro wait target.");
			_machine.Fail(failure);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.wait.failed", failure.Message, action.ActionId.Value, failure);
			_stateChanged();
			return;
		}

		_machine.BeginWait(target, observation.RuntimeHostInstanceId);
		await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
		Journal("production_macro.wait.started", $"{CurrentActionDetail("Frame wait started")}; target sequence {target}.", action.ActionId.Value);
		_stateChanged();

		CancelWaitWorker();
		_waitCancellation = new CancellationTokenSource();
		var token = _waitCancellation.Token;
		var timeout = WaitTimeout(frames, observation.FrameBudget);
		_waitWorker = Task.Run(
			() => WaitForFrameTargetAsync(target, observation.RuntimeHostInstanceId, timeout, token),
			CancellationToken.None);
	}

	private async Task WaitForFrameTargetAsync(
		ulong targetFrameSequence,
		string runtimeHostInstanceId,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (!cancellationToken.IsCancellationRequested)
		{
			ShowControlFrameObservation observation;
			try
			{
				observation = await _frameObserver(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch
			{
				if (DateTimeOffset.UtcNow >= deadline)
				{
					await FailWaitAsync(
						new Failure("production_macro.wait.timeout", "Runtime frame timing did not recover before the bounded Macro wait timeout."),
						cancellationToken).ConfigureAwait(false);
					return;
				}
				await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (!string.Equals(observation.RuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal))
			{
				await RequireRecoveryAsync(
					new Failure("production_macro.wait.runtime_restarted", "RuntimeHost changed while a Production Macro frame wait was active."),
					cancellationToken).ConfigureAwait(false);
				return;
			}

			if (observation.NextFrameSequence >= targetFrameSequence)
			{
				await CompleteWaitAsync(observation, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (DateTimeOffset.UtcNow >= deadline)
			{
				await FailWaitAsync(
					new Failure("production_macro.wait.timeout", $"Production frame sequence did not reach {targetFrameSequence} before the bounded Macro wait timeout."),
					cancellationToken).ConfigureAwait(false);
				return;
			}

			var poll = observation.FrameBudget > TimeSpan.Zero
				? TimeSpan.FromTicks(Math.Clamp(observation.FrameBudget.Ticks / 4, TimeSpan.FromMilliseconds(2).Ticks, TimeSpan.FromMilliseconds(20).Ticks))
				: TimeSpan.FromMilliseconds(10);
			await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task CompleteWaitAsync(ShowControlFrameObservation observation, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			if (actionId.HasValue)
				_lastCompletedActionId = new ProductionMacroActionId(actionId.Value);
			_machine.CompleteWait(observation.NextFrameSequence, observation.RuntimeHostInstanceId);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.wait.completed", $"Production Macro frame wait completed at Runtime sequence {observation.NextFrameSequence}.", actionId);
			_stateChanged();
			await ExecuteActiveActionsLockedAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task FailWaitAsync(Failure failure, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			_machine.Fail(failure);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.wait.failed", failure.Message, actionId, failure);
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task RequireRecoveryAsync(Failure failure, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_machine.Snapshot.State != ShowControlExecutionState.Waiting)
				return;
			var actionId = _machine.Snapshot.CurrentActionId?.Value;
			_machine.RequireRecovery(failure);
			await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
			Journal("production_macro.recovery.required", failure.Message, actionId, failure);
			_stateChanged();
		}
		finally
		{
			_gate.Release();
		}
	}

	private async ValueTask EnsureRestoredAsync(CancellationToken cancellationToken)
	{
		if (_restored)
			return;

		var control = RequireControl();
		var definitions = await _persistence.LoadProductionMacrosAsync(control.Specification, cancellationToken).ConfigureAwait(false);
		_storageVersion = definitions.Version;
		if (!string.IsNullOrWhiteSpace(definitions.Json))
		{
			foreach (var macro in ProductionMacroCanonicalSerializer.Deserialize(definitions.Json))
				_macros.Add(macro.MacroId, macro);
		}

		var execution = await _persistence.LoadProductionMacroExecutionAsync(control.Specification, cancellationToken).ConfigureAwait(false);
		_executionStorageVersion = execution.Version;
		if (!string.IsNullOrWhiteSpace(execution.Json))
		{
			var persisted = ProductionMacroExecutionSerializer.Deserialize(execution.Json);
			if (persisted.State != ProductionMacroExecutionState.Idle && persisted.MacroId is { } macroId)
			{
				if (!_macros.TryGetValue(macroId, out var macro))
					throw new InvalidDataException($"Persisted Production Macro execution references missing Macro '{macroId}'.");
				_executionId = persisted.ExecutionId;
				_lastCompletedActionId = persisted.LastCompletedActionId;
				var cueList = ProductionMacroShowControlAdapter.BuildCueList(macro);
				var restored = _machine.Restore(cueList, ToShowControlSnapshot(persisted, cueList));
				if (restored.State == ShowControlExecutionState.RecoveryRequired)
				{
					await PersistExecutionAsync(cancellationToken).ConfigureAwait(false);
					Journal("production_macro.recovery.required", "ControlHost restarted while a Production Macro action was active; automatic replay is blocked.", restored.CurrentActionId?.Value, restored.Failure);
				}
			}
		}

		_restored = true;
	}

	private async ValueTask<ProductionMacroValidationResult> ValidateDefinitionAsync(
		ProductionMacroDefinition macro,
		CancellationToken cancellationToken)
	{
		var issues = new List<ProductionMacroValidationIssue>();
		var control = RequireControl();
		MediaAssetCatalogSnapshot? catalog = null;
		if (macro.Actions.Any(action => action.Kind is ShowControlActionKind.MediaOpen or ShowControlActionKind.JumpMediaCue or ShowControlActionKind.MediaPlay or ShowControlActionKind.MediaPause or ShowControlActionKind.MediaStop))
		{
			if (_mediaCatalog is null)
			{
				issues.Add(new ProductionMacroValidationIssue(
					"production_macro.media_catalog.unavailable",
					"Media actions require the persistent Media Library."));
			}
			else
			{
				catalog = await _mediaCatalog.GetSnapshotAsync(refreshAvailability: false, cancellationToken: cancellationToken).ConfigureAwait(false);
			}
		}

		foreach (var macroAction in macro.Actions)
		{
			try
			{
				GovernedProductionActionValidator.ValidateReferenceShape(control, macroAction.Command);
			}
			catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException)
			{
				issues.Add(new ProductionMacroValidationIssue(
					"production_macro.action.reference_invalid",
					exception.Message,
					macroAction.ActionId));
				continue;
			}

			if (catalog is not null && macroAction.Command.MediaAssetId is { } assetText)
			{
				var assetId = new MediaAssetId(Identity.Parse(assetText));
				if (catalog.Assets.All(asset => asset.AssetId != assetId))
				{
					issues.Add(new ProductionMacroValidationIssue(
						"production_macro.media_asset.missing",
						$"Persistent media asset '{assetId}' is not available in the Media Library.",
						macroAction.ActionId));
				}
			}
		}

		return issues.Count == 0
			? ProductionMacroValidationResult.Valid
			: new ProductionMacroValidationResult(false, issues);
	}

	private async ValueTask PersistDefinitionsAsync(ulong expectedStorageVersion, CancellationToken cancellationToken)
	{
		if (expectedStorageVersion != _storageVersion)
			throw new InvalidOperationException($"Expected Production Macro storage version {expectedStorageVersion}, current version is {_storageVersion}.");
		var control = RequireControl();
		var json = ProductionMacroCanonicalSerializer.Serialize(
			_macros.Values
				.OrderBy(macro => macro.Name, StringComparer.Ordinal)
				.ThenBy(macro => macro.MacroId.ToString(), StringComparer.Ordinal)
				.ToArray());
		var write = await _persistence.UpdateProductionMacrosAsync(
			control.Specification,
			json,
			expectedStorageVersion,
			cancellationToken).ConfigureAwait(false);
		if (!write.Written || write.Snapshot is null)
			throw new InvalidOperationException(write.Failure?.Message ?? "Production Macro library could not be persisted.");
		_storageVersion = write.Snapshot.Version;
	}

	private async ValueTask PersistExecutionAsync(CancellationToken cancellationToken)
	{
		var control = RequireControl();
		var json = ProductionMacroExecutionSerializer.Serialize(ExecutionSnapshot());
		var write = await _persistence.UpdateProductionMacroExecutionAsync(
			control.Specification,
			json,
			_executionStorageVersion,
			cancellationToken).ConfigureAwait(false);
		if (!write.Written || write.Snapshot is null)
			throw new InvalidOperationException(write.Failure?.Message ?? "Production Macro execution state could not be persisted.");
		_executionStorageVersion = write.Snapshot.Version;
	}

	private ProductionMacroWorkspaceSnapshot WorkspaceSnapshot() =>
		new(
			_macros.Values
				.OrderBy(macro => macro.Name, StringComparer.Ordinal)
				.ThenBy(macro => macro.MacroId.ToString(), StringComparer.Ordinal)
				.ToArray(),
			_storageVersion,
			ExecutionSnapshot());

	private ProductionMacroExecutionSnapshot ExecutionSnapshot()
	{
		if (_machine.CueList is null || _executionId is null)
			return ProductionMacroExecutionSnapshot.Idle;
		var snapshot = _machine.Snapshot;
		return new ProductionMacroExecutionSnapshot(
			MapState(snapshot.State),
			_executionId,
			snapshot.CueListId.HasValue ? new ProductionMacroId(snapshot.CueListId.Value.Value) : null,
			snapshot.ActionIndex,
			snapshot.CurrentActionId.HasValue ? new ProductionMacroActionId(snapshot.CurrentActionId.Value.Value) : null,
			_lastCompletedActionId,
			snapshot.ExecutionRevision,
			snapshot.WaitTargetFrameSequence,
			snapshot.RuntimeHostInstanceId,
			snapshot.RequiresAcknowledgement,
			snapshot.Failure);
	}

	private static ShowControlExecutionSnapshot ToShowControlSnapshot(
		ProductionMacroExecutionSnapshot snapshot,
		ShowControlCueList cueList)
	{
		var state = snapshot.State switch
		{
			ProductionMacroExecutionState.Idle => ShowControlExecutionState.Idle,
			ProductionMacroExecutionState.Armed => ShowControlExecutionState.Armed,
			ProductionMacroExecutionState.Executing => ShowControlExecutionState.Executing,
			ProductionMacroExecutionState.Waiting => ShowControlExecutionState.Waiting,
			ProductionMacroExecutionState.Completed => ShowControlExecutionState.Completed,
			ProductionMacroExecutionState.Failed => ShowControlExecutionState.Failed,
			ProductionMacroExecutionState.Cancelled => ShowControlExecutionState.Cancelled,
			ProductionMacroExecutionState.RecoveryRequired => ShowControlExecutionState.RecoveryRequired,
			_ => throw new InvalidDataException($"Unsupported Production Macro execution state '{snapshot.State}'.")
		};
		var actionId = snapshot.CurrentActionId.HasValue
			? new ShowControlActionId(snapshot.CurrentActionId.Value.Value)
			: (ShowControlActionId?)null;
		return new ShowControlExecutionSnapshot(
			ShowControlContractVersion.Current,
			snapshot.ExecutionId.HasValue ? new ShowControlExecutionId(snapshot.ExecutionId.Value.Value) : ShowControlExecutionId.New(),
			cueList.CueListId,
			state,
			state == ShowControlExecutionState.Idle ? null : 0,
			snapshot.ActionIndex,
			state == ShowControlExecutionState.Idle ? null : cueList.Cues[0].CueId,
			actionId,
			snapshot.Revision,
			snapshot.WaitTargetFrameSequence,
			snapshot.RuntimeHostInstanceId,
			snapshot.RequiresAcknowledgement,
			snapshot.Failure);
	}

	private static ProductionMacroExecutionState MapState(ShowControlExecutionState state) => state switch
	{
		ShowControlExecutionState.Idle => ProductionMacroExecutionState.Idle,
		ShowControlExecutionState.Armed => ProductionMacroExecutionState.Armed,
		ShowControlExecutionState.Executing => ProductionMacroExecutionState.Executing,
		ShowControlExecutionState.Waiting => ProductionMacroExecutionState.Waiting,
		ShowControlExecutionState.Completed => ProductionMacroExecutionState.Completed,
		ShowControlExecutionState.Failed => ProductionMacroExecutionState.Failed,
		ShowControlExecutionState.Cancelled => ProductionMacroExecutionState.Cancelled,
		ShowControlExecutionState.RecoveryRequired => ProductionMacroExecutionState.RecoveryRequired,
		_ => throw new InvalidDataException($"Unsupported Show Control execution state '{state}'.")
	};

	private void EnsureDefinitionEditable(ProductionMacroId macroId)
	{
		if (_machine.CueList is null || _machine.Snapshot.CueListId != new ShowControlCueListId(macroId.Value))
			return;
		if (_machine.Snapshot.State is ShowControlExecutionState.Executing or ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired)
			throw new InvalidOperationException("The active Production Macro cannot be edited or deleted while execution is active or awaiting recovery.");
	}

	private ControlHostService RequireControl()
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			throw new InvalidOperationException("ControlHost has no committed authoritative state yet.");
		return control;
	}

	private void Journal(string code, string detail, Identity? causationId, Failure? failure = null)
	{
		var control = _controlAccessor();
		if (control is null || !control.HasAuthoritativeState)
			return;
		control.RecordProductionMacroEvent(code, detail, causationId, failure);
	}

	private string CurrentActionDetail(string prefix) =>
		$"{prefix}; state={_machine.Snapshot.State}; action={_machine.Snapshot.ActionIndex?.ToString() ?? "-"}.";

	private static TimeSpan WaitTimeout(uint frames, TimeSpan frameBudget)
	{
		var budget = frameBudget > TimeSpan.Zero ? frameBudget : TimeSpan.FromMilliseconds(40);
		var expectedTicks = Math.Min((double)TimeSpan.MaxValue.Ticks, budget.Ticks * (double)frames);
		var expected = TimeSpan.FromTicks((long)expectedTicks);
		var timeout = expected + TimeSpan.FromSeconds(5);
		if (timeout < TimeSpan.FromSeconds(10))
			timeout = TimeSpan.FromSeconds(10);
		if (timeout > TimeSpan.FromMinutes(30))
			timeout = TimeSpan.FromMinutes(30);
		return timeout;
	}

	private void CancelWaitWorker()
	{
		try { _waitCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}
}
