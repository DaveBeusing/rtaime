// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Provider.Inference;

namespace rtaime.AIHost;

/// <summary>
/// AIHost composition boundary. It owns governed inference execution only;
/// it has no Control or Runtime dependency and therefore no production authority.
/// Host shutdown cancels and drains in-flight inference before resources are released.
/// </summary>
public sealed class AIHostService : IAsyncDisposable
{
	private readonly object _lifecycleGate = new();
	private readonly CancellationTokenSource _shutdown = new();
	private int _activeExecutions;
	private TaskCompletionSource? _drained;
	private bool _disposeStarted;
	private Task? _disposeTask;

	public AIHostService(GovernedInferenceRuntime runtime)
	{
		Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
	}

	public GovernedInferenceRuntime Runtime { get; }

	public IReadOnlyList<InferenceCapabilityDescriptor> Capabilities => Runtime.Capabilities;

	public AIExecutionSnapshot Snapshot => Runtime.Snapshot;

	public int ActiveExecutions
	{
		get
		{
			lock (_lifecycleGate)
				return _activeExecutions;
		}
	}

	public async ValueTask<GovernedInferenceExecutionResult> ExecuteAsync(
		GovernedInferenceExecutionRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		lock (_lifecycleGate)
		{
			ObjectDisposedException.ThrowIf(_disposeStarted, this);
			if (_activeExecutions == 0)
				_drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_activeExecutions++;
		}

		try
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
			return await Runtime.ExecuteAsync(request, linked.Token).ConfigureAwait(false);
		}
		finally
		{
			lock (_lifecycleGate)
			{
				_activeExecutions--;
				if (_activeExecutions == 0)
					_drained?.TrySetResult();
			}
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_lifecycleGate)
		{
			if (_disposeTask is not null)
				return new ValueTask(_disposeTask);

			_disposeStarted = true;
			_shutdown.Cancel();
			var drain = _activeExecutions == 0
				? Task.CompletedTask
				: _drained?.Task ?? Task.CompletedTask;
			_disposeTask = CompleteDisposeAsync(drain);
			return new ValueTask(_disposeTask);
		}
	}

	public static AIHostService CreateManagedReference(InferenceRuntimeLimits? limits = null) =>
		new(new GovernedInferenceRuntime(
			new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider() },
			limits ?? InferenceRuntimeLimits.ReferenceV1));

	private async Task CompleteDisposeAsync(Task drain)
	{
		await drain.ConfigureAwait(false);
		_shutdown.Dispose();
	}
}
