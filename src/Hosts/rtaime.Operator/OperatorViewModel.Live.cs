// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;

namespace rtaime.Operator;

public sealed partial class OperatorViewModel
{
    internal Func<CancellationToken, Task<Action?>>? CaptureLiveObservationAsync { get; set; }
    internal Func<bool>? LiveCommandsBusy { get; set; }
    internal void InvalidateLiveObservation() => _audioRefreshDelivery.Invalidate();
    internal bool CanOperateLive => CanControl();
    public string LiveAvailability => !IsConnected || IsStale ? "Control evidence is stale. Synchronize before taking." :
        IsBusy || LiveCommandsBusy?.Invoke() == true ? "Production command pending. Wait for confirmation." :
        ProgramSafety == OperatorProgramSafetyStates.Blocked ? $"Production blocked: {GlobalReadinessSummary}. Open Health." :
        !string.Equals(RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase) ? $"Runtime {RuntimeStatus}. Restore Runtime readiness." :
        "Confirmed Control state; Runtime revalidates every action.";
    public string PreviewTakeDetail => $"CUT / AUTO → {PreviewSourceName} · {LiveAvailability}";
    public string SceneTakeDetail => $"SCENE TAKE → {SelectedScene?.Name ?? "select a scene"} · {LiveAvailability}";
    internal void NotifyLiveCommandsChanged()
    {
        RaiseCommandState();
        NotifyLivePresentation();
    }
    private void NotifyLiveProperty(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    private void NotifyLivePresentation()
    {
        NotifyLiveProperty(nameof(LiveAvailability));
        NotifyLiveProperty(nameof(PreviewTakeDetail));
        NotifyLiveProperty(nameof(SceneTakeDetail));
    }

    private void ApplyLiveEvidence(OperatorStatusSnapshot snapshot)
    {
        var previewId = snapshot.Production.Routing.PreviewSourceId.ToString();
        var programId = snapshot.Production.Routing.ProgramSourceId.ToString();
        // Preserve row identity and local selection; status refresh must not select a target.
        var selectedSourceId = SelectedSource?.Id;
        var sourceById = Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var projected = snapshot.Sources.Select((descriptor, index) => {
            if (!sourceById.TryGetValue(descriptor.Id, out var source))
                source = new OperatorSourceTileViewModel(descriptor);
            else source.ApplyDescriptor(descriptor);
            source.ApplyDisplayIndex(index + 1);
            source.ApplyRouting(previewId, programId);
            return source;
        }).ToArray();
        if (!Sources.SequenceEqual(projected))
        {
            Sources.Clear();
            foreach (var source in projected) Sources.Add(source);
            SelectedSource = Sources.FirstOrDefault(source => source.Id == selectedSourceId);
        }
        var selectedSceneId = SelectedScene?.Id;
        var sceneById = Scenes.ToDictionary(scene => scene.Id, StringComparer.Ordinal);
        var scenes = new List<OperatorSceneViewModel>();
        foreach (var descriptor in snapshot.Scenes)
        {
            var programSource = Sources.FirstOrDefault(source => source.Id == descriptor.ProgramSourceId);
            if (programSource is null) continue;
            if (!sceneById.TryGetValue(descriptor.Id, out var scene))
                scene = new OperatorSceneViewModel(descriptor, programSource, scenes.Count + 1);
            else scene.Apply(descriptor, programSource, scenes.Count + 1);
            scene.ApplyEvidence(previewId, snapshot.Production.ActiveSceneId?.ToString());
            scenes.Add(scene);
        }
        if (!Scenes.SequenceEqual(scenes))
        {
            Scenes.Clear();
            foreach (var scene in scenes) Scenes.Add(scene);
            SelectedScene = Scenes.FirstOrDefault(scene => scene.Id == selectedSceneId);
        }
        PreviewSourceId = previewId;
        ProgramSourceId = programId;
        PreviewSourceName = ResolveSourceName(snapshot, previewId);
        ProgramSourceName = ResolveSourceName(snapshot, programId);
        ActiveSceneId = snapshot.Production.ActiveSceneId?.ToString() ?? "—";
        ActiveSceneName = Scenes.FirstOrDefault(scene => scene.Id == ActiveSceneId)?.Name ?? "NO CONFIRMED SCENE";
        SceneFailureReason = snapshot.Production.ActiveSceneId is null
            ? "Current Program state was not committed by a Scene activation." : "NONE";
        RuntimeStatus = snapshot.RuntimeStatus;
        RevisionLabel = $"REV {snapshot.Production.Revision.Value}";
        CommitStatus = $"CONFIRMED · REV {snapshot.Production.Revision.Value}";
        ConfirmedShowControlSnapshot?.Invoke(snapshot.ShowControl);
        NotifyLiveCommandsChanged();
    }
}
