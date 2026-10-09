// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed class OperatorAudioMixerViewModel : INotifyPropertyChanged
{
	private readonly OperatorViewModel _owner;
	private OperatorAudioSourceStripViewModel? _selectedSource;
	private string _availability = "UNAVAILABLE";
	private string _disabledReason = "Synchronize authoritative audio state.";
	private string _mutationState = "CONFIRMED";

	internal OperatorAudioMixerViewModel(OperatorViewModel owner)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		Sources = new ObservableCollection<OperatorAudioSourceStripViewModel>();
		Buses = new ObservableCollection<OperatorAudioBusStripViewModel>();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public OperatorViewModel Owner => _owner;
	public ObservableCollection<OperatorAudioSourceStripViewModel> Sources { get; }
	public ObservableCollection<OperatorAudioBusStripViewModel> Buses { get; }

	public OperatorAudioSourceStripViewModel? SelectedSource
	{
		get => _selectedSource;
		set
		{
			if (!Set(ref _selectedSource, value))
				return;
			if (value is not null)
			{
				var input = _owner.AudioInputs.FirstOrDefault(candidate =>
					string.Equals(candidate.SourceId, value.SourceId, StringComparison.Ordinal));
				if (input is not null)
					_owner.SelectedAudioInput = input;
			}
			RaiseCanExecuteChanged();
		}
	}

	public string Availability { get => _availability; private set => Set(ref _availability, value); }
	public string DisabledReason { get => _disabledReason; private set => Set(ref _disabledReason, value); }
	public string MutationState { get => _mutationState; private set => Set(ref _mutationState, value); }
	public bool CanEdit => _owner.CanEditAudioMixer();
	public string BoundsLabel => $"{AudioProductionLimits.MaximumSources} SOURCES · {AudioProductionLimits.MaximumBuses} BUSES MAX";
	public string MeteringLabel => "RUNTIME PEAK / REDUCTION · RMS NOT EXPOSED";

	internal void Apply(OperatorStatusSnapshot snapshot, bool refreshDrafts)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var previousSelectedId = SelectedSource?.SourceId;
		var production = snapshot.AudioProduction;
		if (production is null)
		{
			Sources.Clear();
			Buses.Clear();
			SelectedSource = null;
			Availability = "UNAVAILABLE";
			MutationState = "UNCONFIRMED";
			UpdateAvailability();
			return;
		}

		var busIds = production.Configuration.Buses.Select(bus => bus.BusId).ToArray();
		var inputs = snapshot.AudioInputs.ToDictionary(input => input.SourceId, StringComparer.Ordinal);
		var existingSources = Sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
		Sources.Clear();
		foreach (var source in production.Configuration.Sources.Take(AudioProductionLimits.MaximumSources))
		{
			var sourceId = source.SourceId.ToString();
			var descriptor = snapshot.Sources.FirstOrDefault(candidate =>
				string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
			inputs.TryGetValue(sourceId, out var input);
			if (!existingSources.TryGetValue(sourceId, out var strip))
			{
				strip = new OperatorAudioSourceStripViewModel(
					this,
					source,
					descriptor?.Name ?? sourceId,
					input,
					busIds);
			}
			else
			{
				strip.Apply(
					source,
					descriptor?.Name ?? sourceId,
					input,
					busIds,
					refreshDrafts);
			}
			Sources.Add(strip);
		}

		var evidence = (production.Buses ?? Array.Empty<OperatorAudioProductionBusDescriptor>())
			.ToDictionary(bus => bus.BusId, StringComparer.OrdinalIgnoreCase);
		var existingBuses = Buses.ToDictionary(bus => bus.BusId, StringComparer.OrdinalIgnoreCase);
		Buses.Clear();
		foreach (var bus in production.Configuration.Buses.Take(AudioProductionLimits.MaximumBuses))
		{
			evidence.TryGetValue(bus.BusId.Value, out var busEvidence);
			if (!existingBuses.TryGetValue(bus.BusId.Value, out var strip))
				strip = new OperatorAudioBusStripViewModel(this, bus, busEvidence, snapshot.OutputRoles);
			else
				strip.Apply(bus, busEvidence, snapshot.OutputRoles, refreshDrafts);
			Buses.Add(strip);
		}

		SelectedSource = Sources.FirstOrDefault(source =>
				string.Equals(source.SourceId, previousSelectedId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault(source =>
				string.Equals(source.SourceId, _owner.AudioRoutingSourceId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault();

		Availability = "CONFIRMED";
		if (refreshDrafts)
			MutationState = "CONFIRMED";
		UpdateAvailability();
		RaiseCanExecuteChanged();
	}

	internal void RaiseCanExecuteChanged()
	{
		UpdateAvailability();
		foreach (var source in Sources)
			source.RaiseCanExecuteChanged();
		foreach (var bus in Buses)
			bus.RaiseCanExecuteChanged();
		OnPropertyChanged(nameof(CanEdit));
		OnPropertyChanged(nameof(DisabledReason));
	}

	internal bool CanMutate() => CanEdit && _owner.Client?.Snapshot?.AudioProduction is not null;

	internal async Task<bool> ApplySourceAsync(OperatorAudioSourceStripViewModel strip)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var sourceId = new MediaSourceId(Identity.Parse(strip.SourceId));
		var confirmed = production.Configuration.Sources.First(source => source.SourceId == sourceId);
		var replacement = strip.BuildConfiguration(confirmed);
		var sources = production.Configuration.Sources
			.Select(source => source.SourceId == sourceId ? replacement : source)
			.ToArray();
		var next = Clone(production.Configuration, sources: sources);
		strip.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO SOURCE MIX",
			next,
			$"{strip.SourceName} mixer processing was confirmed by RuntimeHost.");
		strip.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	internal async Task<bool> ToggleSourceMuteAsync(OperatorAudioSourceStripViewModel strip)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var sourceId = new MediaSourceId(Identity.Parse(strip.SourceId));
		var sources = production.Configuration.Sources
			.Select(source => source.SourceId == sourceId
				? new AudioProductionSourceConfiguration(
					source.SourceId,
					source.Gain,
					!source.Muted,
					source.FollowRoutedSource,
					source.BusAssignments,
					source.Equalizer)
				: source)
			.ToArray();
		strip.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO SOURCE MUTE",
			Clone(production.Configuration, sources: sources),
			$"{strip.SourceName} mixer mute state was confirmed by RuntimeHost.");
		strip.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	internal async Task<bool> ToggleContributionAsync(OperatorAudioSourceStripViewModel strip)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var sourceId = new MediaSourceId(Identity.Parse(strip.SourceId));
		var sources = production.Configuration.Sources
			.Select(source => source.SourceId == sourceId
				? new AudioProductionSourceConfiguration(
					source.SourceId,
					source.Gain,
					source.Muted,
					!source.FollowRoutedSource,
					source.BusAssignments,
					source.Equalizer)
				: source)
			.ToArray();
		strip.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO SOURCE MODE",
			Clone(production.Configuration, sources: sources),
			$"{strip.SourceName} contribution mode was confirmed by RuntimeHost.");
		strip.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	internal async Task<bool> ToggleAssignmentAsync(
		OperatorAudioSourceStripViewModel strip,
		OperatorAudioBusAssignmentViewModel assignment)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var sourceId = new MediaSourceId(Identity.Parse(strip.SourceId));
		var busId = new AudioBusId(assignment.BusId);
		var confirmed = production.Configuration.Sources.First(source => source.SourceId == sourceId);
		var buses = confirmed.BusAssignments.ToHashSet();
		if (!buses.Add(busId))
			buses.Remove(busId);
		var orderedAssignments = production.Configuration.Buses
			.Select(bus => bus.BusId)
			.Where(buses.Contains)
			.ToArray();
		var replacement = new AudioProductionSourceConfiguration(
			confirmed.SourceId,
			confirmed.Gain,
			confirmed.Muted,
			confirmed.FollowRoutedSource,
			orderedAssignments,
			confirmed.Equalizer);
		var sources = production.Configuration.Sources
			.Select(source => source.SourceId == sourceId ? replacement : source)
			.ToArray();

		assignment.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO BUS ASSIGNMENT",
			Clone(production.Configuration, sources: sources),
			$"{strip.SourceName} → {assignment.BusLabel} routing was confirmed by RuntimeHost.");
		assignment.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	internal async Task<bool> ApplyBusAsync(OperatorAudioBusStripViewModel strip)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var busId = new AudioBusId(strip.BusId);
		var confirmed = production.Configuration.Buses.First(bus => bus.BusId == busId);
		var replacement = strip.BuildConfiguration(confirmed);
		var buses = production.Configuration.Buses
			.Select(bus => bus.BusId == busId ? replacement : bus)
			.ToArray();
		strip.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO BUS PROCESSING",
			Clone(production.Configuration, buses: buses),
			$"{strip.BusLabel} bus processing was confirmed by RuntimeHost.");
		strip.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	internal async Task<bool> ToggleBusMuteAsync(OperatorAudioBusStripViewModel strip)
	{
		var production = _owner.Client?.Snapshot?.AudioProduction;
		if (production is null || !CanMutate())
			return false;

		var busId = new AudioBusId(strip.BusId);
		var buses = production.Configuration.Buses
			.Select(bus => bus.BusId == busId
				? new AudioProductionBusConfiguration(bus.BusId, bus.MasterGain, !bus.Muted, bus.Dynamics)
				: bus)
			.ToArray();
		strip.BeginMutation();
		MutationState = "PENDING";
		var success = await _owner.ApplyAudioMixerConfigurationAsync(
			"AUDIO BUS MUTE",
			Clone(production.Configuration, buses: buses),
			$"{strip.BusLabel} bus mute state was confirmed by RuntimeHost.");
		strip.CompleteMutation(success);
		MutationState = success ? "CONFIRMED" : "REJECTED";
		return success;
	}

	private void UpdateAvailability()
	{
		DisabledReason = _owner.IsStale
			? "Authoritative audio state is stale. Controls remain disabled until synchronization recovers."
			: !_owner.IsConnected
				? "ControlHost is disconnected. Mixer evidence is read-only until a confirmed snapshot returns."
				: !string.Equals(_owner.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase)
					? $"Runtime is {_owner.RuntimeStatus}. Mixer mutations require READY state."
					: _owner.IsBusy
						? "A governed mutation is awaiting authoritative confirmation."
						: _owner.Client?.Snapshot?.AudioProduction is null
							? "Advanced audio production state is unavailable."
							: "Confirmed Runtime state. Edits remain drafts until applied and acknowledged.";
	}

	private static AudioProductionConfiguration Clone(
		AudioProductionConfiguration current,
		IReadOnlyList<AudioProductionBusConfiguration>? buses = null,
		IReadOnlyList<AudioProductionSourceConfiguration>? sources = null) =>
		new(
			checked(current.Revision + 1),
			buses ?? current.Buses,
			sources ?? current.Sources,
			current.Crossfade,
			current.Ducking,
			current.ClipStrategy);

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(name);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OperatorAudioSourceStripViewModel : INotifyPropertyChanged
{
	private readonly OperatorAudioMixerViewModel _owner;
	private AudioProductionSourceConfiguration _confirmed;
	private string _sourceName;
	private string _health = "UNKNOWN";
	private double _leftPeak;
	private double _rightPeak;
	private bool _inputClipping;
	private double _gain;
	private bool _muted;
	private bool _followRoutedSource;
	private bool _lowEnabled;
	private double _lowFrequencyHz;
	private double _lowGainDb;
	private bool _midEnabled;
	private double _midFrequencyHz;
	private double _midGainDb;
	private double _midQ;
	private bool _highEnabled;
	private double _highFrequencyHz;
	private double _highGainDb;
	private string _mutationState = "CONFIRMED";

	internal OperatorAudioSourceStripViewModel(
		OperatorAudioMixerViewModel owner,
		AudioProductionSourceConfiguration source,
		string sourceName,
		OperatorAudioInputDescriptor? input,
		IReadOnlyList<AudioBusId> busIds)
	{
		_owner = owner;
		_confirmed = source;
		_sourceName = sourceName;
		Assignments = new ObservableCollection<OperatorAudioBusAssignmentViewModel>();
		Apply(source, sourceName, input, busIds, refreshDrafts: true);
		ApplyCommand = new AsyncRelayCommand(() => _owner.ApplySourceAsync(this), CanApply);
		ToggleMuteCommand = new AsyncRelayCommand(() => _owner.ToggleSourceMuteAsync(this), _owner.CanMutate);
		ToggleContributionCommand = new AsyncRelayCommand(() => _owner.ToggleContributionAsync(this), _owner.CanMutate);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string SourceId => _confirmed.SourceId.ToString();
	public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }
	public string Health { get => _health; private set => Set(ref _health, value); }
	public string RuntimeState => Health is "UNDERRUN" or "ERROR" ? "MISSING / UNDERRUN" : InputClipping ? "INPUT OVERLOAD" : Health;
	public double LeftPeak { get => _leftPeak; private set => Set(ref _leftPeak, value); }
	public double RightPeak { get => _rightPeak; private set => Set(ref _rightPeak, value); }
	public bool InputClipping { get => _inputClipping; private set { if (Set(ref _inputClipping, value)) OnPropertyChanged(nameof(RuntimeState)); } }
	public ObservableCollection<OperatorAudioBusAssignmentViewModel> Assignments { get; }
	public ICommand ApplyCommand { get; }
	public ICommand ToggleMuteCommand { get; }
	public ICommand ToggleContributionCommand { get; }

	public double Gain { get => _gain; set { if (Set(ref _gain, Clamp(value, 0, 4, _gain))) DraftChanged(); } }
	public bool Muted { get => _muted; set { if (Set(ref _muted, value)) DraftChanged(); } }
	public bool FollowRoutedSource { get => _followRoutedSource; set { if (Set(ref _followRoutedSource, value)) DraftChanged(); } }
	public string ContributionLabel => FollowRoutedSource ? "ROUTED" : "ALWAYS IN MIX";
	public string MuteActionLabel => _confirmed.Muted ? "MUTE OFF" : "MUTE";
	public string BusSummary => _confirmed.BusAssignments.Count == 0
		? "NO BUS"
		: string.Join(" · ", _confirmed.BusAssignments.Select(bus => bus.Value.ToUpperInvariant()));

	public bool LowEnabled { get => _lowEnabled; set { if (Set(ref _lowEnabled, value)) DraftChanged(); } }
	public double LowFrequencyHz { get => _lowFrequencyHz; set { if (Set(ref _lowFrequencyHz, Clamp(value, AudioEqualizerLimits.MinimumFrequencyHz, AudioEqualizerLimits.MaximumFrequencyHz, _lowFrequencyHz))) DraftChanged(); } }
	public double LowGainDb { get => _lowGainDb; set { if (Set(ref _lowGainDb, Clamp(value, AudioEqualizerLimits.MinimumGainDb, AudioEqualizerLimits.MaximumGainDb, _lowGainDb))) DraftChanged(); } }
	public bool MidEnabled { get => _midEnabled; set { if (Set(ref _midEnabled, value)) DraftChanged(); } }
	public double MidFrequencyHz { get => _midFrequencyHz; set { if (Set(ref _midFrequencyHz, Clamp(value, AudioEqualizerLimits.MinimumFrequencyHz, AudioEqualizerLimits.MaximumFrequencyHz, _midFrequencyHz))) DraftChanged(); } }
	public double MidGainDb { get => _midGainDb; set { if (Set(ref _midGainDb, Clamp(value, AudioEqualizerLimits.MinimumGainDb, AudioEqualizerLimits.MaximumGainDb, _midGainDb))) DraftChanged(); } }
	public double MidQ { get => _midQ; set { if (Set(ref _midQ, Clamp(value, AudioEqualizerLimits.MinimumBellQ, AudioEqualizerLimits.MaximumBellQ, _midQ))) DraftChanged(); } }
	public bool HighEnabled { get => _highEnabled; set { if (Set(ref _highEnabled, value)) DraftChanged(); } }
	public double HighFrequencyHz { get => _highFrequencyHz; set { if (Set(ref _highFrequencyHz, Clamp(value, AudioEqualizerLimits.MinimumFrequencyHz, AudioEqualizerLimits.MaximumFrequencyHz, _highFrequencyHz))) DraftChanged(); } }
	public double HighGainDb { get => _highGainDb; set { if (Set(ref _highGainDb, Clamp(value, AudioEqualizerLimits.MinimumGainDb, AudioEqualizerLimits.MaximumGainDb, _highGainDb))) DraftChanged(); } }

	public string MutationState { get => _mutationState; private set => Set(ref _mutationState, value); }
	public bool HasDraftChanges => !DraftMatches(_confirmed);
	public string DraftState => MutationState == "PENDING" ? "PENDING" : HasDraftChanges ? "DRAFT" : MutationState;

	internal void Apply(
		AudioProductionSourceConfiguration source,
		string sourceName,
		OperatorAudioInputDescriptor? input,
		IReadOnlyList<AudioBusId> busIds,
		bool refreshDrafts)
	{
		var preserveDraft = !refreshDrafts && HasDraftChanges;
		_confirmed = source;
		OnPropertyChanged(nameof(SourceId));
		SourceName = sourceName;
		Health = input?.Health ?? "UNAVAILABLE";
		LeftPeak = input?.LeftPeak ?? 0;
		RightPeak = input?.RightPeak ?? 0;
		InputClipping = input?.Clipping == true;
		if (!preserveDraft)
			LoadDraft(source);
		UpdateAssignments(busIds, source.BusAssignments);
		if (refreshDrafts)
			MutationState = "CONFIRMED";
		OnPropertyChanged(nameof(BusSummary));
		OnPropertyChanged(nameof(MuteActionLabel));
		OnPropertyChanged(nameof(RuntimeState));
		DraftChanged();
	}

	internal AudioProductionSourceConfiguration BuildConfiguration(AudioProductionSourceConfiguration confirmed) =>
		new(
			confirmed.SourceId,
			Gain,
			Muted,
			FollowRoutedSource,
			confirmed.BusAssignments,
			new AudioSourceEqualizerConfiguration(
				new AudioLowShelfEqualizerBand(LowEnabled, LowFrequencyHz, LowGainDb),
				new AudioBellEqualizerBand(MidEnabled, MidFrequencyHz, MidGainDb, MidQ),
				new AudioHighShelfEqualizerBand(HighEnabled, HighFrequencyHz, HighGainDb)));

	internal void BeginMutation()
	{
		MutationState = "PENDING";
		RaiseCanExecuteChanged();
	}

	internal void CompleteMutation(bool success)
	{
		MutationState = success ? "CONFIRMED" : "REJECTED";
		RaiseCanExecuteChanged();
	}

	internal void RaiseCanExecuteChanged()
	{
		(ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleContributionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		foreach (var assignment in Assignments)
			assignment.RaiseCanExecuteChanged();
		OnPropertyChanged(nameof(DraftState));
		OnPropertyChanged(nameof(HasDraftChanges));
	}

	private bool CanApply() => _owner.CanMutate() && HasDraftChanges && MutationState != "PENDING";

	private void LoadDraft(AudioProductionSourceConfiguration source)
	{
		var equalizer = source.Equalizer;
		_gain = source.Gain;
		_muted = source.Muted;
		_followRoutedSource = source.FollowRoutedSource;
		_lowEnabled = equalizer?.LowShelf.Enabled ?? false;
		_lowFrequencyHz = equalizer?.LowShelf.FrequencyHz ?? 120d;
		_lowGainDb = equalizer?.LowShelf.GainDb ?? 0d;
		_midEnabled = equalizer?.Mid.Enabled ?? false;
		_midFrequencyHz = equalizer?.Mid.FrequencyHz ?? 1_000d;
		_midGainDb = equalizer?.Mid.GainDb ?? 0d;
		_midQ = equalizer?.Mid.Q ?? 1d;
		_highEnabled = equalizer?.HighShelf.Enabled ?? false;
		_highFrequencyHz = equalizer?.HighShelf.FrequencyHz ?? 8_000d;
		_highGainDb = equalizer?.HighShelf.GainDb ?? 0d;
		foreach (var property in new[]
		{
			nameof(Gain), nameof(Muted), nameof(FollowRoutedSource), nameof(ContributionLabel),
			nameof(LowEnabled), nameof(LowFrequencyHz), nameof(LowGainDb),
			nameof(MidEnabled), nameof(MidFrequencyHz), nameof(MidGainDb), nameof(MidQ),
			nameof(HighEnabled), nameof(HighFrequencyHz), nameof(HighGainDb)
		})
			OnPropertyChanged(property);
	}

	private bool DraftMatches(AudioProductionSourceConfiguration source)
	{
		var eq = source.Equalizer;
		return Gain.Equals(source.Gain) &&
			Muted == source.Muted &&
			FollowRoutedSource == source.FollowRoutedSource &&
			LowEnabled == (eq?.LowShelf.Enabled ?? false) &&
			LowFrequencyHz.Equals(eq?.LowShelf.FrequencyHz ?? 120d) &&
			LowGainDb.Equals(eq?.LowShelf.GainDb ?? 0d) &&
			MidEnabled == (eq?.Mid.Enabled ?? false) &&
			MidFrequencyHz.Equals(eq?.Mid.FrequencyHz ?? 1_000d) &&
			MidGainDb.Equals(eq?.Mid.GainDb ?? 0d) &&
			MidQ.Equals(eq?.Mid.Q ?? 1d) &&
			HighEnabled == (eq?.HighShelf.Enabled ?? false) &&
			HighFrequencyHz.Equals(eq?.HighShelf.FrequencyHz ?? 8_000d) &&
			HighGainDb.Equals(eq?.HighShelf.GainDb ?? 0d);
	}

	private void UpdateAssignments(IReadOnlyList<AudioBusId> busIds, IReadOnlyList<AudioBusId> confirmedAssignments)
	{
		var existing = Assignments.ToDictionary(item => item.BusId, StringComparer.OrdinalIgnoreCase);
		Assignments.Clear();
		foreach (var busId in busIds.Take(AudioProductionLimits.MaximumBuses))
		{
			if (!existing.TryGetValue(busId.Value, out var assignment))
				assignment = new OperatorAudioBusAssignmentViewModel(_owner, this, busId);
			assignment.Apply(confirmedAssignments.Contains(busId));
			Assignments.Add(assignment);
		}
	}

	private void DraftChanged()
	{
		OnPropertyChanged(nameof(HasDraftChanges));
		OnPropertyChanged(nameof(DraftState));
		OnPropertyChanged(nameof(ContributionLabel));
		RaiseCanExecuteChanged();
	}

	private static double Clamp(double value, double min, double max, double fallback) =>
		double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(name);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OperatorAudioBusAssignmentViewModel : INotifyPropertyChanged
{
	private readonly OperatorAudioMixerViewModel _owner;
	private readonly OperatorAudioSourceStripViewModel _source;
	private bool _isAssigned;
	private string _state = "CONFIRMED";

	internal OperatorAudioBusAssignmentViewModel(
		OperatorAudioMixerViewModel owner,
		OperatorAudioSourceStripViewModel source,
		AudioBusId busId)
	{
		_owner = owner;
		_source = source;
		BusId = busId.Value;
		ToggleCommand = new AsyncRelayCommand(() => _owner.ToggleAssignmentAsync(_source, this), CanToggle);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public string BusId { get; }
	public string BusLabel => BusId.ToUpperInvariant();
	public bool IsAssigned { get => _isAssigned; private set { if (Set(ref _isAssigned, value)) OnPropertyChanged(nameof(ActionLabel)); } }
	public string State { get => _state; private set => Set(ref _state, value); }
	public string ActionLabel => IsAssigned ? "ON" : "OFF";
	public ICommand ToggleCommand { get; }

	internal void Apply(bool assigned)
	{
		IsAssigned = assigned;
		if (State != "PENDING")
			State = "CONFIRMED";
	}

	internal void BeginMutation()
	{
		State = "PENDING";
		RaiseCanExecuteChanged();
	}

	internal void CompleteMutation(bool success)
	{
		State = success ? "CONFIRMED" : "REJECTED";
		RaiseCanExecuteChanged();
	}

	internal void RaiseCanExecuteChanged() =>
		(ToggleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

	private bool CanToggle() => _owner.CanMutate() && State != "PENDING";

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(name);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class OperatorAudioBusStripViewModel : INotifyPropertyChanged
{
	private readonly OperatorAudioMixerViewModel _owner;
	private AudioProductionBusConfiguration _confirmed;
	private double _leftPeak;
	private double _rightPeak;
	private bool _clipping;
	private ulong _clippedValues;
	private double _compressorReductionDb;
	private double _limiterReductionDb;
	private ulong _limiterHitCount;
	private int _activeSources;
	private int _missingSources;
	private string _outputRoles = "UNASSIGNED";
	private double _masterGain;
	private bool _muted;
	private bool _compressorEnabled;
	private double _compressorThresholdDbFs;
	private double _compressorRatio;
	private double _compressorAttackMilliseconds;
	private double _compressorReleaseMilliseconds;
	private double _compressorMakeupGainDb;
	private bool _limiterEnabled;
	private double _limiterCeilingDbFs;
	private double _limiterReleaseMilliseconds;
	private string _mutationState = "CONFIRMED";

	internal OperatorAudioBusStripViewModel(
		OperatorAudioMixerViewModel owner,
		AudioProductionBusConfiguration bus,
		OperatorAudioProductionBusDescriptor? evidence,
		IReadOnlyList<OperatorOutputRoleDescriptor> outputRoles)
	{
		_owner = owner;
		_confirmed = bus;
		Apply(bus, evidence, outputRoles, refreshDrafts: true);
		ApplyCommand = new AsyncRelayCommand(() => _owner.ApplyBusAsync(this), CanApply);
		ToggleMuteCommand = new AsyncRelayCommand(() => _owner.ToggleBusMuteAsync(this), _owner.CanMutate);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public string BusId => _confirmed.BusId.Value;
	public string BusLabel => BusId.ToUpperInvariant();
	public double LeftPeak { get => _leftPeak; private set => Set(ref _leftPeak, value); }
	public double RightPeak { get => _rightPeak; private set => Set(ref _rightPeak, value); }
	public bool Clipping { get => _clipping; private set => Set(ref _clipping, value); }
	public ulong ClippedValues { get => _clippedValues; private set => Set(ref _clippedValues, value); }
	public double CompressorReductionDb { get => _compressorReductionDb; private set => Set(ref _compressorReductionDb, value); }
	public double LimiterReductionDb { get => _limiterReductionDb; private set => Set(ref _limiterReductionDb, value); }
	public ulong LimiterHitCount { get => _limiterHitCount; private set => Set(ref _limiterHitCount, value); }
	public int ActiveSources { get => _activeSources; private set => Set(ref _activeSources, value); }
	public int MissingSources { get => _missingSources; private set => Set(ref _missingSources, value); }
	public string OutputRoles { get => _outputRoles; private set => Set(ref _outputRoles, value); }
	public string EvidenceState => Clipping ? "CLIPPING" : MissingSources > 0 ? "MISSING SOURCE" : "CONFIRMED";
	public string LimiterActivity => LimiterHitCount > 0 ? $"{LimiterHitCount} HIT" : LimiterReductionDb > 0 ? $"{LimiterReductionDb:0.0} dB" : "IDLE";
	public string CompressorActivity => CompressorReductionDb > 0 ? $"{CompressorReductionDb:0.0} dB GR" : "IDLE";
	public ICommand ApplyCommand { get; }
	public ICommand ToggleMuteCommand { get; }

	public double MasterGain { get => _masterGain; set { if (Set(ref _masterGain, Clamp(value, 0, 4, _masterGain))) DraftChanged(); } }
	public bool Muted { get => _muted; set { if (Set(ref _muted, value)) DraftChanged(); } }
	public bool CompressorEnabled { get => _compressorEnabled; set { if (Set(ref _compressorEnabled, value)) DraftChanged(); } }
	public double CompressorThresholdDbFs { get => _compressorThresholdDbFs; set { if (Set(ref _compressorThresholdDbFs, Clamp(value, AudioDynamicsLimits.MinimumCompressorThresholdDbFs, AudioDynamicsLimits.MaximumCompressorThresholdDbFs, _compressorThresholdDbFs))) DraftChanged(); } }
	public double CompressorRatio { get => _compressorRatio; set { if (Set(ref _compressorRatio, Clamp(value, AudioDynamicsLimits.MinimumCompressorRatio, AudioDynamicsLimits.MaximumCompressorRatio, _compressorRatio))) DraftChanged(); } }
	public double CompressorAttackMilliseconds { get => _compressorAttackMilliseconds; set { if (Set(ref _compressorAttackMilliseconds, Clamp(value, AudioDynamicsLimits.MinimumAttackMilliseconds, AudioDynamicsLimits.MaximumAttackMilliseconds, _compressorAttackMilliseconds))) DraftChanged(); } }
	public double CompressorReleaseMilliseconds { get => _compressorReleaseMilliseconds; set { if (Set(ref _compressorReleaseMilliseconds, Clamp(value, AudioDynamicsLimits.MinimumReleaseMilliseconds, AudioDynamicsLimits.MaximumReleaseMilliseconds, _compressorReleaseMilliseconds))) DraftChanged(); } }
	public double CompressorMakeupGainDb { get => _compressorMakeupGainDb; set { if (Set(ref _compressorMakeupGainDb, Clamp(value, AudioDynamicsLimits.MinimumMakeupGainDb, AudioDynamicsLimits.MaximumMakeupGainDb, _compressorMakeupGainDb))) DraftChanged(); } }
	public bool LimiterEnabled { get => _limiterEnabled; set { if (Set(ref _limiterEnabled, value)) DraftChanged(); } }
	public double LimiterCeilingDbFs { get => _limiterCeilingDbFs; set { if (Set(ref _limiterCeilingDbFs, Clamp(value, AudioDynamicsLimits.MinimumLimiterCeilingDbFs, AudioDynamicsLimits.MaximumLimiterCeilingDbFs, _limiterCeilingDbFs))) DraftChanged(); } }
	public double LimiterReleaseMilliseconds { get => _limiterReleaseMilliseconds; set { if (Set(ref _limiterReleaseMilliseconds, Clamp(value, AudioDynamicsLimits.MinimumReleaseMilliseconds, AudioDynamicsLimits.MaximumReleaseMilliseconds, _limiterReleaseMilliseconds))) DraftChanged(); } }

	public string MutationState { get => _mutationState; private set => Set(ref _mutationState, value); }
	public bool HasDraftChanges => !DraftMatches(_confirmed);
	public string DraftState => MutationState == "PENDING" ? "PENDING" : HasDraftChanges ? "DRAFT" : MutationState;
	public string MuteActionLabel => _confirmed.Muted ? "MUTE OFF" : "MUTE";

	internal void Apply(
		AudioProductionBusConfiguration bus,
		OperatorAudioProductionBusDescriptor? evidence,
		IReadOnlyList<OperatorOutputRoleDescriptor> outputRoles,
		bool refreshDrafts)
	{
		var preserveDraft = !refreshDrafts && HasDraftChanges;
		_confirmed = bus;
		OnPropertyChanged(nameof(BusId));
		OnPropertyChanged(nameof(BusLabel));
		LeftPeak = evidence?.LeftPeak ?? 0;
		RightPeak = evidence?.RightPeak ?? 0;
		Clipping = evidence?.Clipping == true;
		ClippedValues = evidence?.ClippedSampleValues ?? 0;
		CompressorReductionDb = evidence?.CompressorGainReductionDb ?? 0;
		LimiterReductionDb = evidence?.LimiterGainReductionDb ?? 0;
		LimiterHitCount = evidence?.LimiterHitCount ?? 0;
		ActiveSources = evidence?.ActiveSourceCount ?? 0;
		MissingSources = evidence?.MissingSourceCount ?? 0;
		var roles = outputRoles
			.Where(role => string.Equals(role.AudioBusId, bus.BusId.Value, StringComparison.OrdinalIgnoreCase))
			.Select(role => role.RoleId.ToUpperInvariant())
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		OutputRoles = roles.Length == 0 ? "UNASSIGNED" : string.Join(" · ", roles);
		if (!preserveDraft)
			LoadDraft(bus);
		if (refreshDrafts)
			MutationState = "CONFIRMED";
		foreach (var property in new[] { nameof(EvidenceState), nameof(LimiterActivity), nameof(CompressorActivity), nameof(MuteActionLabel) })
			OnPropertyChanged(property);
		DraftChanged();
	}

	internal AudioProductionBusConfiguration BuildConfiguration(AudioProductionBusConfiguration confirmed) =>
		new(
			confirmed.BusId,
			MasterGain,
			Muted,
			new AudioBusDynamicsConfiguration(
				new AudioBusCompressorConfiguration(
					CompressorEnabled,
					CompressorThresholdDbFs,
					CompressorRatio,
					CompressorAttackMilliseconds,
					CompressorReleaseMilliseconds,
					CompressorMakeupGainDb),
				new AudioBusSamplePeakLimiterConfiguration(
					LimiterEnabled,
					LimiterCeilingDbFs,
					LimiterReleaseMilliseconds)));

	internal void BeginMutation()
	{
		MutationState = "PENDING";
		RaiseCanExecuteChanged();
	}

	internal void CompleteMutation(bool success)
	{
		MutationState = success ? "CONFIRMED" : "REJECTED";
		RaiseCanExecuteChanged();
	}

	internal void RaiseCanExecuteChanged()
	{
		(ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		OnPropertyChanged(nameof(HasDraftChanges));
		OnPropertyChanged(nameof(DraftState));
	}

	private bool CanApply() => _owner.CanMutate() && HasDraftChanges && MutationState != "PENDING";

	private void LoadDraft(AudioProductionBusConfiguration bus)
	{
		var compressor = bus.Dynamics?.Compressor;
		var limiter = bus.Dynamics?.Limiter;
		_masterGain = bus.MasterGain;
		_muted = bus.Muted;
		_compressorEnabled = compressor?.Enabled ?? false;
		_compressorThresholdDbFs = compressor?.ThresholdDbFs ?? -18d;
		_compressorRatio = compressor?.Ratio ?? 4d;
		_compressorAttackMilliseconds = compressor?.AttackMilliseconds ?? 5d;
		_compressorReleaseMilliseconds = compressor?.ReleaseMilliseconds ?? 100d;
		_compressorMakeupGainDb = compressor?.MakeupGainDb ?? 0d;
		_limiterEnabled = limiter?.Enabled ?? false;
		_limiterCeilingDbFs = limiter?.CeilingDbFs ?? -1d;
		_limiterReleaseMilliseconds = limiter?.ReleaseMilliseconds ?? 100d;
		foreach (var property in new[]
		{
			nameof(MasterGain), nameof(Muted), nameof(CompressorEnabled), nameof(CompressorThresholdDbFs),
			nameof(CompressorRatio), nameof(CompressorAttackMilliseconds), nameof(CompressorReleaseMilliseconds),
			nameof(CompressorMakeupGainDb), nameof(LimiterEnabled), nameof(LimiterCeilingDbFs),
			nameof(LimiterReleaseMilliseconds)
		})
			OnPropertyChanged(property);
	}

	private bool DraftMatches(AudioProductionBusConfiguration bus)
	{
		var compressor = bus.Dynamics?.Compressor;
		var limiter = bus.Dynamics?.Limiter;
		return MasterGain.Equals(bus.MasterGain) &&
			Muted == bus.Muted &&
			CompressorEnabled == (compressor?.Enabled ?? false) &&
			CompressorThresholdDbFs.Equals(compressor?.ThresholdDbFs ?? -18d) &&
			CompressorRatio.Equals(compressor?.Ratio ?? 4d) &&
			CompressorAttackMilliseconds.Equals(compressor?.AttackMilliseconds ?? 5d) &&
			CompressorReleaseMilliseconds.Equals(compressor?.ReleaseMilliseconds ?? 100d) &&
			CompressorMakeupGainDb.Equals(compressor?.MakeupGainDb ?? 0d) &&
			LimiterEnabled == (limiter?.Enabled ?? false) &&
			LimiterCeilingDbFs.Equals(limiter?.CeilingDbFs ?? -1d) &&
			LimiterReleaseMilliseconds.Equals(limiter?.ReleaseMilliseconds ?? 100d);
	}

	private void DraftChanged()
	{
		OnPropertyChanged(nameof(HasDraftChanges));
		OnPropertyChanged(nameof(DraftState));
		RaiseCanExecuteChanged();
	}

	private static double Clamp(double value, double min, double max, double fallback) =>
		double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(name);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
