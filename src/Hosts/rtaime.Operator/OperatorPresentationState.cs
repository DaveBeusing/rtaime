// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

public sealed partial class OperatorShellViewModel
{
    private sealed record PresentationRestore(string Workspace, OperatorWorkspaceLayoutSettings Layout, bool Fullscreen,
        bool MonitorActive, string MonitorViewer, bool MonitorCenter);
    private PresentationRestore? _presentation;
    private string _presentationMode = "NORMAL";
    public bool PresentationActive => _presentation is not null;
    public string PresentationMode => _presentationMode;
    internal void BeginPresentation(string mode)
    {
        RestorePresentation();
        CaptureCurrentWorkspace();
        _presentation = new(SelectedWorkspace, CurrentWorkspaceLayout(), IsFullscreen, _monitorFullscreenActive,
            _monitorFullscreenRestoreViewerMode, _monitorFullscreenRestoreCenterMaximized);
        _presentationMode = mode;
        if (mode == "CINEMA")
        {
            SelectedWorkspace = OperatorWorkspaceNames.Media;
            ApplyWorkspaceLayout(_workspaceLayouts[OperatorWorkspaceNames.Media]);
        }
        NotifyPresentation();
    }
    public void RestorePresentation()
    {
        if (_presentation is not { } restore) return;
        _presentation = null; _presentationMode = "NORMAL";
        _monitorFullscreenActive = restore.MonitorActive;
        _monitorFullscreenRestoreViewerMode = restore.MonitorViewer;
        _monitorFullscreenRestoreCenterMaximized = restore.MonitorCenter;
        SelectedWorkspace = restore.Workspace;
        ApplyWorkspaceLayout(restore.Layout);
        _setFullscreen(restore.Fullscreen);
        IsFullscreen = restore.Fullscreen;
        NotifyPresentation();
    }
    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(PresentationActive)); OnPropertyChanged(nameof(PresentationMode));
        OnPropertyChanged(nameof(TemplateCaptureWorkspace));
        (ExitFullscreenCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }
}
