// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ShowReadinessOperatorTests
{
	[Fact]
	public async Task Slow_dispatcher_coalesces_evidence_and_history_without_unbounded_work_or_persisted_state()
	{
		await using var owner = new OperatorViewModel();
		var client = new OperatorControlClient(new Transport());
		var provider = new Provider();
		var context = new QueuedContext();
		using var notifications = new OperatorNotificationCenterViewModel(new NotificationService(), owner, context);
		using var model = new ShowReadinessViewModel(client, owner, provider, notifications, _ => { }, context);
		for (var index = 0; index < 5000; index++)
		{
			provider.Publish();
			await client.CreateShowTemplateAsync("sensitive authored name", null);
		}
		Assert.Single(context.Pending);
		context.Drain();
		Assert.Equal(OperatorActionHistory.Capacity, model.History.Count);
		Assert.All(model.Checks, check => Assert.Equal("UNVERIFIED", check.State));
		Assert.True(model.RefreshChecksCommand.CanExecute(null));
		model.RefreshChecksCommand.Execute(null);
		context.Drain();
		var before = model.History.Count;
		model.Dispose();
		await client.CreateShowTemplateAsync("later", null);
		provider.Publish();
		context.Drain();
		Assert.Equal(before, model.History.Count);
	}

	[Fact]
	public async Task Acknowledge_dismiss_and_review_preserve_incident_state_and_expanded_details()
	{
		await using var owner = new OperatorViewModel();
		var service = new NotificationService();
		var context = new QueuedContext();
		string? workspace = null;
		using var center = new OperatorNotificationCenterViewModel(service, owner, context, value => workspace = value);
		var fault = new OperatorNotification("output.failure", OperatorNotificationCategory.Error, OperatorNotificationCondition.Active,
			"Output fault", "Transport unavailable.", "Output", "Review Outputs.", "Output transport only.", true,
			TechnicalDetail: "No credentials.", TargetWorkspace: "OUTPUTS");
		service.Publish(fault); context.Drain();
		var item = Assert.Single(center.Items);
		item.ToggleTechnicalDetailsCommand.Execute(null);
		service.Publish(fault); context.Drain();
		Assert.Same(item, Assert.Single(center.Items));
		Assert.True(item.IsTechnicalDetailsVisible);
		item.AcknowledgeCommand.Execute(null); context.Drain();
		Assert.True(item.IsAcknowledged);
		Assert.False(item.AcknowledgeCommand.CanExecute(null));
		Assert.Equal("ACTIVE", item.Condition);
		Assert.Single(center.ActiveProblems);
		item.SnoozeCommand.Execute(null); context.Drain();
		Assert.True(item.IsSnoozed);
		Assert.False(item.SnoozeCommand.CanExecute(null));
		Assert.True(item.ResumeCommand.CanExecute(null));
		Assert.Single(center.ActiveProblems);
		Assert.Same(item, Assert.Single(center.Groups.Single(group => group.Label == "ERROR").Items));
		item.ResumeCommand.Execute(null); context.Drain();
		Assert.False(item.IsSnoozed);
		item.NavigateCommand.Execute(null);
		Assert.Equal("OUTPUTS", workspace);
		item.DismissCommand.Execute(null); context.Drain();
		Assert.Empty(center.Items);
		Assert.Equal(OperatorProgramSafetyStates.Unknown, owner.ProgramSafety);
	}

	[Fact]
	public async Task Severity_groups_keep_fatal_and_snoozed_conditions_visible_and_queued_updates_bounded()
	{
		await using var owner = new OperatorViewModel();
		var service = new NotificationService();
		var context = new QueuedContext();
		using var center = new OperatorNotificationCenterViewModel(service, owner, context);
		var fatal = new OperatorNotification("fatal", OperatorNotificationCategory.Information, OperatorNotificationCondition.Fatal,
			"Fatal", "Evidence", "Runtime", "Review Health", "Program blocked", true);
		service.Publish(fatal);
		for (var i = 0; i < 5000; i++)
			service.Publish(fatal with { Key = "transient-" + i, Condition = OperatorNotificationCondition.Informational, Persistent = false });
		Assert.Single(context.Pending);
		context.Drain();
		Assert.Equal(6, center.Groups.Count);
		var item = Assert.Single(center.Groups[0].Items);
		Assert.Equal("FATAL", center.Groups[0].Label);
		Assert.False(item.SnoozeCommand.CanExecute(null));
		Assert.False(item.DismissCommand.CanExecute(null));
		item.DismissCommand.Execute(null); context.Drain();
		item.AcknowledgeCommand.Execute(null); context.Drain();
		Assert.Single(center.ActiveProblems);
		Assert.Contains(center.Items, row => row.Key == "fatal");
		Assert.Equal(center.Items.Count, center.Groups.Sum(group => group.Items.Count));
		Assert.InRange(center.Toasts.Count, 0, 4);
		var transient = center.Items.First(row => row.Key != "fatal");
		transient.SnoozeCommand.Execute(null); context.Drain();
		Assert.DoesNotContain(center.Toasts, row => row.Key == transient.Key);
		Assert.Contains(center.Items, row => row.Key == transient.Key && row.IsSnoozed);
		center.Dispose(); context.Drain();
		service.Publish(fatal);
		Assert.Empty(context.Pending);
	}

	[Fact]
	public void Changing_a_notification_to_fatal_rechecks_dismissal_at_execution()
	{
		var service = new NotificationService();
		var fault = new OperatorNotification("condition", OperatorNotificationCategory.Error,
			OperatorNotificationCondition.Active, "Failure", "Evidence", "Runtime", "Review Health", "Unknown", true);
		service.Publish(fault);
		var item = new OperatorNotificationItemViewModel(service.Current.Single(), service);
		Assert.True(item.DismissCommand.CanExecute(null));
		var fatal = fault with { Condition = OperatorNotificationCondition.Fatal };
		service.Publish(fatal); item.Apply(service.Current.Single());
		Assert.False(item.DismissCommand.CanExecute(null));
		item.DismissCommand.Execute(null);
		Assert.Single(service.Current);
		Assert.Equal(OperatorNotificationCondition.Fatal, service.Current.Single().Condition);
	}

	[Fact]
	public void History_rows_never_invent_acceptance_commitment_or_server_epoch()
	{
		var pending = new OperatorActionObservation(Guid.NewGuid(), "CutAsync", DateTimeOffset.UtcNow, null,
			OperatorActionOutcome.Pending, 1);
		Assert.Contains("NO RESPONSE EVIDENCE", new OperatorActionHistoryRow(pending).ResponseEvidence);
		var receipt = pending with { Outcome = OperatorActionOutcome.Confirmed, CompletedAtUtc = DateTimeOffset.UtcNow };
		Assert.Contains("not exposed", new OperatorActionHistoryRow(receipt).ResponseEvidence);
		Assert.Contains("server epoch not exposed", new OperatorActionHistoryRow(receipt).Provenance);
		Assert.Contains("ACCEPTED", new OperatorActionHistoryRow(receipt with { ResponseAccepted = true }).ResponseEvidence);
		Assert.Contains("REJECTED", new OperatorActionHistoryRow(receipt with { ResponseAccepted = false }).ResponseEvidence);
	}

	internal static async Task VerifyWindowAsync(MainWindow main, Dispatcher dispatcher)
	{
		var workspace = main.Shell.SelectedWorkspace;
		var owner = Assert.IsType<OperatorViewModel>(main.DataContext);
		var program = owner.ProgramSourceId;
		var history = main.ShowReadiness.History.Count;
		try
		{
			main.OpenShowChecksCommand.Execute(null);
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
			var window = Assert.IsType<ShowReadinessWindow>(main.ShowReadinessWindow);
			Assert.True(window.IsVisible);
			Assert.Same(main, window.Owner);
			Assert.True(window.IsKeyboardFocusWithin);
			var export = Assert.IsType<RtaimeButton>(window.FindName("ExportChecksSupportButton"));
			Assert.Same(main.Startup.ExportSupportBundleCommand, export.Command);
			Assert.Equal(10, main.ShowReadiness.Checks.Count);
			main.MediaPool.SetCatalogSnapshot(MediaAssetCatalogSnapshot.Empty);
			var catalogueObservedAt = main.MediaPool.CatalogObservedAtUtc;
			main.ShowReadiness.RefreshChecksCommand.Execute(null);
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
			Assert.Equal(catalogueObservedAt, main.MediaPool.CatalogObservedAtUtc);
			Assert.Contains("no server request", main.ShowReadiness.ReadStatus);
			// Exercise the compiled grouped templates and command bindings with an isolated notification source.
			var service = new NotificationService();
			var context = new DispatcherSynchronizationContext(dispatcher);
			using (var center = new OperatorNotificationCenterViewModel(service, owner, context))
			using (var checksModel = new ShowReadinessViewModel(new OperatorControlClient(new Transport()), owner,
				new Provider(), center, _ => { }, context))
			{
				window.DataContext = checksModel;
				var fault = new OperatorNotification("qualification.output", OperatorNotificationCategory.Error, OperatorNotificationCondition.Active,
					"Output fault", "Observed fault", "Output", "Review Outputs", "Output transport only", true);
				service.Publish(fault);
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
				var row = Assert.Single(center.ActiveProblems);
				var pause = Descendants(window).OfType<RtaimeButton>().Single(button => Equals(button.Content, "Pause reminders 5 min"));
				Assert.Same(row.SnoozeCommand, pause.Command);
				Assert.True(pause.IsEnabled); pause.Command.Execute(null);
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
				Assert.True(row.IsSnoozed); Assert.Single(center.ActiveProblems); Assert.False(pause.IsEnabled);
				var resume = Descendants(window).OfType<RtaimeButton>().Single(button => Equals(button.Content, "Resume reminders"));
				Assert.Same(row.ResumeCommand, resume.Command); Assert.True(resume.IsEnabled); resume.Command.Execute(null);
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
				Assert.False(row.IsSnoozed);
				service.Publish(fault with { Category = OperatorNotificationCategory.Information, Condition = OperatorNotificationCondition.Fatal });
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
				Assert.Same(row, Assert.Single(center.Groups[0].Items));
				pause = Descendants(window).OfType<RtaimeButton>().Single(button => Equals(button.Content, "Pause reminders 5 min"));
				Assert.False(pause.IsEnabled);
				window.DataContext = main.ShowReadiness;
			}
			await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
			foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
			{
				window.Width = 960; window.Height = 500;
				((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
				await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
				Assert.True(window.ActualWidth > 0 && window.ActualHeight > 0);
				var checks = Assert.IsType<System.Windows.Controls.ItemsControl>(window.FindName("PreflightChecks"));
				Assert.True(checks.ActualWidth > 0);
				var buttons = Descendants(window).OfType<RtaimeButton>().Where(button => button.IsVisible).ToArray();
				Assert.NotEmpty(buttons);
				Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
				Assert.All(buttons, button => Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0));
			}
			main.ShowReadiness.Checks.Single(check => check.Id == "assets").NavigateCommand.Execute(null);
			Assert.Null(main.ShowReadinessWindow);
			Assert.Equal("MEDIA", main.Shell.SelectedWorkspace);
			main.CommandPalette.Query = "show preflight";
			main.CommandPalette.Selected = main.CommandPalette.Results.Single(entry => entry.Id == "show-checks");
			Assert.True(main.CommandPalette.ExecuteSelected(true));
			await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
			window = Assert.IsType<ShowReadinessWindow>(main.ShowReadinessWindow);
			var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
				{ RoutedEvent = Keyboard.PreviewKeyDownEvent };
			window.RaiseEvent(escape);
			Assert.True(escape.Handled);
			Assert.Null(main.ShowReadinessWindow);
			Assert.Equal(program, owner.ProgramSourceId);
			Assert.Equal(history, main.ShowReadiness.History.Count);
			Console.WriteLine("SHOW CHECKS UI PASS: real BAML, keyboard open/close, workspace links, automation names, 960x500 at injected 100/125/150/200% layout scales; no production mutation.");
		}
		finally { main.ShowReadinessWindow?.Close(); main.Shell.SelectWorkspace(workspace); }
	}
	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index); yield return child;
			foreach (var nested in Descendants(child)) yield return nested;
		}
	}
	private sealed class Provider : IHealthSnapshotProvider
	{
		public event EventHandler<HealthSnapshotChangedEventArgs>? Changed;
		public IReadOnlyList<SubsystemHealthSnapshot> GetCurrent() => [];
		public void Publish() => Changed?.Invoke(this, new([]));
	}
	private sealed class QueuedContext : SynchronizationContext
	{
		public ConcurrentQueue<Action> Pending { get; } = new();
		public override void Post(SendOrPostCallback action, object? state) => Pending.Enqueue(() => action(state));
		public void Drain() { while (Pending.TryDequeue(out var action)) action(); }
	}
	private sealed class Transport : IOperatorControlTransport
	{
		public ValueTask<string> CreateShowTemplateAsync(string name, string? cueListId, CancellationToken cancellationToken = default) => ValueTask.FromResult(name);
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
