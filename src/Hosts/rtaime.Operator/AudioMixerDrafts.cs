// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
namespace rtaime.Operator;

public interface IAudioMixerDraftStrip
{
    bool InputsValid { get; set; }
    double GainDb { get; set; }
    bool CanEditDraft { get; }
    void UnityDraft();
    InspectorAttributePayload? CopyGain();
    bool PasteGain(InspectorAttributePayload payload);
}

public static class AudioMixerPresentation
{
    public static bool ValidPeak(double value) => double.IsFinite(value) && value >= 0;
    public static string PeakLabel(double value) => !ValidPeak(value) ? "—" : value == 0 ? "−∞" : $"{20 * Math.Log10(value):0.0}";
    public static double ToDb(double gain) => gain <= 0 ? -96 : 20 * Math.Log10(gain);
    public static double FromDb(double db) => db <= -96 ? 0 : Math.Min(4, Math.Pow(10, db / 20));
    public static InspectorPropertyDefinition GainDb { get; } = new("mixer.gain.db", "Gain dB", -96, 20 * Math.Log10(4), 0, 0.1, "dB; -96 = silence");
    public static string MeterDisclaimer => "Runtime sample peaks · RMS / LUFS / true peak unavailable · no peak hold or display decay";
}

public sealed partial class OperatorAudioSourceStripViewModel : IAudioMixerDraftStrip
{
    private bool _inputsValid = true;
    public bool InputsValid { get => _inputsValid; set { if (Set(ref _inputsValid, value)) DraftChanged(); } }
    public double GainDb { get => AudioMixerPresentation.ToDb(Gain); set { if (AudioMixerPresentation.GainDb.Accepts(value)) Gain = AudioMixerPresentation.FromDb(value); } }
    public string DraftMuteLabel => Muted ? "DRAFT MUTED" : "DRAFT OPEN";
    public string DraftContributionLabel => FollowRoutedSource ? "DRAFT ROUTED" : "DRAFT ALWAYS";
    public string DraftRoutingSummary => string.Join(" · ", Assignments.Where(a => a.DraftAssigned).Select(a => a.BusLabel));
    public void UnityDraft() { if (CanEditDraft) Gain = 1; }
    public InspectorAttributePayload? CopyGain() => InputsValid && _owner.AttributeRevision is { } revision
        ? new(1, revision, new InspectorGainAttributes(Gain)) : null;
    public bool PasteGain(InspectorAttributePayload payload)
    {
        if (!CanEditDraft || payload.Version != 1 || payload.RevisionToken != _owner.AttributeRevision ||
            payload.Values is not InspectorGainAttributes { IsValid: true } gain) return false;
        Gain = gain.Gain; return true;
    }
    internal void AssignmentDraftChanged() => DraftChanged();
}

public sealed partial class OperatorAudioBusStripViewModel : IAudioMixerDraftStrip
{
    private bool _inputsValid = true;
    public bool InputsValid { get => _inputsValid; set { if (Set(ref _inputsValid, value)) DraftChanged(); } }
    public double GainDb { get => AudioMixerPresentation.ToDb(MasterGain); set { if (AudioMixerPresentation.GainDb.Accepts(value)) MasterGain = AudioMixerPresentation.FromDb(value); } }
    public string DraftMuteLabel => Muted ? "DRAFT MUTED" : "DRAFT OPEN";
    public void UnityDraft() { if (CanEditDraft) MasterGain = 1; }
    public InspectorAttributePayload? CopyGain() => InputsValid && _owner.AttributeRevision is { } revision
        ? new(1, revision, new InspectorGainAttributes(MasterGain)) : null;
    public bool PasteGain(InspectorAttributePayload payload)
    {
        if (!CanEditDraft || payload.Version != 1 || payload.RevisionToken != _owner.AttributeRevision ||
            payload.Values is not InspectorGainAttributes { IsValid: true } gain) return false;
        MasterGain = gain.Gain; return true;
    }
}

public sealed partial class OperatorAudioBusAssignmentViewModel
{
    private bool _draftAssigned;
    public bool DraftAssigned
    {
        get => _draftAssigned;
        set
        {
            if (!_source.CanEditDraft || !Set(ref _draftAssigned, value)) return;
            OnPropertyChanged(nameof(HasDraftChanges)); OnPropertyChanged(nameof(DraftLabel));
            _source.AssignmentDraftChanged();
        }
    }
    public bool HasDraftChanges => DraftAssigned != IsAssigned;
    public string DraftLabel => $"{_source.SourceName} → {BusLabel}: draft {(DraftAssigned ? "ON" : "OFF")}, confirmed {(IsAssigned ? "ON" : "OFF")}";
    internal void ResetDraft()
    {
        _draftAssigned = IsAssigned;
        OnPropertyChanged(nameof(DraftAssigned)); OnPropertyChanged(nameof(HasDraftChanges)); OnPropertyChanged(nameof(DraftLabel));
    }
}

// A narrow exchange seam over the Inspector's existing typed, revision-qualified clipboard.
public sealed partial class MediaPoolInspectorViewModel
{
    internal InspectorAttributePayload? ReadAudioGainClipboard() => _attributeClipboard;
    internal bool WriteAudioGainClipboard(InspectorAttributePayload payload)
    {
        if (payload.Version != 1 || payload.RevisionToken != CurrentRevisionToken ||
            payload.Values is not InspectorGainAttributes { IsValid: true }) return false;
        _attributeClipboard = payload; NotifyInspectorHistory(); return true;
    }
}
