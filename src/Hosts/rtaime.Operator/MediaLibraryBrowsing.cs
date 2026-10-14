// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public sealed record MediaDurationChoice(MediaDurationFilter Value, string Label);
public sealed record MediaReadinessChoice(MediaReadinessFilter Value, string Label);

public sealed partial class MediaPoolInspectorViewModel
{
	private readonly MediaLibraryHoverPreview _hoverPreview = new();
	private MediaSmartBinStore? _binStore;
	private MediaSmartBin? _selectedSmartBin;
	private MediaDurationFilter _durationFilter;
	private MediaReadinessFilter _readinessFilter;
	private string _binStatus = "Smart Bins save filters only.";
	private string _hoverStatus = "Hover or focus an asset · scrub capability unavailable";
	private ImageSource? _hoverThumbnail;
	private string? _hoverKey;
	private bool _browsingDisposed;
	private bool _loadingBins;
	private bool _binsLoaded;
	private bool _restoringBin;
	private Task _binSave = Task.CompletedTask;
	public ObservableCollection<MediaSmartBin> SmartBins { get; } = [];
	public IReadOnlyList<MediaDurationChoice> DurationChoices { get; } =
		[new(MediaDurationFilter.Any, "Any duration"), new(MediaDurationFilter.UnderOneMinute, "Under 1 minute"),
		 new(MediaDurationFilter.OneToTenMinutes, "1–10 minutes"), new(MediaDurationFilter.TenMinutesOrLonger, "10+ minutes"),
		 new(MediaDurationFilter.Unknown, "Unknown duration")];
	public IReadOnlyList<MediaReadinessChoice> ReadinessChoices { get; } =
		[new(MediaReadinessFilter.Any, "Any readiness"), new(MediaReadinessFilter.Ready, "Ready"),
		 new(MediaReadinessFilter.NotReady, "Not ready")];
	public ICommand SaveSmartBinCommand { get; private set; } = null!;
	public ICommand DeleteSmartBinCommand { get; private set; } = null!;
	public ICommand ClearLibrarySelectionCommand { get; private set; } = null!;
	public string BinStatus { get => _binStatus; private set => Set(ref _binStatus, value); }
	public string HoverStatus { get => _hoverStatus; private set => Set(ref _hoverStatus, value); }
	public ImageSource? HoverThumbnail { get => _hoverThumbnail; private set => Set(ref _hoverThumbnail, value); }

	private void InitializeBrowsing()
	{
		SaveSmartBinCommand = new AsyncRelayCommand(SaveSmartBinAsync, () => !_loadingBins && !_browsingDisposed);
		DeleteSmartBinCommand = new AsyncRelayCommand(DeleteSmartBinAsync, () => SelectedSmartBin is not null && !_loadingBins && !_browsingDisposed);
		ClearLibrarySelectionCommand = new AsyncRelayCommand(() => { UpdateSelection([]); return Task.CompletedTask; });
	}

	public MediaDurationFilter DurationFilter
	{
		get => _durationFilter;
		set { if (Enum.IsDefined(value) && Set(ref _durationFilter, value)) ApplyFilter(); }
	}
	public MediaReadinessFilter ReadinessFilter
	{
		get => _readinessFilter;
		set { if (Enum.IsDefined(value) && Set(ref _readinessFilter, value)) ApplyFilter(); }
	}
	public MediaSmartBin? SelectedSmartBin
	{
		get => _selectedSmartBin;
		set
		{
			if (!Set(ref _selectedSmartBin, value) || value is null || !value.IsValid) return;
			_restoringBin = true;
			try
			{
				_searchText = value.Search;
				_selectedCategory = value.Category;
				_selectedFilter = value.Filter;
				_durationFilter = value.Duration;
				_readinessFilter = value.Readiness;
				OnPropertyChanged(nameof(SearchText)); OnPropertyChanged(nameof(SelectedCategory));
				OnPropertyChanged(nameof(SelectedFilter)); OnPropertyChanged(nameof(DurationFilter)); OnPropertyChanged(nameof(ReadinessFilter));
			}
			finally { _restoringBin = false; }
			ApplyFilter();
			BinStatus = "Loaded " + value.Name;
		}
	}
	private MediaSmartBin CurrentBin(string name = "Current") =>
		new(name, SearchText, SelectedCategory, SelectedFilter, DurationFilter, ReadinessFilter);

	public async Task LoadSmartBinsAsync(MediaSmartBinStore? store = null)
	{
		if (_binsLoaded || _loadingBins || _browsingDisposed) return;
		_loadingBins = true;
		_binStore = store ?? new MediaSmartBinStore();
		try
		{
			var bins = await _binStore.LoadAsync();
			if (_browsingDisposed) return;
			SmartBins.Clear();
			foreach (var bin in bins) SmartBins.Add(bin);
			_binsLoaded = true;
		}
		finally { _loadingBins = false; }
	}

	private async Task SaveSmartBinAsync()
	{
		if (!_binsLoaded || _binStore is null) { BinStatus = "Smart Bin preferences are not loaded."; return; }
		var current = CurrentBin();
		var existing = SmartBins.FirstOrDefault(bin => bin with { Name = current.Name } == current);
		if (existing is not null) { SelectedSmartBin = existing; BinStatus = "This filter is already saved."; return; }
		if (SmartBins.Count >= MediaSmartBin.MaximumBins) { BinStatus = "16 Smart Bins maximum; remove one first."; return; }
		var name = $"{SelectedCategory} / {SelectedFilter} / {DurationFilter} / {ReadinessFilter}" +
			(string.IsNullOrWhiteSpace(SearchText) ? "" : " / " + SearchText.Trim());
		if (name.Length > 112) name = name[..112];
		var unique = name;
		for (var index = 2; SmartBins.Any(bin => bin.Name == unique); index++) unique = name + " (" + index + ")";
		var saved = current with { Name = unique };
		SmartBins.Add(saved); SelectedSmartBin = saved;
		await PersistBinsAsync();
	}
	private async Task DeleteSmartBinAsync()
	{
		if (SelectedSmartBin is not { } bin) return;
		SelectedSmartBin = null;
		SmartBins.Remove(bin);
		await PersistBinsAsync();
	}
	private Task PersistBinsAsync()
	{
		var snapshot = SmartBins.ToArray();
		_binSave = SaveBinsAfterAsync(_binSave, snapshot);
		return _binSave;
	}
	private async Task SaveBinsAfterAsync(Task previous, MediaSmartBin[] snapshot)
	{
		await previous;
		try
		{
			if (_binStore is not null) await _binStore.SaveAsync(snapshot);
			BinStatus = "Saved locally · no production state changed.";
			HoverStatus = BinStatus;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{ BinStatus = "Preferences were not saved: " + ex.Message; HoverStatus = BinStatus; }
	}
	public Task FlushSmartBinsAsync() => _binSave;
	public async Task BeginHoverAsync(MediaPoolItemViewModel item)
	{
		if (_browsingDisposed || _hoverKey == item.Key) return;
		CancelHover();
		_hoverKey = item.Key;
		var preview = await _hoverPreview.RequestAsync(item);
		if (_browsingDisposed || _hoverKey != item.Key || preview is null) return;
		HoverThumbnail = preview.Thumbnail; HoverStatus = preview.Status;
	}
	public void CancelHover()
	{
		_hoverPreview.Cancel(); _hoverKey = null; HoverThumbnail = null;
		HoverStatus = "Hover or focus an asset · scrub capability unavailable";
	}
	private void DisposeBrowsing()
	{
		_browsingDisposed = true; CancelHover(); _hoverPreview.Dispose();
	}
}
