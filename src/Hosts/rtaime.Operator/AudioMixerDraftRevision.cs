// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

// A retained mixer draft belongs to the authoritative production/configuration
// revision and the current synchronization session, never to a meter refresh.
// Review acknowledges newer evidence locally; only Apply requests a mutation.
public sealed partial class OperatorAudioSourceStripViewModel
{
    private string? _draftOrigin;
    public bool NeedsReview => HasDraftChanges && _draftOrigin is not null &&
        !string.Equals(_draftOrigin, _owner.CurrentAudioDraftToken, StringComparison.Ordinal);
    public Visibility DraftReviewVisibility => NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    public string DraftReviewDetail => NeedsReview
        ? "SOURCE DRAFT HELD · production revision or connection changed. Compare confirmed gain, mute, EQ and bus routing; REBASE retains this draft locally, DISCARD restores confirmed values."
        : string.Empty;
    public ICommand RebaseDraftCommand { get; private set; } = null!;

    private void InitializeDraftReview()
    {
        RebaseDraftCommand = new AsyncRelayCommand(() =>
        {
            _draftOrigin = _owner.CurrentAudioDraftToken;
            MutationState = "CONFIRMED";
            DraftChanged();
            return Task.CompletedTask;
        }, () => NeedsReview && CanEditDraft && InputsValid && _owner.CanMutate() &&
            _owner.CurrentAudioDraftToken is not null);
    }

    private void TrackDraftRevision()
    {
        if (!HasDraftChanges)
            _draftOrigin = null;
        else
            _draftOrigin ??= _owner.CurrentAudioDraftToken ?? "UNVERIFIED";
        NotifyDraftReview();
    }

    internal void NotifyDraftReview()
    {
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(DraftReviewVisibility));
        OnPropertyChanged(nameof(DraftReviewDetail));
        OnPropertyChanged(nameof(DraftState));
        (RebaseDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed partial class OperatorAudioBusStripViewModel
{
    private string? _draftOrigin;
    public bool NeedsReview => HasDraftChanges && _draftOrigin is not null &&
        !string.Equals(_draftOrigin, _owner.CurrentAudioDraftToken, StringComparison.Ordinal);
    public Visibility DraftReviewVisibility => NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    public string DraftReviewDetail => NeedsReview
        ? "BUS DRAFT HELD · production revision or connection changed. Compare confirmed bus gain, mute and dynamics; REBASE retains this draft locally, DISCARD restores confirmed values."
        : string.Empty;
    public ICommand RebaseDraftCommand { get; private set; } = null!;

    private void InitializeDraftReview()
    {
        RebaseDraftCommand = new AsyncRelayCommand(() =>
        {
            _draftOrigin = _owner.CurrentAudioDraftToken;
            MutationState = "CONFIRMED";
            DraftChanged();
            return Task.CompletedTask;
        }, () => NeedsReview && CanEditDraft && InputsValid && _owner.CanMutate() &&
            _owner.CurrentAudioDraftToken is not null);
    }

    private void TrackDraftRevision()
    {
        if (!HasDraftChanges)
            _draftOrigin = null;
        else
            _draftOrigin ??= _owner.CurrentAudioDraftToken ?? "UNVERIFIED";
        NotifyDraftReview();
    }

    internal void NotifyDraftReview()
    {
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(DraftReviewVisibility));
        OnPropertyChanged(nameof(DraftReviewDetail));
        OnPropertyChanged(nameof(DraftState));
        (RebaseDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
