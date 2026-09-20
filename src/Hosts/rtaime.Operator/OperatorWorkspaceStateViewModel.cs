// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorWorkspaceStateViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly OperatorViewModel _control;
	private readonly MediaPoolInspectorViewModel _mediaPool;
	private readonly MediaDeckViewModel _mediaDeck;
	private readonly MediaTimelineViewModel _timeline;
	private readonly CompositingGraphViewModel _compositing;
	private readonly OutputRoutingHealthViewModel _output;
	private readonly HealthCenterViewModel _health;
	private readonly StartupLifecycleViewModel _startup;
	private OperatorUiStateSnapshot _shell = Ready();
	private OperatorUiStateSnapshot _mediaLibrary = Ready();
	private OperatorUiStateSnapshot _preview = Ready();
	private OperatorUiStateSnapshot _program = Ready();
	private OperatorUiStateSnapshot _inspector = Ready();
	private OperatorUiStateSnapshot _timelineState = Ready();
	private OperatorUiStateSnapshot _compositingState = Ready();
	private OperatorUiStateSnapshot _outputState = Ready();
	private OperatorUiStateSnapshot _healthState = Ready();
	private bool _disposed;

	public OperatorWorkspaceStateViewModel(
		OperatorViewModel control,
		MediaPoolInspectorViewModel mediaPool,
		MediaDeckViewModel mediaDeck,
		MediaTimelineViewModel timeline,
		CompositingGraphViewModel compositing,
		OutputRoutingHealthViewModel output,
		HealthCenterViewModel health,
		StartupLifecycleViewModel startup)
	{
		_control = control ?? throw new ArgumentNullException(nameof(control));
		_mediaPool = mediaPool ?? throw new ArgumentNullException(nameof(mediaPool));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_compositing = compositing ?? throw new ArgumentNullException(nameof(compositing));
		_output = output ?? throw new ArgumentNullException(nameof(output));
		_health = health ?? throw new ArgumentNullException(nameof(health));
		_startup = startup ?? throw new ArgumentNullException(nameof(startup));

		_control.PropertyChanged += SourcePropertyChanged;
		_mediaPool.PropertyChanged += SourcePropertyChanged;
		_mediaDeck.PropertyChanged += SourcePropertyChanged;
		_timeline.PropertyChanged += SourcePropertyChanged;
		_compositing.PropertyChanged += SourcePropertyChanged;
		_output.PropertyChanged += SourcePropertyChanged;
		_health.PropertyChanged += SourcePropertyChanged;
		_startup.PropertyChanged += SourcePropertyChanged;
		_compositing.Nodes.CollectionChanged += SourceCollectionChanged;
		_output.Outputs.CollectionChanged += SourceCollectionChanged;
		_health.Subsystems.CollectionChanged += SourceCollectionChanged;
		Refresh();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public OperatorUiStateSnapshot Shell
	{
		get => _shell;
		private set
		{
			if (!Set(ref _shell, value))
				return;
			OnPropertyChanged(nameof(IsShellAvailable));
		}
	}

	public OperatorUiStateSnapshot MediaLibrary
	{
		get => _mediaLibrary;
		private set => Set(ref _mediaLibrary, value);
	}

	public OperatorUiStateSnapshot Preview
	{
		get => _preview;
		private set => Set(ref _preview, value);
	}

	public OperatorUiStateSnapshot Program
	{
		get => _program;
		private set => Set(ref _program, value);
	}

	public OperatorUiStateSnapshot Inspector
	{
		get => _inspector;
		private set => Set(ref _inspector, value);
	}

	public OperatorUiStateSnapshot Timeline
	{
		get => _timelineState;
		private set => Set(ref _timelineState, value);
	}

	public OperatorUiStateSnapshot Compositing
	{
		get => _compositingState;
		private set => Set(ref _compositingState, value);
	}

	public OperatorUiStateSnapshot Output
	{
		get => _outputState;
		private set => Set(ref _outputState, value);
	}

	public OperatorUiStateSnapshot Health
	{
		get => _healthState;
		private set => Set(ref _healthState, value);
	}

	public bool IsShellAvailable => Shell.IsReady;

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_control.PropertyChanged -= SourcePropertyChanged;
		_mediaPool.PropertyChanged -= SourcePropertyChanged;
		_mediaDeck.PropertyChanged -= SourcePropertyChanged;
		_timeline.PropertyChanged -= SourcePropertyChanged;
		_compositing.PropertyChanged -= SourcePropertyChanged;
		_output.PropertyChanged -= SourcePropertyChanged;
		_health.PropertyChanged -= SourcePropertyChanged;
		_startup.PropertyChanged -= SourcePropertyChanged;
		_compositing.Nodes.CollectionChanged -= SourceCollectionChanged;
		_output.Outputs.CollectionChanged -= SourceCollectionChanged;
		_health.Subsystems.CollectionChanged -= SourceCollectionChanged;
	}

	private void SourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

	private void SourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

	private void Refresh()
	{
		if (_disposed)
			return;

		var recovering = IsRecovering();
		var offline = !_control.IsConnected && !_control.IsBusy;

		Shell = ResolveShell();
		MediaLibrary = ResolveMediaLibrary();
		Preview = ResolvePreview(recovering, offline);
		Program = ResolveProgram(recovering, offline);
		Inspector = ResolveInspector();
		Timeline = ResolveTimeline();
		Compositing = ResolveCompositing(recovering, offline);
		Output = ResolveOutput(recovering, offline);
		Health = ResolveHealth();
	}

	private OperatorUiStateSnapshot ResolveShell()
	{
		if (_startup.HasCriticalFailure)
			return Error("Operator startup failed", _startup.Summary);

		if (_startup.IsShellAvailable)
			return Ready();

		return Loading("Preparing operator interface", _startup.Summary);
	}

	private OperatorUiStateSnapshot ResolveMediaLibrary()
	{
		var state = OperatorUiStateMachine.Resolve(
			hasContent: _mediaPool.HasItems,
			isLoading: _mediaPool.IsLoading,
			hasError: _mediaPool.HasError);

		return state switch
		{
			OperatorUiStateKind.Error => Error(
				"Media unavailable",
				string.IsNullOrWhiteSpace(_mediaPool.ErrorState)
					? "The media source could not be loaded."
					: _mediaPool.ErrorState),
			OperatorUiStateKind.Loading => Loading(
				"Loading media…",
				"Preparing media metadata and transport state."),
			OperatorUiStateKind.Empty when _mediaPool.EmptyState.Contains("match", StringComparison.OrdinalIgnoreCase) =>
				Empty("No media matches", _mediaPool.EmptyState),
			OperatorUiStateKind.Empty => Empty(
				"No media loaded",
				"Import media to begin."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolvePreview(bool recovering, bool offline)
	{
		var hasSource = HasValue(_control.PreviewSourceId);
		var viewerError = ContainsFailure(_control.PreviewViewerState);
		var state = OperatorUiStateMachine.Resolve(
			hasContent: hasSource,
			isLoading: _mediaDeck.IsBusy && !hasSource,
			isOffline: offline,
			hasError: viewerError,
			isRecovering: recovering);

		return state switch
		{
			OperatorUiStateKind.Error => Error(
				"Preview unavailable",
				"The Preview viewer reported an error."),
			OperatorUiStateKind.Recovering => Recovering(
				"Restoring Preview…",
				RecoveryDetail()),
			OperatorUiStateKind.Offline => Offline(
				"Preview offline",
				"Waiting for the authoritative Control connection."),
			OperatorUiStateKind.Loading => Loading(
				"Loading Preview…",
				"Preparing the selected media source."),
			OperatorUiStateKind.Empty => Empty(
				"No source selected",
				"Select a media item, scene or live source."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolveProgram(bool recovering, bool offline)
	{
		var hasSource = HasValue(_control.ProgramSourceId);
		var viewerError = ContainsFailure(_control.ProgramViewerState);
		var state = OperatorUiStateMachine.Resolve(
			hasContent: hasSource,
			isOffline: offline,
			isUnavailable: !hasSource,
			hasError: viewerError,
			isRecovering: recovering);

		return state switch
		{
			OperatorUiStateKind.Error => Error(
				"Program unavailable",
				"The Program viewer reported an error."),
			OperatorUiStateKind.Recovering => Recovering(
				"Restoring Program…",
				"Program authority remains blocked until recovery completes."),
			OperatorUiStateKind.Offline => Offline(
				"Program offline",
				"Waiting for authoritative Runtime state."),
			OperatorUiStateKind.Unavailable => Unavailable(
				"Program not configured",
				"Assign a Program source to enable output monitoring."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolveInspector() =>
		_mediaPool.HasSelection
			? Ready()
			: Empty(
				"No selection",
				"Select a media item, timeline item, cue or compositing node.");

	private OperatorUiStateSnapshot ResolveTimeline()
	{
		var hasError = !string.IsNullOrWhiteSpace(_timeline.Failure);
		var state = OperatorUiStateMachine.Resolve(
			hasContent: _timeline.IsLoaded,
			isLoading: _mediaDeck.IsBusy && !_timeline.IsLoaded,
			hasError: hasError);

		return state switch
		{
			OperatorUiStateKind.Error => Error(
				"Timeline unavailable",
				_timeline.Failure ?? "The timeline could not be loaded."),
			OperatorUiStateKind.Loading => Loading(
				"Loading timeline…",
				"Preparing transport, markers and cue projections."),
			OperatorUiStateKind.Empty => Empty(
				"No timeline loaded",
				"Load media to begin editing."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolveCompositing(bool recovering, bool offline)
	{
		var state = OperatorUiStateMachine.Resolve(
			hasContent: _compositing.Nodes.Count > 0,
			isLoading: _control.IsBusy && !offline,
			isOffline: offline,
			isRecovering: recovering);

		return state switch
		{
			OperatorUiStateKind.Recovering => Recovering(
				"Rebuilding render pipeline…",
				RecoveryDetail()),
			OperatorUiStateKind.Offline => Offline(
				"Compositing offline",
				"Connect to Runtime to inspect the authoritative graph."),
			OperatorUiStateKind.Loading => Loading(
				"Building compositing graph…",
				"Waiting for Runtime topology and render evidence."),
			OperatorUiStateKind.Empty => Empty(
				"No compositing graph",
				"Load or select production media to build the graph."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolveOutput(bool recovering, bool offline)
	{
		var hasProgramRoute = HasValue(_control.ProgramSourceId);
		var state = OperatorUiStateMachine.Resolve(
			hasContent: hasProgramRoute && _output.Outputs.Count > 0,
			isOffline: offline,
			isUnavailable: !hasProgramRoute,
			isRecovering: recovering);

		return state switch
		{
			OperatorUiStateKind.Recovering => Recovering(
				"Restoring output routing…",
				RecoveryDetail()),
			OperatorUiStateKind.Offline => Offline(
				"Output routing offline",
				"Runtime routing evidence is unavailable."),
			OperatorUiStateKind.Unavailable => Unavailable(
				"Output not configured",
				"Configure an output to enable Program."),
			OperatorUiStateKind.Empty => Empty(
				"No outputs available",
				"No output roles are currently exposed."),
			_ => Ready()
		};
	}

	private OperatorUiStateSnapshot ResolveHealth()
	{
		if (_health.Subsystems.Count == 0)
			return Loading("Loading health evidence…", "Waiting for subsystem observations.");

		return _health.OverallState switch
		{
			"INITIALIZING" => Loading(
				"Qualifying production readiness…",
				_health.OverallSummary),
			"RECOVERING" => Recovering(
				"Recovering runtime…",
				_health.OverallSummary),
			"FAILED" => Error(
				"Runtime health failed",
				_health.OverallSummary),
			"NOT READY" => Unavailable(
				"Production not ready",
				_health.OverallSummary),
			"DEGRADED" => Unavailable(
				"Production degraded",
				_health.OverallSummary),
			_ => Ready()
		};
	}

	private bool IsRecovering() =>
		_control.IsStale ||
		string.Equals(_control.EngineLifecycleState, "RECOVERING", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(_control.ConnectionState, "RESYNCING", StringComparison.OrdinalIgnoreCase) ||
		_control.CommandStatus.Contains("RECOVER", StringComparison.OrdinalIgnoreCase) ||
		_control.CommandStatus.Contains("RESYNC", StringComparison.OrdinalIgnoreCase);

	private string RecoveryDetail() =>
		!string.IsNullOrWhiteSpace(_control.EngineLifecycleDetail)
			? _control.EngineLifecycleDetail
			: "Re-establishing authoritative Runtime state.";

	private static bool HasValue(string? value) =>
		!string.IsNullOrWhiteSpace(value) &&
		!string.Equals(value, "—", StringComparison.Ordinal) &&
		!string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase) &&
		!string.Equals(value, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase);

	private static bool ContainsFailure(string value) =>
		value.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
		value.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
		value.Contains("FAULT", StringComparison.OrdinalIgnoreCase);

	private static OperatorUiStateSnapshot Ready() =>
		new(OperatorUiStateKind.Ready, string.Empty, string.Empty);

	private static OperatorUiStateSnapshot Loading(string title, string detail) =>
		new(OperatorUiStateKind.Loading, title, detail);

	private static OperatorUiStateSnapshot Empty(string title, string detail) =>
		new(OperatorUiStateKind.Empty, title, detail);

	private static OperatorUiStateSnapshot Offline(string title, string detail) =>
		new(OperatorUiStateKind.Offline, title, detail);

	private static OperatorUiStateSnapshot Unavailable(string title, string detail) =>
		new(OperatorUiStateKind.Unavailable, title, detail);

	private static OperatorUiStateSnapshot Error(string title, string detail) =>
		new(OperatorUiStateKind.Error, title, detail);

	private static OperatorUiStateSnapshot Recovering(string title, string detail) =>
		new(OperatorUiStateKind.Recovering, title, detail);

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;

		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
