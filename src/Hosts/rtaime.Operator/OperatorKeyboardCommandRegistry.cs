// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorShortcutDefinition(
	string Id,
	string Label,
	Key Key,
	ModifierKeys Modifiers,
	ICommand Command,
	bool AllowInTextEntry = false)
{
	public string Gesture => OperatorKeyboardCommandRegistry.FormatGesture(Key, Modifiers);
}

public sealed class OperatorKeyboardCommandRegistry
{
	private readonly IReadOnlyList<OperatorShortcutDefinition> _definitions;

	public OperatorKeyboardCommandRegistry(IEnumerable<OperatorShortcutDefinition> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		_definitions = definitions.ToArray();
		var conflicts = FindConflicts(_definitions);
		if (conflicts.Count > 0)
		{
			throw new InvalidOperationException(
				$"Operator keyboard shortcut conflict: {string.Join(", ", conflicts)}");
		}
	}

	public IReadOnlyList<OperatorShortcutDefinition> Definitions => _definitions;

	public string ReferenceText
	{
		get
		{
			var builder = new StringBuilder();
			foreach (var definition in _definitions)
				builder.AppendLine($"{definition.Gesture,-14} {definition.Label}");
			return builder.ToString().TrimEnd();
		}
	}

	public void Apply(InputBindingCollection bindings)
	{
		ArgumentNullException.ThrowIfNull(bindings);
		bindings.Clear();
		foreach (var definition in _definitions)
		{
			bindings.Add(new KeyBinding(
				new ContextSafeShortcutCommand(definition.Command, definition.AllowInTextEntry),
				definition.Key,
				definition.Modifiers));
		}
	}

	public static OperatorKeyboardCommandRegistry Create(
		OperatorViewModel @operator,
		MediaDeckViewModel mediaDeck,
		MediaTimelineViewModel timeline,
		OperatorShellViewModel shell)
	{
		ArgumentNullException.ThrowIfNull(@operator);
		ArgumentNullException.ThrowIfNull(mediaDeck);
		ArgumentNullException.ThrowIfNull(timeline);
		ArgumentNullException.ThrowIfNull(shell);

		var recording = new ContextSwitchCommand(
			@operator.StopRecordingCommand,
			@operator.StartRecordingCommand);
		return new OperatorKeyboardCommandRegistry(
		[
			new("sync", "Synchronize authoritative state", Key.F5, ModifierKeys.None, @operator.SynchronizeCommand, true),
			new("preview", "Set selected source to Preview", Key.P, ModifierKeys.Control, @operator.SetPreviewCommand),
			new("play-pause", "Media Play / Pause", Key.Space, ModifierKeys.None, mediaDeck.TogglePlayPauseCommand),
			new("pause", "Media Pause", Key.K, ModifierKeys.None, mediaDeck.PauseCommand),
			new("stop", "Media Stop", Key.S, ModifierKeys.None, mediaDeck.StopCommand),
			new("set-in", "Set IN", Key.I, ModifierKeys.None, mediaDeck.SetInCommand),
			new("set-out", "Set OUT", Key.O, ModifierKeys.None, mediaDeck.SetOutCommand),
			new("add-cue", "Add marker / cue", Key.M, ModifierKeys.None, mediaDeck.AddCueCommand),
			new("previous-cue", "Previous cue", Key.Up, ModifierKeys.None, timeline.PreviousCueCommand),
			new("next-cue", "Next cue", Key.Down, ModifierKeys.None, timeline.NextCueCommand),
			new("auto", "AUTO Preview to Program", Key.Return, ModifierKeys.None, @operator.DissolveCommand),
			new("cut", "CUT Preview to Program", Key.Return, ModifierKeys.Control, @operator.CutCommand),
			new("record", "Start / Stop Program recording", Key.R, ModifierKeys.None, recording),
			new("fullscreen", "Fullscreen / windowed Operator", Key.F11, ModifierKeys.None, shell.ToggleFullscreenCommand, true),
			new("exit-fullscreen", "Exit fullscreen", Key.Escape, ModifierKeys.None, shell.ExitFullscreenCommand, true),
			new("preview-view", "Maximize Preview viewer", Key.D1, ModifierKeys.Control, shell.MaximizePreviewCommand, true),
			new("program-view", "Maximize Program viewer", Key.D2, ModifierKeys.Control, shell.MaximizeProgramCommand, true),
			new("dual-view", "Restore dual viewer", Key.D0, ModifierKeys.Control, shell.RestoreViewersCommand, true)
		]);
	}

