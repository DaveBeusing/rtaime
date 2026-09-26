// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorShutdownLifecycleTests
{
	[Fact]
	public async Task Dispose_ignores_audio_polling_callbacks_already_queued_for_the_dispatcher()
	{
		var synchronizationContext = new QueuedSynchronizationContext();
		var viewModel = new OperatorViewModel(
			new OperatorControlClient(new FailingOperatorTransport()),
			synchronizationContext: synchronizationContext);

		viewModel.StartAudioMetering();
		await synchronizationContext.WaitForPostAsync(TimeSpan.FromSeconds(2));

		await viewModel.DisposeAsync();

		var exception = Record.Exception(synchronizationContext.Drain);
		Assert.Null(exception);
	}

	private sealed class QueuedSynchronizationContext : SynchronizationContext
	{
		private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
		private readonly TaskCompletionSource<bool> _posted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override void Post(SendOrPostCallback callback, object? state)
		{
			_callbacks.Enqueue((callback, state));
			_posted.TrySetResult(true);
		}

		public Task WaitForPostAsync(TimeSpan timeout) => _posted.Task.WaitAsync(timeout);

		public void Drain()
		{
			while (_callbacks.TryDequeue(out var work))
				work.Callback(work.State);
		}
	}

	private sealed class FailingOperatorTransport : IOperatorControlTransport
	{
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorStatusSnapshot>(
				new InvalidOperationException("Simulated Control synchronization failure."));

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
	}
}
