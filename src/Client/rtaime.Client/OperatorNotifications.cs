// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum OperatorNotificationCategory
{
	Information,
	Success,
	Warning,
	Error,
	Recovery
}

public enum OperatorNotificationCondition
{
	Informational,
	Resolved,
	Active,
	Fatal
}

public sealed record OperatorNotification(
	string Key,
	OperatorNotificationCategory Category,
	OperatorNotificationCondition Condition,
	string Title,
	string Detail,
	string AffectedComponent,
	string OperatorAction,
	string ProductionImpact,
	bool Persistent = false,
	string? TechnicalDetail = null,
	TimeSpan? Lifetime = null,
	DateTimeOffset PublishedAtUtc = default,
	DateTimeOffset? ExpiresAtUtc = null,
	int OccurrenceCount = 1,
	DateTimeOffset? AcknowledgedAtUtc = null,
	string TargetWorkspace = "HEALTH",
	DateTimeOffset? SnoozedUntilUtc = null)
{
	public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);
	public bool RequiresOperatorAction =>
		(Condition is OperatorNotificationCondition.Active or OperatorNotificationCondition.Fatal) &&
		!string.IsNullOrWhiteSpace(OperatorAction);

	public bool IsSnoozed(DateTimeOffset now) => Condition != OperatorNotificationCondition.Fatal && SnoozedUntilUtc > now;

	public bool IsExpired(DateTimeOffset now) =>
		!Persistent &&
		ExpiresAtUtc is { } expiresAt &&
		expiresAt <= now;
}

public sealed class OperatorNotificationEventArgs : EventArgs
{
	public OperatorNotificationEventArgs(OperatorNotification notification)
	{
		Notification = notification ?? throw new ArgumentNullException(nameof(notification));
	}

	public OperatorNotification Notification { get; }
}

public interface INotificationService
{
	IReadOnlyList<OperatorNotification> Current { get; }

	event EventHandler<OperatorNotificationEventArgs>? Published;
	event EventHandler<OperatorNotificationEventArgs>? Removed;

	void Publish(OperatorNotification notification);
	bool Acknowledge(string key) => false;
	bool Snooze(string key, TimeSpan duration) => false;
	bool Resume(string key) => false;
	bool Dismiss(string key);
	bool DismissPresentation(string key) => false;
	int RemoveExpired(DateTimeOffset now);
}

public sealed class NotificationService : INotificationService
{
	public const int Capacity = 64;
	public static readonly TimeSpan DefaultTransientLifetime = TimeSpan.FromSeconds(6);

	private readonly object _gate = new();
	private readonly Dictionary<string, OperatorNotification> _current = new(StringComparer.Ordinal);
	private readonly Func<DateTimeOffset> _clock;

	public NotificationService(Func<DateTimeOffset>? clock = null)
	{
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
	}

	public event EventHandler<OperatorNotificationEventArgs>? Published;
	public event EventHandler<OperatorNotificationEventArgs>? Removed;

	public IReadOnlyList<OperatorNotification> Current
	{
		get
		{
			lock (_gate)
			{
				return _current.Values
					.OrderByDescending(notification => notification.PublishedAtUtc)
					.ToArray();
			}
		}
	}

	public void Publish(OperatorNotification notification)
	{
		ArgumentNullException.ThrowIfNull(notification);
		if (string.IsNullOrWhiteSpace(notification.Key) || notification.Key.Length > 128)
			throw new ArgumentException("Notification key is required.", nameof(notification));
		if (string.IsNullOrWhiteSpace(notification.Title))
			throw new ArgumentException("Notification title is required.", nameof(notification));
		if (string.IsNullOrWhiteSpace(notification.Detail))
			throw new ArgumentException("Notification detail is required.", nameof(notification));

		notification = notification with
		{
			Title = Bound(notification.Title, 256), Detail = Bound(notification.Detail, 2048),
			AffectedComponent = Bound(notification.AffectedComponent, 256),
			OperatorAction = Bound(notification.OperatorAction, 2048), ProductionImpact = Bound(notification.ProductionImpact, 2048),
			TechnicalDetail = notification.TechnicalDetail is null ? null : Bound(notification.TechnicalDetail, 8192),
			TargetWorkspace = notification.TargetWorkspace is "LIVE" or "EDIT" or "MEDIA" or "SCENES" or "COMPOSITING" or "OUTPUTS" or "SETTINGS" ? notification.TargetWorkspace : "HEALTH"
		};
		var now = _clock();
		OperatorNotification normalized;
		OperatorNotification? evicted = null;
		lock (_gate)
		{
			_current.TryGetValue(notification.Key, out var existing);
			var occurrenceCount = existing is not null && IsSameOccurrence(existing, notification)
				? existing.OccurrenceCount == int.MaxValue ? int.MaxValue : existing.OccurrenceCount + 1
				: 1;
			TimeSpan? lifetime = notification.Persistent
				? null
				: notification.Lifetime ?? DefaultTransientLifetime;

			normalized = notification with
			{
				PublishedAtUtc = now,
				ExpiresAtUtc = lifetime is { } duration ? now + duration : null,
				OccurrenceCount = occurrenceCount,
				AcknowledgedAtUtc = existing is not null && IsSameOccurrence(existing, notification) ? existing.AcknowledgedAtUtc : null,
				SnoozedUntilUtc = existing is not null && IsSameOccurrence(existing, notification) && existing.IsSnoozed(now) ? existing.SnoozedUntilUtc : null
			};
			if (existing is null && _current.Count == Capacity)
			{
				var oldest = _current.Values.OrderBy(Priority).ThenBy(item => item.PublishedAtUtc).First();
				if (Priority(normalized) < Priority(oldest)) return;
				evicted = oldest;
				_current.Remove(oldest.Key);
			}
			_current[normalized.Key] = normalized;
		}

		if (evicted is not null) Removed?.Invoke(this, new OperatorNotificationEventArgs(evicted));
		Published?.Invoke(this, new OperatorNotificationEventArgs(normalized));
	}

