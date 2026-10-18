// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;

namespace rtaime.Operator;

// One composition lifetime; all commands and observations belong to existing owners.
internal sealed class LiveConsoleBinding : IDisposable
{
    private readonly OperatorViewModel _owner;
    private readonly RundownViewModel _rundown;
    private readonly ShowControlViewModel _show;
    public LiveConsoleBinding(OperatorViewModel owner, RundownViewModel rundown, ShowControlViewModel show)
    {
        _owner = owner; _rundown = rundown; _show = show;
        owner.CaptureLiveObservationAsync = rundown.CaptureLiveObservationAsync;
        owner.LiveCommandsBusy = () => rundown.IsBusy || show.IsBusy;
        owner.ConfirmedShowControlSnapshot += show.ApplyLiveObservation;
        owner.PropertyChanged += Changed;
        rundown.PropertyChanged += Changed;
        show.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OperatorViewModel.IsBusy) or nameof(OperatorViewModel.IsConnected) or
            nameof(OperatorViewModel.IsStale) or nameof(OperatorViewModel.RuntimeStatus) or
            nameof(OperatorViewModel.ProgramSafety) or nameof(OperatorViewModel.SelectedScene) or
            nameof(OperatorViewModel.PreviewSourceName) or nameof(OperatorViewModel.ProgramSourceName))) return;
        if (!ReferenceEquals(sender, _owner) && e.PropertyName == nameof(OperatorViewModel.IsBusy)) _owner.InvalidateLiveObservation();
        _owner.NotifyLiveCommandsChanged();
        _rundown.NotifyLiveAuthorityChanged();
        _show.NotifyLiveAuthorityChanged();
    }
    public void Dispose()
    {
        _owner.CaptureLiveObservationAsync = null;
        _owner.LiveCommandsBusy = null;
        _owner.ConfirmedShowControlSnapshot -= _show.ApplyLiveObservation;
        _owner.PropertyChanged -= Changed;
        _rundown.PropertyChanged -= Changed;
        _show.PropertyChanged -= Changed;
    }
}
