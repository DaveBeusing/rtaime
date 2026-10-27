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
        (_owner.CurrentAudioDraftToken is { } current
            ? !string.Equals(_draftOrigin, current, StringComparison.Ordinal)
            : _draftOrigin != "UNVERIFIED");
    public Visibility DraftReviewVisibility => NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    public string DraftReviewDetail => NeedsReview
        ? "SOURCE DRAFT HELD · production revision or connection changed. Compare confirmed gain, mute, EQ and bus routing; REBASE retains this draft locally, DISCARD restores confirmed values."
        : string.Empty;
    public ICommand RebaseDraftCommand { get; private set; } = null!;

    private void InitializeDraftReview()
    {
        RebaseDraftCommand = new AsyncRelayCommand(() =>
        {
            _draftOrigin = _owner.PresentedAudioDraftToken;
            MutationState = "CONFIRMED";
            DraftChanged();
            return Task.CompletedTask;
        }, () => NeedsReview && CanEditDraft && InputsValid && _owner.CanMutate() &&
            _owner.CurrentAudioDraftToken is { } current &&
            string.Equals(_owner.PresentedAudioDraftToken, current, StringComparison.Ordinal));
    }

    private void TrackDraftRevision()
    {
        if (!HasDraftChanges)
            _draftOrigin = null;
        else
            _draftOrigin ??= _owner.PresentedAudioDraftToken ?? "UNVERIFIED";
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
        (_owner.CurrentAudioDraftToken is { } current
            ? !string.Equals(_draftOrigin, current, StringComparison.Ordinal)
            : _draftOrigin != "UNVERIFIED");
    public Visibility DraftReviewVisibility => NeedsReview ? Visibility.Visible : Visibility.Collapsed;
    public string DraftReviewDetail => NeedsReview
        ? "BUS DRAFT HELD · production revision or connection changed. Compare confirmed bus gain, mute and dynamics; REBASE retains this draft locally, DISCARD restores confirmed values."
        : string.Empty;
    public ICommand RebaseDraftCommand { get; private set; } = null!;

    private void InitializeDraftReview()
    {
        RebaseDraftCommand = new AsyncRelayCommand(() =>
        {
            _draftOrigin = _owner.PresentedAudioDraftToken;
            MutationState = "CONFIRMED";
            DraftChanged();
            return Task.CompletedTask;
        }, () => NeedsReview && CanEditDraft && InputsValid && _owner.CanMutate() &&
            _owner.CurrentAudioDraftToken is { } current &&
            string.Equals(_owner.PresentedAudioDraftToken, current, StringComparison.Ordinal));
    }

    private void TrackDraftRevision()
    {
        if (!HasDraftChanges)
            _draftOrigin = null;
        else
            _draftOrigin ??= _owner.PresentedAudioDraftToken ?? "UNVERIFIED";
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
