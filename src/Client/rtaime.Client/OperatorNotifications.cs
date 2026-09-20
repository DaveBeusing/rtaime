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
	int OccurrenceCount = 1)
{
	public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);
	public bool RequiresOperatorAction =>
		(Condition is OperatorNotificationCondition.Active or OperatorNotificationCondition.Fatal) &&
		!string.IsNullOrWhiteSpace(OperatorAction);

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
	bool Dismiss(string key);
	int RemoveExpired(DateTimeOffset now);
}

public sealed class NotificationService : INotificationService
{
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
		if (string.IsNullOrWhiteSpace(notification.Key))
			throw new ArgumentException("Notification key is required.", nameof(notification));
		if (string.IsNullOrWhiteSpace(notification.Title))
			throw new ArgumentException("Notification title is required.", nameof(notification));
		if (string.IsNullOrWhiteSpace(notification.Detail))
			throw new ArgumentException("Notification detail is required.", nameof(notification));

		var now = _clock();
		OperatorNotification normalized;
		lock (_gate)
		{
			_current.TryGetValue(notification.Key, out var existing);
			var occurrenceCount = existing is not null && IsSameOccurrence(existing, notification)
				? checked(existing.OccurrenceCount + 1)
				: 1;
			var lifetime = notification.Persistent
				? null
				: notification.Lifetime ?? DefaultTransientLifetime;

			normalized = notification with
			{
				PublishedAtUtc = now,
				ExpiresAtUtc = lifetime is { } duration ? now + duration : null,
				OccurrenceCount = occurrenceCount
			};
			_current[normalized.Key] = normalized;
		}

		Published?.Invoke(this, new OperatorNotificationEventArgs(normalized));
	}

	public bool Dismiss(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
			return false;

		OperatorNotification? removed;
		lock (_gate)
		{
			if (!_current.Remove(key, out removed))
				return false;
		}

		Removed?.Invoke(this, new OperatorNotificationEventArgs(removed));
		return true;
	}

	public int RemoveExpired(DateTimeOffset now)
	{
		List<OperatorNotification>? removed = null;
		lock (_gate)
		{
			foreach (var notification in _current.Values.Where(candidate => candidate.IsExpired(now)).ToArray())
			{
				if (!_current.Remove(notification.Key))
					continue;
				removed ??= [];
				removed.Add(notification);
			}
		}

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
		string.Equals(existing.TechnicalDetail, incoming.TechnicalDetail, StringComparison.Ordinal);
}
