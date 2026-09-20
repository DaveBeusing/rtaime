// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace rtaime.Operator;

public sealed class RuntimePerformanceStatusSlotViewModel : INotifyPropertyChanged
{
	private string _value = "UNAVAILABLE";
	private string _status = "WARNING";
	private string _evidenceState = "UNVERIFIED";
	private string _detail = "Telemetry is unavailable.";

	public RuntimePerformanceStatusSlotViewModel(string key, string label)
	{
		Key = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("Metric key is required.", nameof(key)) : key;
		Label = string.IsNullOrWhiteSpace(label) ? throw new ArgumentException("Metric label is required.", nameof(label)) : label;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Key { get; }
	public string Label { get; }
	public string Value { get => _value; private set => Set(ref _value, value); }
	public string Status { get => _status; private set => Set(ref _status, value); }
	public string EvidenceState { get => _evidenceState; private set => Set(ref _evidenceState, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }

	internal void Apply(PerformanceMetricViewModel metric, bool retained)
	{
		ArgumentNullException.ThrowIfNull(metric);
		Value = metric.Value;
		Status = retained ? "WARNING" : metric.Status;
		EvidenceState = retained ? "UNVERIFIED" : metric.EvidenceState;
		Detail = retained
			? $"Recent Runtime performance value retained while authoritative synchronization is recovering. {metric.Detail}"
			: metric.Detail;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}

public sealed class RuntimePerformanceStatusBarViewModel : IDisposable
{
	private static readonly HashSet<string> RefreshProperties = new(StringComparer.Ordinal)
	{
		nameof(OperatorViewModel.HealthObserved),
		nameof(OperatorViewModel.IsConnected),
		nameof(OperatorViewModel.IsStale)
	};

	private readonly OperatorViewModel _operator;
	private readonly OutputRoutingHealthViewModel _performance;
	private readonly Dictionary<string, RuntimePerformanceStatusSlotViewModel> _slots;
	private bool _disposed;

	public RuntimePerformanceStatusBarViewModel(
		OperatorViewModel @operator,
		OutputRoutingHealthViewModel performance)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_performance = performance ?? throw new ArgumentNullException(nameof(performance));
		_slots = new Dictionary<string, RuntimePerformanceStatusSlotViewModel>(StringComparer.Ordinal)
		{
			["cpu"] = new("cpu", "CPU"),
			["gpu"] = new("gpu", "GPU"),
			["memory"] = new("memory", "RAM"),
			["vram"] = new("vram", "VRAM"),
			["render"] = new("render", "FRAME"),
			["fps"] = new("fps", "FPS"),
			["dropped"] = new("dropped", "DROPPED")
		};
		Slots =
		[
			_slots["cpu"],
			_slots["gpu"],
			_slots["memory"],
			_slots["vram"],
			_slots["render"],
			_slots["fps"],
			_slots["dropped"]
		];

		_operator.PropertyChanged += OperatorPropertyChanged;
		Refresh();
	}

	public IReadOnlyList<RuntimePerformanceStatusSlotViewModel> Slots { get; }

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_operator.PropertyChanged -= OperatorPropertyChanged;
	}

	private void OperatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed)
			return;
		if (!string.IsNullOrEmpty(e.PropertyName) && !RefreshProperties.Contains(e.PropertyName))
			return;

		Refresh();
	}

	private void Refresh()
	{
		var retained = !_operator.IsConnected || _operator.IsStale;
		_slots["cpu"].Apply(_performance.CpuMetric, retained);
		_slots["gpu"].Apply(_performance.GpuMetric, retained);
		_slots["memory"].Apply(_performance.MemoryMetric, retained);
		_slots["vram"].Apply(_performance.VramMetric, retained);
		_slots["render"].Apply(_performance.RenderMetric, retained);
		_slots["fps"].Apply(_performance.FpsMetric, retained);
		_slots["dropped"].Apply(_performance.DroppedMetric, retained);
	}
}