	private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
	private static int Priority(OperatorNotification item) =>
		(item.Condition == OperatorNotificationCondition.Fatal ? 4 : item.Persistent ? 2 : 0) + (item.AcknowledgedAtUtc is null ? 1 : 0);

	public bool Acknowledge(string key)
	{
		OperatorNotification? updated;
		lock (_gate)
		{
			if (!_current.TryGetValue(key, out var current) || current.AcknowledgedAtUtc is not null) return false;
			updated = current with { AcknowledgedAtUtc = _clock() };
			_current[key] = updated;
		}
		Published?.Invoke(this, new OperatorNotificationEventArgs(updated));
		return true;
	}

	// Snooze affects reminders only. The condition remains in Current and active-problem projections.
	public bool Snooze(string key, TimeSpan duration)
	{
		if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(5)) return false;
		OperatorNotification updated;
		lock (_gate)
		{
			if (!_current.TryGetValue(key, out var current) || current.Condition == OperatorNotificationCondition.Fatal) return false;
			updated = current with { SnoozedUntilUtc = _clock() + duration };
			_current[key] = updated;
		}
		Published?.Invoke(this, new OperatorNotificationEventArgs(updated));
		return true;
	}

	public bool Resume(string key)
	{
		OperatorNotification updated;
		lock (_gate)
		{
			if (!_current.TryGetValue(key, out var current) || current.SnoozedUntilUtc is null) return false;
			updated = current with { SnoozedUntilUtc = null };
			_current[key] = updated;
		}
		Published?.Invoke(this, new OperatorNotificationEventArgs(updated));
		return true;
	}

	public bool Dismiss(string key) => DismissCore(key, allowFatal: true);

	public bool DismissPresentation(string key) => DismissCore(key, allowFatal: false);

	private bool DismissCore(string key, bool allowFatal)
	{
		if (string.IsNullOrWhiteSpace(key))
			return false;

		OperatorNotification? removed;
		lock (_gate)
		{
			if (!_current.TryGetValue(key, out var current) ||
				(!allowFatal && current.Condition == OperatorNotificationCondition.Fatal))
				return false;
			_current.Remove(key, out removed);
		}

		Removed?.Invoke(this, new OperatorNotificationEventArgs(removed));
		return true;
	}

	public int RemoveExpired(DateTimeOffset now)
	{
		List<OperatorNotification>? removed = null;
		List<OperatorNotification> resumed = [];
		lock (_gate)
		{
			foreach (var notification in _current.Values.Where(item => item.SnoozedUntilUtc is { } until && until <= now).ToArray())
			{
				var updated = notification with { SnoozedUntilUtc = null };
				_current[updated.Key] = updated; resumed.Add(updated);
			}
			foreach (var notification in _current.Values.Where(candidate => candidate.IsExpired(now)).ToArray())
			{
				if (!_current.Remove(notification.Key))
					continue;
				removed ??= [];
				removed.Add(notification);
			}
		}

		foreach (var notification in resumed) Published?.Invoke(this, new OperatorNotificationEventArgs(notification));
		if (removed is null)
			return 0;

		foreach (var notification in removed)
			Removed?.Invoke(this, new OperatorNotificationEventArgs(notification));
		return removed.Count;
	}

	private static bool IsSameOccurrence(OperatorNotification existing, OperatorNotification incoming) =>
		existing.Category == incoming.Category &&
		existing.Condition == incoming.Condition &&
		existing.Persistent == incoming.Persistent &&
		string.Equals(existing.Title, incoming.Title, StringComparison.Ordinal) &&
		string.Equals(existing.Detail, incoming.Detail, StringComparison.Ordinal) &&
		string.Equals(existing.AffectedComponent, incoming.AffectedComponent, StringComparison.Ordinal) &&
		string.Equals(existing.OperatorAction, incoming.OperatorAction, StringComparison.Ordinal) &&
		string.Equals(existing.ProductionImpact, incoming.ProductionImpact, StringComparison.Ordinal) &&
		string.Equals(existing.TechnicalDetail, incoming.TechnicalDetail, StringComparison.Ordinal) &&
		string.Equals(existing.TargetWorkspace, incoming.TargetWorkspace, StringComparison.Ordinal);
}
