// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
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
		Assert.All(model.Checks, check => Assert.Equal("UNKNOWN", check.State));
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
		item.NavigateCommand.Execute(null);
		Assert.Equal("OUTPUTS", workspace);
		item.DismissCommand.Execute(null); context.Drain();
		Assert.Empty(center.Items);
		Assert.Equal(OperatorProgramSafetyStates.Unknown, owner.ProgramSafety);
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
			Assert.Equal(6, main.ShowReadiness.Checks.Count);
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
