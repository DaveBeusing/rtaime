// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record OperatorQuickControlSettings
{
	public const int CurrentVersion = 1;
	public int Version { get; init; } = CurrentVersion;
	public string[] PropertyIds { get; init; } = [];
}

public sealed class OperatorQuickControlStore
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true
	};

	private readonly string _path;

	public OperatorQuickControlStore(string? path = null)
	{
		_path = path ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime",
			"operator-quick-controls.json");
	}

	public IReadOnlyList<string> Load()
	{
		try
		{
			if (!File.Exists(_path))
				return [];

			var settings = JsonSerializer.Deserialize<OperatorQuickControlSettings>(
				File.ReadAllText(_path),
				SerializerOptions);
			if (settings is null || settings.Version != OperatorQuickControlSettings.CurrentVersion)
				return [];

			return settings.PropertyIds
				.Where(id => !string.IsNullOrWhiteSpace(id))
				.Select(id => id.Trim())
				.Distinct(StringComparer.Ordinal)
				.Take(OperatorQuickControlsViewModel.MaximumPinnedControls)
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return [];
		}
	}

	public void Save(IEnumerable<string> propertyIds)
	{
		ArgumentNullException.ThrowIfNull(propertyIds);
		try
		{
			var settings = new OperatorQuickControlSettings
			{
				PropertyIds = propertyIds
					.Where(id => !string.IsNullOrWhiteSpace(id))
					.Select(id => id.Trim())
					.Distinct(StringComparer.Ordinal)
					.Take(OperatorQuickControlsViewModel.MaximumPinnedControls)
					.ToArray()
			};
			var directory = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);
			File.WriteAllText(_path, JsonSerializer.Serialize(settings, SerializerOptions));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Quick-control pins are presentation preferences only.
		}
	}
}

public sealed class OperatorQuickControlItemViewModel
{
	public OperatorQuickControlItemViewModel(
		string propertyId,
		string label,
		string value,
		string state,
		string actionLabel,
		ICommand? decreaseCommand,
		ICommand? primaryCommand,
		ICommand? increaseCommand,
		ICommand removeCommand)
	{
		PropertyId = propertyId;
		Label = label;
		Value = value;
		State = state;
		ActionLabel = actionLabel;
		DecreaseCommand = decreaseCommand;
		PrimaryCommand = primaryCommand;
		IncreaseCommand = increaseCommand;
		RemoveCommand = removeCommand;
	}

	public string PropertyId { get; }
	public string Label { get; }
	public string Value { get; }
	public string State { get; }
	public string ActionLabel { get; }
	public ICommand? DecreaseCommand { get; }
	public ICommand? PrimaryCommand { get; }
	public ICommand? IncreaseCommand { get; }
	public ICommand RemoveCommand { get; }
	public bool HasDecrease => DecreaseCommand is not null;
	public bool HasPrimary => PrimaryCommand is not null;
	public bool HasIncrease => IncreaseCommand is not null;
}

public sealed class OperatorQuickControlsViewModel : INotifyPropertyChanged, IDisposable
{
	public const int MaximumPinnedControls = 8;

	private static readonly HashSet<string> SupportedPropertyIds = new(StringComparer.Ordinal)
	{
		"production.transition.frames",
		"clip.playback.autoplay",
		"clip.playback.end",
		"audio.gain",
		"audio.mute",
		"graphics.visible",
		"graphics.position.x",
		"graphics.position.y",
		"graphics.scale",
		"ai.enabled"
	};

	private readonly OperatorViewModel _operator;
	private readonly MediaDeckViewModel _mediaDeck;
	private readonly MediaPoolInspectorViewModel _inspector;
	private readonly OperatorQuickControlStore _store;
	private readonly List<string> _pins;
	private string _status = "Pin editable Inspector properties for fast LIVE operation.";

