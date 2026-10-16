// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Client;

public enum OperatorActionOutcome { Pending, Confirmed, Rejected, Unconfirmed }

// Session-only metadata: never retain arguments, paths, payloads or exception messages.
public sealed record OperatorActionObservation(
	Guid Id, string Operation, DateTimeOffset PendingAtUtc, DateTimeOffset? CompletedAtUtc,
	OperatorActionOutcome Outcome, ulong? ObservedProductionRevision,
	ulong? ResponseProductionRevision = null, string? CommandId = null, ulong? ExpectedProductionRevision = null,
	string? ObservedProductionId = null, Guid? ClientObservationEpoch = null, DateTimeOffset? SnapshotObservedAtUtc = null,
	bool? ResponseAccepted = null);

public sealed class OperatorActionHistory
{
	public const int Capacity = 128;
	private readonly object _gate = new();
	private readonly List<OperatorActionObservation> _items = [];
	private readonly Func<DateTimeOffset> _clock;
	public OperatorActionHistory(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);
	public event EventHandler? Changed;
	public IReadOnlyList<OperatorActionObservation> Current { get { lock (_gate) return _items.ToArray(); } }

	internal Guid Begin(string operation, ulong? observedRevision, string? commandId, ulong? expectedRevision,
		string? productionId, Guid epoch, DateTimeOffset? snapshotObservedAtUtc)
	{
		var item = new OperatorActionObservation(Guid.NewGuid(), operation, _clock(), null,
			OperatorActionOutcome.Pending, observedRevision, CommandId: commandId, ExpectedProductionRevision: expectedRevision,
			ObservedProductionId: productionId, ClientObservationEpoch: epoch, SnapshotObservedAtUtc: snapshotObservedAtUtc);
		lock (_gate)
		{
			if (_items.Count == Capacity) _items.RemoveAt(0);
			_items.Add(item);
		}
		Notify();
		return item.Id;
	}

	internal void Complete(Guid id, OperatorActionOutcome outcome, ulong? revision = null, bool? accepted = null)
	{
		lock (_gate)
		{
			var index = _items.FindIndex(item => item.Id == id);
			if (index < 0) return; // An old operation may have aged out of the bounded session.
			_items[index] = _items[index] with { Outcome = outcome, CompletedAtUtc = _clock(), ResponseProductionRevision = revision, ResponseAccepted = accepted };
		}
		Notify();
	}

	private void Notify()
	{
		// Presentation observers must never turn a successful Control response into a failed command.
		foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
			try { observer(this, EventArgs.Empty); }
			catch (Exception) { }
	}
}
