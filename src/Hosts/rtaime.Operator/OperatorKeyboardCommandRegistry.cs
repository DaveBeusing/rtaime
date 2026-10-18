// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using rtaime.Operator.Controls;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorShortcutDefinition(
	string Id,
	string Label,
	Key Key,
	ModifierKeys Modifiers,
	ICommand Command,
	bool AllowInTextEntry = false,
	OperatorShortcutScope Scope = OperatorShortcutScope.Global)
{
	public string Gesture => OperatorKeyboardCommandRegistry.FormatGesture(Key, Modifiers);
}

public sealed class OperatorKeyboardCommandRegistry
{
	private IReadOnlyList<OperatorShortcutDefinition> _definitions;
	private readonly IReadOnlyList<OperatorShortcutDefinition> _canonical;
	public event EventHandler? Changed;
	public OperatorShortcutProfile CurrentProfile { get; private set; } = OperatorShortcutProfile.Defaults;

	public OperatorKeyboardCommandRegistry(IEnumerable<OperatorShortcutDefinition> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		_definitions = _canonical = definitions.ToArray();
		if (_definitions.Count > 96 || _definitions.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != _definitions.Count || _definitions.Any(d => !Enum.IsDefined(d.Scope)))
			throw new ArgumentException("Invalid or duplicate shortcut descriptors.", nameof(definitions));
		var conflicts = FindConflicts(_definitions);
		if (conflicts.Count > 0)
		{
			throw new InvalidOperationException(
				$"Operator keyboard shortcut conflict: {string.Join(", ", conflicts)}");
		}
	}

	public IReadOnlyList<OperatorShortcutDefinition> Definitions => _definitions;
	public IReadOnlyList<OperatorShortcutDefinition> CanonicalDefinitions => _canonical;
	public bool TryApplyProfile(OperatorShortcutProfile profile, out string error)
	{
		if (!OperatorShortcutProfiles.TryResolve(profile, _canonical, out var normalized, out var resolved, out error)) return false;
		_definitions = resolved; CurrentProfile = normalized;
		Changed?.Invoke(this, EventArgs.Empty);
		return true;
	}

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