	public OperatorQuickControlsViewModel(
		OperatorViewModel @operator,
		MediaDeckViewModel mediaDeck,
		MediaPoolInspectorViewModel inspector,
		OperatorQuickControlStore store)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));
		_inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_pins = _store.Load()
			.Where(SupportedPropertyIds.Contains)
			.Take(MaximumPinnedControls)
			.ToList();

		Items = [];
		TogglePinCommand = new OperatorQuickControlCommand(TogglePin);
		ClearPinsCommand = new OperatorQuickControlCommand(ClearPins, () => _pins.Count > 0);

		_operator.PropertyChanged += OnProjectionChanged;
		_mediaDeck.PropertyChanged += OnProjectionChanged;
		_inspector.InspectorProperties.CollectionChanged += OnInspectorPropertiesChanged;
		Refresh();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OperatorQuickControlItemViewModel> Items { get; }
	public ICommand TogglePinCommand { get; }
	public ICommand ClearPinsCommand { get; }
	public bool HasItems => Items.Count > 0;
	public string Status
	{
		get => _status;
		private set
		{
			if (string.Equals(_status, value, StringComparison.Ordinal))
				return;
			_status = value;
			OnPropertyChanged();
		}
	}

	public bool IsPinned(string propertyId) =>
		_pins.Contains(propertyId, StringComparer.Ordinal);

	public void Dispose()
	{
		_operator.PropertyChanged -= OnProjectionChanged;
		_mediaDeck.PropertyChanged -= OnProjectionChanged;
		_inspector.InspectorProperties.CollectionChanged -= OnInspectorPropertiesChanged;
	}

	private void TogglePin(object? parameter)
	{
		if (parameter is not InspectorPropertyViewModel property ||
			!property.IsPinnable ||
			!SupportedPropertyIds.Contains(property.PropertyId))
		{
			Status = "This Inspector value is not available as a Quick Control.";
			return;
		}

		var existing = _pins.FindIndex(id => string.Equals(id, property.PropertyId, StringComparison.Ordinal));
		if (existing >= 0)
		{
			_pins.RemoveAt(existing);
			Status = $"{property.Label} removed from Quick Controls.";
		}
		else
		{
			if (_pins.Count >= MaximumPinnedControls)
			{
				Status = $"Quick Controls are limited to {MaximumPinnedControls} pinned values.";
				return;
			}

			_pins.Add(property.PropertyId);
			Status = $"{property.Label} pinned to Quick Controls.";
		}

		_store.Save(_pins);
		Refresh();
	}

	private void ClearPins(object? parameter)
	{
		_pins.Clear();
		_store.Save(_pins);
		Status = "Quick Controls cleared.";
		Refresh();
	}

	private void Remove(string propertyId)
	{
		if (!_pins.Remove(propertyId))
			return;
		_store.Save(_pins);
		Status = "Quick Control removed.";
		Refresh();
	}

	private void Refresh()
	{
		Items.Clear();
		foreach (var propertyId in _pins)
		{
			var item = BuildItem(propertyId);
			if (item is not null)
				Items.Add(item);
		}

		OnPropertyChanged(nameof(HasItems));
		(ClearPinsCommand as OperatorQuickControlCommand)?.RaiseCanExecuteChanged();
	}

	private OperatorQuickControlItemViewModel? BuildItem(string propertyId)
	{
		var remove = new OperatorQuickControlCommand(_ => Remove(propertyId));
		return propertyId switch
		{
			"production.transition.frames" => new(
				propertyId,
				"Transition Duration",
				$"{_operator.TransitionFrames} frames",
				"DESIRED",
				"ADJUST",
				new OperatorQuickControlCommand(_ => AdjustTransition(-1), () => _operator.TransitionFrames > 2),
				null,
				new OperatorQuickControlCommand(_ => AdjustTransition(1), () => _operator.TransitionFrames < 240),
				remove),

			"clip.playback.autoplay" => new(
				propertyId,
				"Auto Play on Program",
				_mediaDeck.AutoPlayOnProgram ? "ON" : "OFF",
				"DESIRED",
				"TOGGLE",
				null,
				new OperatorQuickControlCommand(_ => ToggleAutoPlay(), () => _mediaDeck.ApplyPlaybackPolicyCommand.CanExecute(null)),
				null,
				remove),

			"clip.playback.end" => new(
				propertyId,
				"End Behavior",
				_mediaDeck.EndBehavior.ToString(),
				"DESIRED",
				"NEXT",
				null,
				new OperatorQuickControlCommand(_ => CycleEndBehavior(), () => _mediaDeck.ApplyPlaybackPolicyCommand.CanExecute(null)),
				null,
				remove),

			"audio.gain" => new(
				propertyId,
				"Audio Gain",
				_operator.SelectedAudioInput is { } input ? $"{input.Gain:0.00}x" : "NO AUDIO SELECTION",
				"DESIRED",
				"ADJUST",
				new OperatorQuickControlCommand(_ => AdjustAudioGain(-0.1), CanAdjustAudio),
				null,
				new OperatorQuickControlCommand(_ => AdjustAudioGain(0.1), CanAdjustAudio),
				remove),

			"audio.mute" => new(
				propertyId,
				"Audio Mute",
				_operator.SelectedAudioInput is { } selected ? (selected.Muted ? "MUTED" : "OPEN") : "NO AUDIO SELECTION",
				"COMMITTED",
				"TOGGLE",
				null,
				new OperatorQuickControlCommand(_ => Execute(_operator.ToggleAudioMuteCommand), () => _operator.ToggleAudioMuteCommand.CanExecute(null)),
				null,
				remove),

			"graphics.visible" => new(
				propertyId,
				"Graphics Visibility",
				_operator.GraphicsVisible ? "VISIBLE" : "HIDDEN",
				"COMMITTED",
				"SHOW / HIDE",
				null,
				new OperatorQuickControlCommand(_ => Execute(_operator.ToggleGraphicsCommand), () => _operator.ToggleGraphicsCommand.CanExecute(null)),
				null,
				remove),

			"graphics.position.x" => BuildGraphicsNumeric(
				propertyId,
				"Graphics X",
				_operator.GraphicsPositionX,
				1.0,
				value => _operator.GraphicsPositionX = value,
				remove),

			"graphics.position.y" => BuildGraphicsNumeric(
				propertyId,
				"Graphics Y",
				_operator.GraphicsPositionY,
				1.0,
				value => _operator.GraphicsPositionY = value,
				remove),

			"graphics.scale" => new(
				propertyId,
				"Graphics Scale",
				$"{_operator.GraphicsScale:0.00}x",
				"DESIRED",
				"ADJUST",
				new OperatorQuickControlCommand(_ => AdjustGraphicsScale(-0.05), () => _operator.ApplyGraphicsCommand.CanExecute(null)),
				null,
				new OperatorQuickControlCommand(_ => AdjustGraphicsScale(0.05), () => _operator.ApplyGraphicsCommand.CanExecute(null)),
				remove),

			"ai.enabled" => new(
				propertyId,
				"AI Effect",
				_operator.AIEnabled ? "ON" : "OFF",
				"COMMITTED",
				_operator.AIEnabled ? "AI OFF" : "AI ON",
				null,
				new OperatorQuickControlCommand(
					_ => Execute(_operator.AIEnabled ? _operator.DisableAIShowcaseCommand : _operator.EnableAIShowcaseCommand),
					() => (_operator.AIEnabled ? _operator.DisableAIShowcaseCommand : _operator.EnableAIShowcaseCommand).CanExecute(null)),
				null,
				remove),

			_ => null
		};
	}

	private OperatorQuickControlItemViewModel BuildGraphicsNumeric(
		string propertyId,
		string label,
		double value,
		double step,
		Action<double> assign,
		ICommand remove) =>
		new(
			propertyId,
			label,
			$"{value:0.##}%",
			"DESIRED",
			"ADJUST",
			new OperatorQuickControlCommand(_ => AdjustGraphics(value - step, assign), () => _operator.ApplyGraphicsCommand.CanExecute(null)),
			null,
			new OperatorQuickControlCommand(_ => AdjustGraphics(value + step, assign), () => _operator.ApplyGraphicsCommand.CanExecute(null)),
			remove);

	private void AdjustTransition(int delta)
	{
		var next = Math.Clamp((long)_operator.TransitionFrames + delta, 2, 240);
		_operator.TransitionFrames = checked((uint)next);
		Refresh();
	}

	private void ToggleAutoPlay()
	{
		_mediaDeck.AutoPlayOnProgram = !_mediaDeck.AutoPlayOnProgram;
		Execute(_mediaDeck.ApplyPlaybackPolicyCommand);
		Refresh();
	}

	private void CycleEndBehavior()
	{
		var values = _mediaDeck.EndBehaviors;
		var index = Array.IndexOf(values.ToArray(), _mediaDeck.EndBehavior);
		_mediaDeck.EndBehavior = values[(index + 1 + values.Count) % values.Count];
		Execute(_mediaDeck.ApplyPlaybackPolicyCommand);
		Refresh();
	}

	private bool CanAdjustAudio() =>
		_operator.SelectedAudioInput is not null &&
		_operator.ApplyAudioGainCommand.CanExecute(null);

	private void AdjustAudioGain(double delta)
	{
		var input = _operator.SelectedAudioInput;
		if (input is null)
			return;
		input.Gain = Math.Clamp(input.Gain + delta, 0.0, 4.0);
		Execute(_operator.ApplyAudioGainCommand);
		Refresh();
	}

	private void AdjustGraphics(double value, Action<double> assign)
	{
		assign(Math.Clamp(value, 0.0, 100.0));
		Execute(_operator.ApplyGraphicsCommand);
		Refresh();
	}

	private void AdjustGraphicsScale(double delta)
	{
		_operator.GraphicsScale = Math.Clamp(_operator.GraphicsScale + delta, 0.05, 8.0);
		Execute(_operator.ApplyGraphicsCommand);
		Refresh();
	}

	private static void Execute(ICommand command)
	{
		if (command.CanExecute(null))
			command.Execute(null);
	}

	private void OnProjectionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
	private void OnInspectorPropertiesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class OperatorQuickControlCommand : ICommand
{
	private readonly Action<object?> _execute;
	private readonly Func<bool> _canExecute;

	public OperatorQuickControlCommand(Action<object?> execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => _canExecute();

	public void Execute(object? parameter)
	{
		if (CanExecute(parameter))
			_execute(parameter);
	}

	public void RaiseCanExecuteChanged() =>
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