	public static IReadOnlyList<string> FindConflicts(IEnumerable<OperatorShortcutDefinition> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		return definitions
			.GroupBy(item => (item.Key, item.Modifiers))
			.Where(group => group.Count() > 1)
			.Select(group => FormatGesture(group.Key.Key, group.Key.Modifiers))
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();
	}

	public static string FormatGesture(Key key, ModifierKeys modifiers)
	{
		var parts = new List<string>(4);
		if (modifiers.HasFlag(ModifierKeys.Control))
			parts.Add("Ctrl");
		if (modifiers.HasFlag(ModifierKeys.Alt))
			parts.Add("Alt");
		if (modifiers.HasFlag(ModifierKeys.Shift))
			parts.Add("Shift");
		if (modifiers.HasFlag(ModifierKeys.Windows))
			parts.Add("Win");

		parts.Add(key switch
		{
			Key.D0 => "0",
			Key.D1 => "1",
			Key.D2 => "2",
			Key.D3 => "3",
			Key.D4 => "4",
			Key.D5 => "5",
			Key.D6 => "6",
			Key.D7 => "7",
			Key.D8 => "8",
			Key.D9 => "9",
			Key.Return => "Enter",
			_ => key.ToString()
		});
		return string.Join("+", parts);
	}

	private sealed class ContextSafeShortcutCommand : ICommand
	{
		private readonly ICommand _inner;
		private readonly bool _allowInTextEntry;

		public ContextSafeShortcutCommand(ICommand inner, bool allowInTextEntry)
		{
			_inner = inner ?? throw new ArgumentNullException(nameof(inner));
			_allowInTextEntry = allowInTextEntry;
			_inner.CanExecuteChanged += OnInnerCanExecuteChanged;
		}

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) =>
			(_allowInTextEntry || !IsTextEntryContext()) &&
			_inner.CanExecute(parameter);

		public void Execute(object? parameter)
		{
			if (CanExecute(parameter))
				_inner.Execute(parameter);
		}

		private void OnInnerCanExecuteChanged(object? sender, EventArgs e) =>
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}

	private sealed class ContextSwitchCommand : ICommand
	{
		private readonly ICommand _preferred;
		private readonly ICommand _fallback;

		public ContextSwitchCommand(ICommand preferred, ICommand fallback)
		{
			_preferred = preferred ?? throw new ArgumentNullException(nameof(preferred));
			_fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
			_preferred.CanExecuteChanged += OnCanExecuteChanged;
			_fallback.CanExecuteChanged += OnCanExecuteChanged;
		}

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) =>
			_preferred.CanExecute(parameter) || _fallback.CanExecute(parameter);

		public void Execute(object? parameter)
		{
			if (_preferred.CanExecute(parameter))
				_preferred.Execute(parameter);
			else if (_fallback.CanExecute(parameter))
				_fallback.Execute(parameter);
		}

		private void OnCanExecuteChanged(object? sender, EventArgs e) =>
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}

	private static bool IsTextEntryContext()
	{
		var focused = Keyboard.FocusedElement;
		if (focused is TextBoxBase or PasswordBox)
			return true;
		if (focused is ComboBox comboBox)
			return comboBox.IsEditable || comboBox.IsKeyboardFocusWithin;
		return false;
	}
}
