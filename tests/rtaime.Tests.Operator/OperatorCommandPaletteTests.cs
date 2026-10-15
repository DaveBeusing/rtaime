// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorCommandPaletteTests
{
	[Fact]
	public void Search_is_bounded_fuzzy_deterministic_and_preserves_disabled_results()
	{
		var commands = Enumerable.Range(0, 96).Select(i => new OperatorPaletteCommand("command-" + i, "Workspace " + i.ToString("D2"), "Navigation", new Probe())).ToArray();
		using var model = new OperatorCommandPaletteViewModel(new([]), commands);
		Assert.Equal(64, model.Results.Count);
		model.Query = "wksp 09";
		Assert.NotEmpty(model.Results);
		Assert.Equal("command-9", model.Selected!.Id);
		model.Query = new string('x', 200);
		Assert.Equal(128, model.Query.Length);
		Assert.Empty(model.Results);
		Assert.False(model.ExecuteSelected(true));
		Assert.Throws<ArgumentException>(() => new OperatorCommandPaletteViewModel(new([]), [commands[0], commands[0]]));
		Assert.Throws<ArgumentException>(() => new OperatorCommandPaletteViewModel(new([]), commands.Append(new("overflow", "Overflow", "Navigation", new Probe()))));
	}

	[Fact]
	public void Production_execution_rechecks_authority_and_requires_explicit_activation()
	{
		var action = new Probe();
		string? reason = null;
		using var model = new OperatorCommandPaletteViewModel(new([]), [
			new("cut", "CUT Preview to Program", "Production", action, "existing parameter", true, () => reason)]);
		Assert.False(model.ExecuteSelected(true));
		Assert.Equal(0, action.Calls);
		reason = "Stale state";
		model.Refresh();
		Assert.Contains("Stale", model.SelectedState);
		Assert.False(model.ExecuteCommand.CanExecute(null));
		Assert.False(model.ExecuteSelected(false));
		reason = null; action.Allowed = false; action.Notify();
		Assert.False(model.ExecuteSelected(false));
		action.Allowed = true; action.Notify();
		Assert.True(model.ExecuteCommand.CanExecute(null));
		Assert.Contains("Execute button", model.SelectedState);
		Assert.True(model.ExecuteSelected(false));
		Assert.Equal(1, action.Calls);
		Assert.Equal("existing parameter", action.Parameter);
		reason = "Disconnected";
		Assert.False(model.ExecuteSelected(false)); // no refresh required for the boundary check
		Assert.Equal(1, action.Calls);
	}

	[Fact]
	public async Task Invalid_import_retains_active_profile_and_persisted_defaults_and_legacy_import_upgrades()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-palette-" + Guid.NewGuid().ToString("N"));
		var store = new OperatorShortcutProfileStore(Path.Combine(root, "preferences.json"));
		var registry = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
		try
		{
			using var model = new OperatorCommandPaletteViewModel(registry, [], store);
			await model.LoadPreferencesAsync();
			await model.ApplyAndSaveAsync(new(2, "Desk", [new("draft", Key.Q, ModifierKeys.Control)]));
			var snapshot = registry.Definitions;
			var imported = Path.Combine(root, "preset.json");
			await File.WriteAllTextAsync(imported, "{bad");
			await model.ImportProfileAsync(imported);
			Assert.Same(snapshot, registry.Definitions);
			Assert.Contains("Import rejected", model.Status);
			await store.WriteAsync(new(2, "Unsafe", [new("cut", Key.Q, ModifierKeys.Control)]), imported);
			await model.ImportProfileAsync(imported);
			Assert.Same(snapshot, registry.Definitions);
			Assert.Equal("Desk", (await store.ReadAsync()).Name);
			await store.WriteAsync(new(1, null, [new("draft", Key.E, ModifierKeys.Control)]), imported);
			await model.ImportProfileAsync(imported);
			Assert.Equal(Key.E, registry.Definitions[0].Key);
			Assert.Equal(Key.E, model.EditingKey);
			Assert.Equal(2, (await store.ReadAsync()).Version);
			await model.ExportProfileAsync(imported);
			Assert.Equal("Imported shortcuts", (await store.ReadAsync(imported)).Name);
			model.ResetShortcutsCommand.Execute(null);
			await model.FlushAsync();
			Assert.Equal(Key.B, registry.Definitions[0].Key);
			Assert.Equal(Key.B, model.EditingKey);
			Assert.Empty((await store.ReadAsync()).Bindings);
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
	}

	[Fact]
	public async Task Failed_save_reports_session_only_and_corrupt_startup_does_not_overwrite_the_file()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-palette-fault-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var registry = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
		try
		{
			// A directory as destination causes atomic replacement to fail after the temporary write.
			using (var model = new OperatorCommandPaletteViewModel(registry, [], new(root)))
			{
				await model.ApplyAndSaveAsync(new(2, "Desk", [new("draft", Key.Q, ModifierKeys.Control)]));
				Assert.Contains("save failed", model.Status);
				Assert.Equal(Key.Q, registry.Definitions[0].Key);
				Assert.Empty(Directory.GetFiles(root));
			}
			var path = Path.Combine(root, "preferences.json");
			await File.WriteAllTextAsync(path, "{bad");
			var fresh = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
			using var recovered = new OperatorCommandPaletteViewModel(fresh, [], new(path));
			await recovered.LoadPreferencesAsync();
			Assert.Equal(Key.B, fresh.Definitions[0].Key);
			Assert.Contains("defaults retained", recovered.Status);
			Assert.Equal("{bad", await File.ReadAllTextAsync(path));
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
	}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_editor_cannot_execute_or_navigate_a_hidden_command(bool production)
    {
        var action = new Probe();
        using var model = new OperatorCommandPaletteViewModel(new([]),
            [new("action", "Action", "Commands", action, RequiresExplicitButton: production)]);
        var selected = model.Selected;
        model.ToggleProfilesCommand.Execute(null);
        Assert.False(model.IsBrowsingCommands);
        Assert.Contains("paused", model.SelectedState);
        Assert.False(model.ExecuteCommand.CanExecute(null));
        Assert.False(model.ExecuteSelected(false));
        Assert.False(model.ExecuteSelected(true));
        model.MoveSelection(1);
        Assert.Same(selected, model.Selected);
        Assert.Equal(0, action.Calls);
        model.ToggleProfilesCommand.Execute(null);
        Assert.True(model.IsBrowsingCommands);
        Assert.True(model.ExecuteCommand.CanExecute(null));
        Assert.Equal(0, action.Calls);
        Assert.True(model.ExecuteSelected(false));
        Assert.Equal(1, action.Calls);
    }

    [Fact]
    public void Binding_summary_shows_active_scope_and_gesture_rather_than_unapplied_choices()
    {
        var registry = new OperatorKeyboardCommandRegistry([
            new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe(), Scope: OperatorShortcutScope.Timeline)]);
        using var model = new OperatorCommandPaletteViewModel(registry, []);
        Assert.Contains("Ctrl+B", model.EditingBindingSummary);
        Assert.Contains("Timeline", model.EditingBindingSummary);
        model.EditingKey = Key.Q;
        Assert.Contains("Ctrl+B", model.EditingBindingSummary);
        Assert.True(registry.TryApplyProfile(new(2, "Desk", [new("draft", Key.E, ModifierKeys.Control)]), out _));
        Assert.Contains("Ctrl+E", model.EditingBindingSummary);
        Assert.Contains("Timeline", model.EditingBindingSummary);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("version")]
    [InlineData("binding")]
    [InlineData("key")]
    [InlineData("command")]
    public async Task Unknown_or_duplicate_JSON_fields_preserve_active_and_saved_profile(string fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-shortcut-schema-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json");
        var imported = Path.Combine(root, "import.json");
        var store = new OperatorShortcutProfileStore(path);
        var registry = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
        try
        {
            using var model = new OperatorCommandPaletteViewModel(registry, [], store);
            await model.ApplyAndSaveAsync(new(2, "Active", [new("draft", Key.E, ModifierKeys.Control)]));
            var before = await File.ReadAllBytesAsync(path);
            var active = registry.Definitions;
            var valid = JsonSerializer.Serialize(new OperatorShortcutProfile(2, "Imported", [new("draft", Key.Q, ModifierKeys.Control)]));
            var invalid = fault switch
            {
                "root" => valid.Replace("\"Name\":", "\"Unknown\":true,\"Name\":"),
                "version" => valid.Replace("\"Version\":2", "\"Version\":99,\"Version\":2"),
                "binding" => valid.Replace("\"CommandId\":", "\"Scope\":\"Global\",\"CommandId\":"),
                "key" => valid.Replace("\"Key\":", "\"Key\":0,\"Key\":"),
                _ => valid.Replace("\"CommandId\":", "\"CommandId\":\"cut\",\"CommandId\":")
            };
            Assert.NotEqual(valid, invalid);
            await File.WriteAllTextAsync(imported, invalid);
            await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(imported));
            await model.ImportProfileAsync(imported);
            Assert.Contains("Import rejected", model.Status);
            Assert.Same(active, registry.Definitions);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Canceled_import_retains_active_profile_and_durable_preferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-shortcut-cancel-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json");
        var store = new OperatorShortcutProfileStore(path);
        var registry = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
        try
        {
            using var model = new OperatorCommandPaletteViewModel(registry, [], store);
            await model.ApplyAndSaveAsync(new(2, "Active", []));
            var before = await File.ReadAllBytesAsync(path);
            var active = registry.Definitions;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await model.ImportProfileAsync(path, canceled.Token);
            Assert.Contains("Import canceled", model.Status);
            Assert.False(model.IsProfileBusy);
            Assert.Same(active, registry.Definitions);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Slow_dispatcher_coalesces_profile_save_burst_and_flush_persists_the_latest_assignment()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-shortcut-burst-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "preferences.json");
        var store = new OperatorShortcutProfileStore(path);
        var registry = new OperatorKeyboardCommandRegistry([new("draft", "Draft", Key.B, ModifierKeys.Control, new Probe())]);
        try
        {
            using var model = new OperatorCommandPaletteViewModel(registry, [], store);
            var context = new QueuedContext();
            var previous = SynchronizationContext.Current;
            Task pending;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                pending = model.ApplyAndSaveAsync(new(2, "First", []));
                for (var index = 0; index < 5000; index++)
                    Assert.Same(pending, model.ApplyAndSaveAsync(new(2, "Desk " + index, [new("draft", Key.Q, ModifierKeys.Control)])));
                Assert.Single(context.Pending);
                Assert.False(File.Exists(path));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline)
            {
                context.Drain();
                await Task.Delay(1);
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(1));
            await model.FlushAsync();
            Assert.Equal("Desk 4999", (await store.ReadAsync()).Name);
            Assert.Equal(Key.Q, (await store.ReadAsync()).Bindings.Single().Key);
            await model.ApplyAndSaveAsync(new(2, "After burst", []));
            Assert.Equal("After burst", (await store.ReadAsync()).Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<Action> Pending { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Pending.Enqueue(() => callback(state));
        public void Drain()
        {
            var previous = Current; SetSynchronizationContext(this);
            try { while (Pending.TryDequeue(out var action)) action(); }
            finally { SetSynchronizationContext(previous); }
        }
    }

	internal static async Task VerifyWindowAsync(MainWindow main, Dispatcher dispatcher)
	{
		Assert.True(OperatorKeyboardCommandRegistry.IsTextEntryContext(new RtaimeComboBoxItem()));
		var selectedWorkspace = main.Shell.SelectedWorkspace;
		var previous = Assert.IsAssignableFrom<RtaimeButton>(main.FindName("SynchronizeButton"));
		previous.Focus();
		Assert.True(main.Shortcuts.TryHandle(Key.K, ModifierKeys.Control, false, true));
		await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
		var palette = Assert.IsType<OperatorCommandPaletteWindow>(main.CommandPaletteWindow);
		try
		{
			Assert.True(palette.IsVisible);
			var search = Assert.IsType<RtaimeTextBox>(palette.FindName("SearchBox"));
			var results = Assert.IsType<RtaimeListBox>(palette.FindName("CommandResults"));
			var button = Assert.IsType<RtaimeButton>(palette.FindName("ExecuteButton"));
			Assert.False(button.IsDefault);
			Assert.True(search.IsKeyboardFocusWithin);
			main.CommandPalette.Query = "cut";
			Assert.Contains(main.CommandPalette.Results, entry => entry.Id == "cut");
			main.CommandPalette.Selected = main.CommandPalette.Results.Single(entry => entry.Id == "cut");
			Assert.False(main.CommandPalette.Selected.IsAvailable);
			Assert.Contains("Disconnected", main.CommandPalette.SelectedState);
			var program = ((OperatorViewModel)main.DataContext).ProgramSourceId;
			var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Return)
			{ RoutedEvent = Keyboard.PreviewKeyDownEvent };
			search.RaiseEvent(enter);
			Assert.True(enter.Handled);
			Assert.Equal(program, ((OperatorViewModel)main.DataContext).ProgramSourceId);
			main.CommandPalette.Query = "workspace health";
			main.CommandPalette.Selected = main.CommandPalette.Results.Single(entry => entry.Id == "workspace-health");
			Assert.True(main.CommandPalette.ExecuteSelected(true));
			Assert.Null(main.CommandPaletteWindow);
			Assert.Equal("HEALTH", main.Shell.SelectedWorkspace);
			// Reopen at compact sizes; profile editing occupies the bounded result area.
			main.OpenCommandPalette();
			palette = Assert.IsType<OperatorCommandPaletteWindow>(main.CommandPaletteWindow);
            search = Assert.IsType<RtaimeTextBox>(palette.FindName("SearchBox"));
			palette.Width = 360; palette.Height = 360;
			main.CommandPalette.ToggleProfilesCommand.Execute(null);
			await dispatcher.InvokeAsync(palette.UpdateLayout, DispatcherPriority.Render);
			Assert.Equal(Visibility.Visible, main.CommandPalette.ProfilesVisibility);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var profileName = Assert.IsType<RtaimeTextBox>(palette.FindName("ProfileNameBox"));
            Assert.True(profileName.IsKeyboardFocusWithin);
            Assert.False(search.IsEnabled);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<RtaimeButton>(palette.FindName("ExecuteButton")).Visibility);
            Assert.False(main.CommandPalette.ExecuteCommand.CanExecute(null));
            var workspaceBeforeEdit = main.Shell.SelectedWorkspace;
            var profileEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(profileName)!, 0, Key.Return)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            profileName.RaiseEvent(profileEnter);
            Assert.True(profileEnter.Handled);
            Assert.Equal(workspaceBeforeEdit, main.Shell.SelectedWorkspace);
            Assert.Equal(program, ((OperatorViewModel)main.DataContext).ProgramSourceId);
            Assert.False(main.CommandPalette.ExecuteSelected(false));
            var summary = Assert.IsType<System.Windows.Controls.TextBlock>(palette.FindName("CurrentBindingSummary"));
            Assert.Equal(main.CommandPalette.EditingBindingSummary, summary.Text);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(summary)));
			Assert.True(palette.ActualWidth > 0);
			Assert.True(Assert.IsType<RtaimeTextBox>(palette.FindName("SearchBox")).ActualWidth > 0);
			main.CommandPalette.ToggleProfilesCommand.Execute(null);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Assert.True(Assert.IsType<RtaimeTextBox>(palette.FindName("SearchBox")).IsKeyboardFocusWithin);
			var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(palette)!, 0, Key.Escape)
			{ RoutedEvent = Keyboard.PreviewKeyDownEvent };
			palette.RaiseEvent(escape);
			Assert.True(escape.Handled);
			Assert.Null(main.CommandPaletteWindow);
			main.Shell.SelectWorkspace("MEDIA");
			var docking = main.Shell.Docking;
			try
			{
				main.Docking.Float("TOOLS");
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
				var panel = main.Docking.FloatingWindows["TOOLS"];
				panel.Activate();
				var mediaSearch = Assert.IsType<RtaimeSearchBox>(main.FindName("MediaSearchBox"));
				mediaSearch.Focus();
				Assert.Same(main.Shortcuts, panel.Shortcuts);
				Assert.True(panel.Shortcuts.TryHandle(Key.K, ModifierKeys.Control, false, true));
				await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
				Assert.Same(panel, main.CommandPaletteWindow!.Owner);
				main.CommandPaletteWindow.Close();
			}
			finally { main.Shell.SetDocking(docking); }
			Console.WriteLine("COMMAND PALETTE PASS: real BAML, shared router, blocked TAKE, navigation, compact profiles, detached owner and Escape.");
		}
		finally
		{
			main.CommandPaletteWindow?.Close();
			main.Shell.SelectWorkspace(selectedWorkspace);
		}
	}
	private sealed class Probe : ICommand
	{
		internal int Calls;
		internal bool Allowed = true;
		internal object? Parameter;
		public event EventHandler? CanExecuteChanged;
		public bool CanExecute(object? parameter) => Allowed;
		public void Execute(object? parameter) { if (Allowed) { Calls++; Parameter = parameter; } }
		internal void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}