	public bool TryHandle(KeyEventArgs args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var key = args.Key == Key.System ? args.SystemKey : args.Key;
		// Numeric Escape cancels the current field/gesture before the shell fullscreen command.
		if (key == Key.Escape && Keyboard.FocusedElement is RtaimeNumericField) return false;
		if (key == Key.Escape)
		{
			for (var current = Keyboard.FocusedElement as System.Windows.DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : System.Windows.LogicalTreeHelper.GetParent(current))
				if (current is MediaTimelineControl { OwnsEscape: true }) return false;
		}
		// Enter on a focused cue button belongs to that explicit button; list/context Enter never takes.
		if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !IsTextEntryContext() &&
			(LiveConsoleInput.FindButton(Keyboard.FocusedElement as System.Windows.DependencyObject) is not null ||
			 LiveConsoleInput.IsCueSurface(Keyboard.FocusedElement as System.Windows.DependencyObject)))
		{
			if (args.IsRepeat || LiveConsoleInput.FindButton(Keyboard.FocusedElement as System.Windows.DependencyObject) is null)
			{ args.Handled = true; return true; }
			return false;
		}
		var handled = TryHandle(key, Keyboard.Modifiers, args.IsRepeat, IsTextEntryContext(), ResolveScope());
		if (handled) args.Handled = true;
		return handled;
	}

	public bool TryHandle(Key key, ModifierKeys modifiers, bool isRepeat, bool textEntry,
		OperatorShortcutScope scope = OperatorShortcutScope.Global)
	{
		var definition = _definitions.FirstOrDefault(item => item.Key == key && item.Modifiers == modifiers &&
			(item.Scope == OperatorShortcutScope.Global || item.Scope == scope));
		if (definition is null || (!definition.AllowInTextEntry && textEntry)) return false;
		if (!isRepeat && definition.Command.CanExecute(null)) definition.Command.Execute(null);
		return true;
	}

	public static OperatorKeyboardCommandRegistry Create(
		OperatorViewModel @operator,
		MediaDeckViewModel mediaDeck,
		MediaTimelineViewModel timeline,
		OperatorShellViewModel shell,
		ShowControlViewModel showControl,
		ICommand focusMediaSearchCommand,
		OperatorDockController? docking = null,
		ICommand? openPaletteCommand = null)
	{
		ArgumentNullException.ThrowIfNull(@operator);
		ArgumentNullException.ThrowIfNull(mediaDeck);
		ArgumentNullException.ThrowIfNull(timeline);
		ArgumentNullException.ThrowIfNull(shell);
		ArgumentNullException.ThrowIfNull(showControl);
		ArgumentNullException.ThrowIfNull(focusMediaSearchCommand);

		var recording = new ContextSwitchCommand(
			@operator.StopRecordingCommand,
			@operator.StartRecordingCommand);
		var previewTransportContext = new Func<bool>(() =>
			mediaDeck.IsLoaded &&
			string.Equals(@operator.PreviewSourceId, mediaDeck.SourceId, StringComparison.Ordinal));
		ICommand PreviewTransport(ICommand command) => new ContextGuardCommand(command, previewTransportContext);

		return new OperatorKeyboardCommandRegistry(
		[
			new("sync", "Synchronize authoritative state", Key.F5, ModifierKeys.None, @operator.SynchronizeCommand, true),
			new("media-search", "Focus Media Library search", Key.F, ModifierKeys.Control, focusMediaSearchCommand, true),
			new("preview", "Set selected source to Preview", Key.P, ModifierKeys.Control, @operator.SetPreviewCommand),
			new("play-pause", "Preview Play / Pause", Key.Space, ModifierKeys.None, PreviewTransport(mediaDeck.TogglePlayPauseCommand)),
			new("j-step", "Preview back one frame (reverse shuttle unavailable)", Key.J, ModifierKeys.None, new ContextGuardCommand(timeline.StepBackwardCommand, () => previewTransportContext() && mediaDeck.ConfirmedSnapshot.Transport?.IsOnProgram != true)),
			new("l-play", "Preview play at normal rate", Key.L, ModifierKeys.None, new ContextGuardCommand(mediaDeck.PlayCommand, () => previewTransportContext() && mediaDeck.ConfirmedSnapshot.Transport?.IsOnProgram != true)),
			new("pause", "Preview Pause", Key.K, ModifierKeys.None, PreviewTransport(mediaDeck.PauseCommand)),
			new("stop", "Preview Stop", Key.S, ModifierKeys.None, PreviewTransport(mediaDeck.StopCommand)),
			new("set-in", "Set Preview IN", Key.I, ModifierKeys.None, PreviewTransport(mediaDeck.SetInCommand)),
			new("set-out", "Set Preview OUT", Key.O, ModifierKeys.None, PreviewTransport(mediaDeck.SetOutCommand)),
			new("add-cue", "Add Preview marker / cue", Key.M, ModifierKeys.None, PreviewTransport(mediaDeck.AddCueCommand)),
			new("delete-cue", "Delete selected Preview cue", Key.Delete, ModifierKeys.None, PreviewTransport(mediaDeck.DeleteCueCommand)),
			new("previous-cue", "Previous Preview cue", Key.Up, ModifierKeys.None, PreviewTransport(timeline.PreviousCueCommand)),
			new("next-cue", "Next Preview cue", Key.Down, ModifierKeys.None, PreviewTransport(timeline.NextCueCommand)),
			new("auto", "AUTO Preview to Program", Key.Return, ModifierKeys.None, @operator.DissolveCommand),
			new("cut", "CUT Preview to Program", Key.Return, ModifierKeys.Control, @operator.CutCommand),
			new("record", "Start / Stop Program recording", Key.R, ModifierKeys.None, recording),
			new("show-control-go", "Show Control GO", Key.F9, ModifierKeys.None, showControl.GoCommand),
			new("fullscreen", "Fullscreen / windowed Operator", Key.F11, ModifierKeys.None, shell.ToggleFullscreenCommand, true),
			new("exit-fullscreen", "Exit fullscreen", Key.Escape, ModifierKeys.None, shell.ExitFullscreenCommand, true),
			new("preview-view", "Maximize Preview viewer", Key.D1, ModifierKeys.Control, shell.MaximizePreviewCommand, true),
			new("program-view", "Maximize Program viewer", Key.D2, ModifierKeys.Control, shell.MaximizeProgramCommand, true),
			new("dual-view", "Restore dual viewer", Key.D0, ModifierKeys.Control, shell.RestoreViewersCommand, true),
            .. DockShortcuts(docking, shell),
			.. (openPaletteCommand is null ? Array.Empty<OperatorShortcutDefinition>() : [new("command-palette", "Open command palette", Key.K, ModifierKeys.Control, openPaletteCommand, true)])
		]);
	}

    private static IEnumerable<OperatorShortcutDefinition> DockShortcuts(OperatorDockController? docking, OperatorShellViewModel shell)
    {
        if (docking is null) return [];
        return [
            new("dock-toggle", "Built-in / docking layout", Key.D, ModifierKeys.Control | ModifierKeys.Shift, docking.ToggleCommand, true),
            new("dock-float", "Float focused panel", Key.W, ModifierKeys.Control | ModifierKeys.Shift, docking.FloatCommand, true),
            new("dock-focus", "Focus / restore panel layout", Key.F, ModifierKeys.Control | ModifierKeys.Shift, docking.FocusCommand, true),
            new("dock-cinema", "Cinema / restore Program monitoring", Key.C, ModifierKeys.Control | ModifierKeys.Shift, docking.CinemaCommand, true),
            new("dock-next", "Next dock panel", Key.N, ModifierKeys.Control | ModifierKeys.Shift, docking.NextCommand, true),
            new("dock-left", "Dock focused panel left", Key.Left, ModifierKeys.Control | ModifierKeys.Alt, docking.LeftCommand, true),
            new("dock-center", "Dock focused panel center", Key.Up, ModifierKeys.Control | ModifierKeys.Alt, docking.CenterCommand, true),
            new("dock-right", "Dock focused panel right", Key.Right, ModifierKeys.Control | ModifierKeys.Alt, docking.RightCommand, true),
            new("dock-bottom", "Dock focused panel below", Key.Down, ModifierKeys.Control | ModifierKeys.Alt, docking.BottomCommand, true),
            new("dock-reset", "Reset workspace layout", Key.R, ModifierKeys.Control | ModifierKeys.Shift, shell.ResetLayoutCommand, true)
        ];
    }

	public static IReadOnlyList<string> FindConflicts(IEnumerable<OperatorShortcutDefinition> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		var items = definitions.ToArray();
		var conflicts = new HashSet<string>(StringComparer.Ordinal);
		for (var i = 0; i < items.Length; i++)
			for (var j = i + 1; j < items.Length; j++)
				if (items[i].Key == items[j].Key && items[i].Modifiers == items[j].Modifiers &&
					(items[i].Scope == OperatorShortcutScope.Global || items[j].Scope == OperatorShortcutScope.Global || items[i].Scope == items[j].Scope))
					conflicts.Add(FormatGesture(items[i].Key, items[i].Modifiers));
		return conflicts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
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

	private sealed class ContextGuardCommand : ICommand
	{
		private readonly ICommand _inner;
		private readonly Func<bool> _isContextActive;

		public ContextGuardCommand(ICommand inner, Func<bool> isContextActive)
		{
			_inner = inner ?? throw new ArgumentNullException(nameof(inner));
			_isContextActive = isContextActive ?? throw new ArgumentNullException(nameof(isContextActive));
			_inner.CanExecuteChanged += OnCanExecuteChanged;
		}

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) =>
			_isContextActive() && _inner.CanExecute(parameter);

		public void Execute(object? parameter)
		{
			if (CanExecute(parameter))
				_inner.Execute(parameter);
		}

		private void OnCanExecuteChanged(object? sender, EventArgs e) =>
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

	private static bool IsTextEntryContext() => IsTextEntryContext(Keyboard.FocusedElement as System.Windows.DependencyObject);
	public static bool IsTextEntryContext(System.Windows.DependencyObject? focused)
	{
		for (var current = focused; current is not null; current = Parent(current))
			if (current is AudioMixerControl or TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuItem or ContextMenu) return true;
		return false;
	}
	private static OperatorShortcutScope ResolveScope()
	{
		for (var current = Keyboard.FocusedElement as System.Windows.DependencyObject; current is not null; current = Parent(current))
		{
			if (current is MediaTimelineControl) return OperatorShortcutScope.Timeline;
			if (current is System.Windows.FrameworkElement { Name: "MediaAssetGrid" or "MediaAssetList" }) return OperatorShortcutScope.MediaLibrary;
		}
		return OperatorShortcutScope.Global;
	}
	private static System.Windows.DependencyObject? Parent(System.Windows.DependencyObject current) =>
		current is Visual ? VisualTreeHelper.GetParent(current) : System.Windows.LogicalTreeHelper.GetParent(current);
}
