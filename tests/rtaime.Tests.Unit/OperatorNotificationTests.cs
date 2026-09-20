// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OperatorNotificationTests
{
	[Fact]
	public void Transient_notification_expires_after_its_lifetime()
	{
		var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
		var service = new NotificationService(() => now);

		service.Publish(new OperatorNotification(
			"media.imported",
			OperatorNotificationCategory.Success,
			OperatorNotificationCondition.Informational,
			"Media imported",
			"The selected media is available in the project.",
			"Media",
			"No action is required.",
			"Production state is unchanged.",
			Lifetime: TimeSpan.FromSeconds(5)));

		Assert.Single(service.Current);
		now = now.AddSeconds(6);

		Assert.Equal(1, service.RemoveExpired(now));
		Assert.Empty(service.Current);
	}

	[Fact]
	public void Repeated_warning_is_deduplicated_and_counts_occurrences()
	{
		var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
		var service = new NotificationService(() => now);
		var warning = new OperatorNotification(
			"output.offline",
			OperatorNotificationCategory.Warning,
			OperatorNotificationCondition.Active,
			"Output is offline",
			"Output 2 is offline.",
			"Output 2",
			"Review the output route.",
			"Program remains available on the confirmed output.",
			Persistent: true);

		service.Publish(warning);
		now = now.AddMilliseconds(100);
		service.Publish(warning);

		var current = Assert.Single(service.Current);
		Assert.Equal(2, current.OccurrenceCount);
		Assert.True(current.Persistent);
	}

	[Fact]
	public void Persistent_error_does_not_expire()
	{
		var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
		var service = new NotificationService(() => now);

		service.Publish(new OperatorNotification(
			"recording.failure",
			OperatorNotificationCategory.Error,
			OperatorNotificationCondition.Active,
			"Recording failed",
			"Program recording is impaired.",
			"Recording",
			"Review recording diagnostics.",
			"Program remains available.",
			Persistent: true));

		now = now.AddDays(1);
		Assert.Equal(0, service.RemoveExpired(now));
		Assert.Single(service.Current);
	}

	[Fact]
	public void Error_recovering_success_transition_reuses_one_notification_key()
	{
		var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
		var service = new NotificationService(() => now);

		service.Publish(State(
			OperatorNotificationCategory.Error,
			OperatorNotificationCondition.Active,
			"Runtime failed",
			persistent: true));
		now = now.AddSeconds(1);
		service.Publish(State(
			OperatorNotificationCategory.Recovery,
			OperatorNotificationCondition.Active,
			"Runtime recovering",
			persistent: true));
		now = now.AddSeconds(1);
		service.Publish(State(
			OperatorNotificationCategory.Success,
			OperatorNotificationCondition.Resolved,
			"Runtime recovered",
			persistent: false));

		var current = Assert.Single(service.Current);
		Assert.Equal(OperatorNotificationCategory.Success, current.Category);
		Assert.Equal(OperatorNotificationCondition.Resolved, current.Condition);
		Assert.False(current.Persistent);
		Assert.Equal(1, current.OccurrenceCount);
	}

	[Fact]
	public async Task Missing_clean_shutdown_marker_restores_only_safe_workspace_state()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "operator-session.json");
			var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
			var first = new OperatorSessionRecoveryStore(path, () => now);
			var initial = first.BeginSession(new OperatorSafeSessionState("COMPOSITING"));
			Assert.False(initial.PreviousSessionEndedUnexpectedly);

			now = now.AddMinutes(1);
			var afterCrash = new OperatorSessionRecoveryStore(path, () => now)
				.BeginSession(new OperatorSafeSessionState("LIVE"));

			Assert.True(afterCrash.PreviousSessionEndedUnexpectedly);
			Assert.Equal("COMPOSITING", afterCrash.SafeState.SelectedWorkspace);
			Assert.True(afterCrash.PersistenceAvailable);

			var persistedProperties = typeof(OperatorSafeSessionState)
				.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
				.Select(property => property.Name)
				.ToArray();
			Assert.Equal(["SelectedWorkspace"], persistedProperties);

			var persistedJson = File.ReadAllText(path);
			Assert.Contains("\"SelectedWorkspace\"", persistedJson, StringComparison.Ordinal);
			Assert.DoesNotContain("\"Output\"", persistedJson, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("\"Recording\"", persistedJson, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("\"Program\"", persistedJson, StringComparison.OrdinalIgnoreCase);

			await new OperatorSessionRecoveryStore(path, () => now)
				.MarkCleanShutdownAsync(new OperatorSafeSessionState("HEALTH"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Clean_shutdown_does_not_trigger_recovery_on_next_start()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "operator-session.json");
			var now = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.Zero);
			var store = new OperatorSessionRecoveryStore(path, () => now);
			store.BeginSession(new OperatorSafeSessionState("LIVE"));
			Assert.True(await store.MarkCleanShutdownAsync(new OperatorSafeSessionState("OUTPUTS")));

			now = now.AddMinutes(1);
			var next = new OperatorSessionRecoveryStore(path, () => now)
				.BeginSession(new OperatorSafeSessionState("MEDIA"));

			Assert.False(next.PreviousSessionEndedUnexpectedly);
			Assert.False(next.StateWasCorrupted);
			Assert.Equal("MEDIA", next.SafeState.SelectedWorkspace);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Corrupted_session_state_falls_back_without_restoring_runtime_actions()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "operator-session.json");
			File.WriteAllText(path, "{ this is not valid json");

			var store = new OperatorSessionRecoveryStore(path);
			var recovery = store.BeginSession(new OperatorSafeSessionState("MEDIA"));

			Assert.True(recovery.StateWasCorrupted);
			Assert.False(recovery.PreviousSessionEndedUnexpectedly);
			Assert.True(recovery.PersistenceAvailable);
			Assert.Equal("MEDIA", recovery.SafeState.SelectedWorkspace);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static OperatorNotification State(
		OperatorNotificationCategory category,
		OperatorNotificationCondition condition,
		string title,
		bool persistent) =>
		new(
			"runtime.recovery",
			category,
			condition,
			title,
			"Runtime state transition.",
			"Runtime",
			"No action is required.",
			"Production follows authoritative readiness.",
			persistent);

	private static string CreateTemporaryDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-notification-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
