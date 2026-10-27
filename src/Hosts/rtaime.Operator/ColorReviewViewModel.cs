// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Local COMPOSITING review mode. Observes the one monitoring owner and the one
/// governed Inspector; never creates a media source, decoder, production edit
/// or independently persisted color state.
/// </summary>
public sealed class ColorReviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OperatorMonitoringViewModel _monitoring;
    private bool _reviewSelected;
    private bool _workspaceActive;
    private bool _scopeLease;
    private bool _previousScopesEnabled;
    private MonitoringStreamKind _selectedSource = MonitoringStreamKind.Program;
    private bool _disposed;

    public ColorReviewViewModel(OperatorMonitoringViewModel monitoring, MediaPoolInspectorViewModel inspector)
    {
        _monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
        Inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _monitoring.PropertyChanged += OnMonitoringChanged;
        Inspector.PropertyChanged += OnInspectorChanged;
        ShowReviewCommand = new OperatorShellCommand(() => SelectReview(true));
        ShowGraphCommand = new OperatorShellCommand(() => SelectReview(false));
        SelectScopeSourceCommand = new OperatorShellCommand(value =>
            SetScopeSource(string.Equals(value?.ToString(), "PREVIEW", StringComparison.OrdinalIgnoreCase)
                ? MonitoringStreamKind.Preview : MonitoringStreamKind.Program));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public OperatorMonitoringViewModel Monitoring => _monitoring;
    public MediaPoolInspectorViewModel Inspector { get; }
    public ICommand ShowReviewCommand { get; }
    public ICommand ShowGraphCommand { get; }
    public ICommand SelectScopeSourceCommand { get; }
    public bool IsReviewSelected => _reviewSelected;
    public Visibility GraphVisibility => _reviewSelected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReviewVisibility => _reviewSelected ? Visibility.Visible : Visibility.Collapsed;
    public string ModeLabel => _reviewSelected ? "COLOR REVIEW" : "LAYER / GRAPH";
    public MonitoringStreamKind ScopeSource => _selectedSource;
    public bool IsPreviewScope => _selectedSource == MonitoringStreamKind.Preview;
    public bool AnalysisEnabled => _scopeLease && _monitoring.ScopesEnabled;
    public OperatorGpuMonitoringFrame? SelectedScopeGpuFrame => IsPreviewScope
        ? _monitoring.PreviewGpuFrame : _monitoring.ProgramGpuFrame;
    public MediaScopeSnapshot? SelectedScopes
    {
        get
        {
            if (!_scopeLease || !IsSelectedObservationLive) return null;
            var sample = IsPreviewScope ? _monitoring.PreviewScopes : _monitoring.ProgramScopes;
            if (sample is null) return null;
            var gpu = SelectedScopeGpuFrame;
            // A derived GPU sample is never accepted as current after its source
            // resource is replaced. Older 5 Hz samples are labelled by sequence.
            if (sample.ProcessingPath == MediaScopeProcessingPath.GpuSharedResource &&
                (gpu is null || sample.SequenceNumber > gpu.SequenceNumber ||
                 sample.SourceWidth != gpu.Resource.Format.Width ||
                 sample.SourceHeight != gpu.Resource.Format.Height ||
                 sample.Color != gpu.Resource.Format.Color))
                return null;
            return sample;
        }
    }
    public bool IsSelectedObservationLive => (IsPreviewScope
        ? _monitoring.PreviewState : _monitoring.ProgramState) == "LIVE";
    public string ScopeSourceLabel => IsPreviewScope ? "CONFIRMED PREVIEW B" : "PROGRAM A";
    public string ScopeFrameLabel => SelectedScopes is { } sample
        ? $"SAMPLED FRAME {sample.SequenceNumber} · {sample.ProcessingPath} · ≤5 Hz"
        : "NO CURRENT QUALIFIED SCOPE SAMPLE";
    public string ColorBasisLabel => SelectedScopes is { } sample
        ? sample.Color.IsComplete
            ? $"SOURCE METADATA COMPLETE · {ScopeSourceLabel} · DISPLAY-CODE SCOPES; PHYSICAL COLOR UNVERIFIED"
            : "COLOR METADATA INCOMPLETE · DISPLAY-CODE PASSTHROUGH; VECTORSCOPE UNAVAILABLE"
        : "COLOR EVIDENCE PENDING · NO CALIBRATION OR HDR CLAIM";
    public string SourceFormatLabel => IsPreviewScope ? _monitoring.PreviewFormat : _monitoring.ProgramFormat;
    public string GradeStatus => Inspector.IsSelectedColorGradeProcessingNode
        ? "COLOR GRADE SELECTED · EDIT & APPLY IN THE SHARED INSPECTOR"
        : "SELECT A COLOR GRADE PROCESSING NODE IN LAYERS / GRAPH; NO UNSUPPORTED GRADE CONTROLS";

    public void SetWorkspaceActive(bool active)
    {
        if (_disposed || _workspaceActive == active) return;
        _workspaceActive = active;
        UpdateScopeLease();
    }

    private void SelectReview(bool selected)
    {
        if (_disposed || _reviewSelected == selected) return;
        _reviewSelected = selected;
        Notify(nameof(IsReviewSelected), nameof(GraphVisibility), nameof(ReviewVisibility), nameof(ModeLabel));
        UpdateScopeLease();
    }

    private void SetScopeSource(MonitoringStreamKind source)
    {
        if (_disposed || _selectedSource == source) return;
        _selectedSource = source;
        if (_scopeLease) _monitoring.ActiveScopeStream = source;
        NotifyReview();
    }

    private void UpdateScopeLease()
    {
        var shouldAnalyze = _reviewSelected && _workspaceActive && !_disposed;
        if (shouldAnalyze && !_scopeLease)
        {
            _previousScopesEnabled = _monitoring.ScopesEnabled;
            _scopeLease = true;
            _monitoring.ActiveScopeStream = _selectedSource;
            _monitoring.ScopesEnabled = true;
        }
        else if (!shouldAnalyze && _scopeLease)
        {
            _scopeLease = false;
            _monitoring.ActiveScopeStream = MonitoringStreamKind.Program;
            if (!_previousScopesEnabled) _monitoring.ScopesEnabled = false;
        }
        NotifyReview();
    }

    private void OnMonitoringChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperatorMonitoringViewModel.ProgramScopes)
            or nameof(OperatorMonitoringViewModel.PreviewScopes)
            or nameof(OperatorMonitoringViewModel.ProgramGpuFrame)
            or nameof(OperatorMonitoringViewModel.PreviewGpuFrame)
            or nameof(OperatorMonitoringViewModel.ProgramState)
            or nameof(OperatorMonitoringViewModel.PreviewState)
            or nameof(OperatorMonitoringViewModel.ProgramFormat)
            or nameof(OperatorMonitoringViewModel.PreviewFormat)
            or nameof(OperatorMonitoringViewModel.ScopesEnabled))
            NotifyReview();
    }

    private void OnInspectorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaPoolInspectorViewModel.IsSelectedColorGradeProcessingNode)
            or nameof(MediaPoolInspectorViewModel.ProcessingStateLabel))
            Notify(nameof(GradeStatus));
    }

    private void NotifyReview() => Notify(nameof(ScopeSource), nameof(IsPreviewScope),
        nameof(AnalysisEnabled), nameof(SelectedScopeGpuFrame), nameof(SelectedScopes),
        nameof(ScopeSourceLabel), nameof(ScopeFrameLabel), nameof(ColorBasisLabel),
        nameof(SourceFormatLabel));

    private void Notify(params string[] properties)
    {
        foreach (var name in properties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetWorkspaceActive(false);
        _disposed = true;
        _monitoring.PropertyChanged -= OnMonitoringChanged;
        Inspector.PropertyChanged -= OnInspectorChanged;
    }
}
