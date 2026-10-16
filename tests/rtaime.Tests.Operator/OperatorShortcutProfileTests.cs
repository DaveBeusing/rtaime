// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorShortcutProfileTests
{
	[Theory]
	[InlineData(Key.Return, ModifierKeys.Control)]
	[InlineData(Key.F4, ModifierKeys.Alt)]
	[InlineData(Key.K, ModifierKeys.Control)]
	[InlineData(Key.Z, ModifierKeys.Control)]
	[InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
	[InlineData(Key.Q, ModifierKeys.Control | ModifierKeys.Alt)]
	[InlineData(Key.Q, ModifierKeys.Windows)]
	[InlineData(Key.Q, ModifierKeys.None)]
	public void Reserved_editing_system_and_AltGr_gestures_are_not_user_overrides(Key key, ModifierKeys modifiers) =>
		Assert.False(OperatorShortcutProfiles.IsSafeOverride(key, modifiers));

	[Fact]
	public void Apply_is_atomic_and_preserves_commands_focus_rules_and_canonical_defaults()
	{
		var action = new Probe();
		var registry = new OperatorKeyboardCommandRegistry([
			new("a", "A", Key.B, ModifierKeys.Control, action),
			new("b", "B", Key.D, ModifierKeys.Control, new Probe())]);
		var profile = new OperatorShortcutProfile(2, "Desk", [new("a", Key.Q, ModifierKeys.Control | ModifierKeys.Shift)]);
		Assert.True(registry.TryApplyProfile(profile, out _));
		Assert.Equal(Key.Q, registry.Definitions[0].Key);
		Assert.Same(action, registry.Definitions[0].Command);
		Assert.False(registry.Definitions[0].AllowInTextEntry);
		Assert.Equal(Key.B, registry.CanonicalDefinitions[0].Key);
		var snapshot = registry.Definitions;
		Assert.False(registry.TryApplyProfile(profile with { Bindings = [new("a", Key.D, ModifierKeys.Control)] }, out var error));
		Assert.Contains("collision", error);
		Assert.Same(snapshot, registry.Definitions);
		Assert.False(registry.TryApplyProfile(profile with { Bindings = [new("unknown", Key.Q, ModifierKeys.Control)] }, out _));
		Assert.False(registry.TryApplyProfile(profile with { Bindings = [new("a", Key.Q, ModifierKeys.Control), new("a", Key.B, ModifierKeys.Control)] }, out _));
		Assert.True(registry.TryApplyProfile(OperatorShortcutProfile.Defaults, out _));
		Assert.Equal(Key.B, registry.Definitions[0].Key);
	}

	[Theory]
	[InlineData("cut")]
	[InlineData("auto")]
	[InlineData("record")]
	[InlineData("show-control-go")]
	[InlineData("command-palette")]
	[InlineData("sync")]
	public void Production_and_recovery_shortcuts_cannot_be_overridden(string id)
	{
		var registry = new OperatorKeyboardCommandRegistry([new(id, id, Key.F5, ModifierKeys.None, new Probe())]);
		Assert.False(registry.TryApplyProfile(new(2, "Unsafe", [new(id, Key.Q, ModifierKeys.Control)]), out var error));
		Assert.Contains("Reserved", error);
		Assert.Equal(Key.F5, registry.Definitions[0].Key);
	}

	[Fact]
	public void Focus_scope_conflicts_include_global_but_allow_disjoint_local_scopes()
	{
		var timeline = new OperatorShortcutDefinition("timeline", "Timeline", Key.B, ModifierKeys.Control, new Probe(), Scope: OperatorShortcutScope.Timeline);
		var library = timeline with { Id = "library", Command = new Probe(), Scope = OperatorShortcutScope.MediaLibrary };
		Assert.Empty(OperatorKeyboardCommandRegistry.FindConflicts([timeline, library]));
		Assert.Single(OperatorKeyboardCommandRegistry.FindConflicts([timeline, library with { Scope = OperatorShortcutScope.Global }]));
		var registry = new OperatorKeyboardCommandRegistry([timeline, library]);
		Assert.False(registry.TryHandle(Key.B, ModifierKeys.Control, false, false));
		Assert.True(registry.TryHandle(Key.B, ModifierKeys.Control, false, false, OperatorShortcutScope.Timeline));
		Assert.Equal(1, ((Probe)timeline.Command).Calls);
		Assert.Equal(0, ((Probe)library.Command).Calls);
	}

	[Fact]
	public void Typing_repeats_and_denied_commands_cannot_execute_TAKE()
	{
		var take = new Probe();
		var registry = new OperatorKeyboardCommandRegistry([new("cut", "CUT", Key.Return, ModifierKeys.Control, take)]);
		Assert.False(registry.TryHandle(Key.Return, ModifierKeys.Control, false, true));
		Assert.True(registry.TryHandle(Key.Return, ModifierKeys.Control, true, false));
		Assert.Equal(0, take.Calls);
		take.Allowed = false;
		Assert.True(registry.TryHandle(Key.Return, ModifierKeys.Control, false, false));
		Assert.Equal(0, take.Calls);
		take.Allowed = true;
		Assert.True(registry.TryHandle(Key.Return, ModifierKeys.Control, false, false));
		Assert.Equal(1, take.Calls);
	}

	[Fact]
	public void Legacy_profile_migrates_without_changing_scope_or_text_entry_authority()
	{
		var original = new OperatorShortcutDefinition("a", "A", Key.B, ModifierKeys.Control, new Probe(), Scope: OperatorShortcutScope.Timeline);
		var registry = new OperatorKeyboardCommandRegistry([original]);
		Assert.True(registry.TryApplyProfile(new(1, null, [new("a", Key.Q, ModifierKeys.Control)]), out _));
		Assert.Equal(2, registry.CurrentProfile.Version);
		Assert.Equal("Imported shortcuts", registry.CurrentProfile.Name);
		Assert.Equal(original.Scope, registry.Definitions[0].Scope);
		Assert.False(registry.Definitions[0].AllowInTextEntry);
		Assert.False(registry.TryApplyProfile(registry.CurrentProfile with { Version = 99 }, out _));
		Assert.False(registry.TryApplyProfile(registry.CurrentProfile with { Name = new string('x', 65) }, out _));
	}

	[Fact]
	public async Task File_roundtrip_is_bounded_cancelable_and_preserves_valid_preferences()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-shortcuts-" + Guid.NewGuid().ToString("N"));
		var path = Path.Combine(root, "shortcuts.json");
		var store = new OperatorShortcutProfileStore(path);
		try
		{
			Assert.Empty((await store.ReadAsync()).Bindings);
			var profile = new OperatorShortcutProfile(2, "Desk", [new("a", Key.Q, ModifierKeys.Control)]);
			await store.WriteAsync(profile);
			var restored = await store.ReadAsync();
			Assert.Equal(profile.Name, restored.Name);
			Assert.Equal(profile.Bindings, restored.Bindings);
			using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(OperatorShortcutProfile.Defaults, cancellationToken: cancelled.Token));
			Assert.Equal("Desk", (await store.ReadAsync()).Name);
			Assert.Single(Directory.GetFiles(root));
			var exported = Path.Combine(root, "export.json");
			await store.WriteAsync(restored, exported);
			Assert.Equal(restored.Bindings, (await store.ReadAsync(exported)).Bindings);
			await File.WriteAllTextAsync(exported, "{bad");
			await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(exported));
			Assert.Equal("Desk", (await store.ReadAsync()).Name);
			await File.WriteAllTextAsync(exported, new string('x', OperatorShortcutProfileStore.MaximumBytes + 1));
			await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(exported));
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
	}
    public static IEnumerable<object[]> AuthorityRoutingMatrix()
    {
        foreach (var id in new[] { "cut", "auto", "record", "show-control-go" })
        foreach (var scope in Enum.GetValues<OperatorShortcutScope>())
        foreach (var textEntry in new[] { false, true })
        foreach (var repeat in new[] { false, true })
        foreach (var allowed in new[] { false, true })
            yield return [id, scope, textEntry, repeat, allowed];
    }

    [Theory]
    [MemberData(nameof(AuthorityRoutingMatrix))]
    public void Production_routing_matrix_preserves_text_repeat_and_authority_guards(
        string id, OperatorShortcutScope scope, bool textEntry, bool repeat, bool allowed)
    {
        var command = new Probe { Allowed = allowed };
        var definition = new OperatorShortcutDefinition(id, id, Key.F9, ModifierKeys.None, command);
        var registry = new OperatorKeyboardCommandRegistry([definition]);
        Assert.Equal(!textEntry, registry.TryHandle(Key.F9, ModifierKeys.None, repeat, textEntry, scope));
        Assert.Equal(!textEntry && !repeat && allowed ? 1 : 0, command.Calls);
        Assert.Same(command, Assert.Single(registry.Definitions).Command);
        Assert.False(Assert.Single(registry.Definitions).AllowInTextEntry);
    }

    [Theory]
    [InlineData(OperatorShortcutScope.Timeline, OperatorShortcutScope.MediaLibrary)]
    [InlineData(OperatorShortcutScope.MediaLibrary, OperatorShortcutScope.Timeline)]
    public void Local_undo_scope_never_falls_through_to_another_editor(
        OperatorShortcutScope active, OperatorShortcutScope other)
    {
        var local = new Probe(); var foreign = new Probe();
        var registry = new OperatorKeyboardCommandRegistry([
            new("local-draft", "Local draft", Key.Z, ModifierKeys.Control, local, Scope: active),
            new("other-draft", "Other draft", Key.Z, ModifierKeys.Control, foreign, Scope: other)]);
        Assert.False(registry.TryHandle(Key.Z, ModifierKeys.Control, false, true, active));
        Assert.True(registry.TryHandle(Key.Z, ModifierKeys.Control, true, false, active));
        Assert.Equal(0, local.Calls); Assert.Equal(0, foreign.Calls);
        Assert.True(registry.TryHandle(Key.Z, ModifierKeys.Control, false, false, active));
        Assert.Equal(1, local.Calls); Assert.Equal(0, foreign.Calls);
        Assert.False(registry.TryHandle(Key.Z, ModifierKeys.Control, false, false));
        Assert.Empty(OperatorKeyboardCommandRegistry.FindConflicts(registry.Definitions));
    }

	private sealed class Probe : ICommand
	{
		internal int Calls;
		internal bool Allowed = true;
		public event EventHandler? CanExecuteChanged { add { } remove { } }
		public bool CanExecute(object? parameter) => Allowed;
		public void Execute(object? parameter) { if (Allowed) Calls++; }
	}
}
