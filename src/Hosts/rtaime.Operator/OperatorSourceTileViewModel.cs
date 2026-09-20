// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorSourceTileViewModel : INotifyPropertyChanged
{
	private OperatorSourceDescriptor _descriptor;
	private ImageSource? _thumbnail;
	private string _thumbnailFormat = "Waiting for source monitor frame.";
	private string _type;
	private string _format;
	private string _health;
	private string _mediaState;
	private string _remaining;
	private string? _mediaFileName;
	private bool _isPreview;
	private bool _isProgram;
	private double _audioLeftPeak;
	private double _audioRightPeak;
	private bool _audioClipping;
	private bool _hasAudio;
	private int _displayIndex;
	private string _timecodeLabel = "—";

	public OperatorSourceTileViewModel(OperatorSourceDescriptor descriptor)
	{
		_descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		_type = descriptor.Type;
		_format = descriptor.Format;
		_health = descriptor.Health;
		_mediaState = descriptor.MediaState;
		_remaining = FormatRemaining(descriptor.Remaining);
		_mediaFileName = descriptor.MediaFileName;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Id => _descriptor.Id;
	public int DisplayIndex { get => _displayIndex; private set => Set(ref _displayIndex, value); }
	public string Name => _descriptor.Name;
	public ImageSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }
	public string ThumbnailFormat { get => _thumbnailFormat; private set => Set(ref _thumbnailFormat, value); }
	public string Type { get => _type; private set => Set(ref _type, value); }
	public string Format { get => _format; private set => Set(ref _format, value); }
	public string FrameRateLabel => ExtractFrameRate(Format);
	public string TimecodeLabel { get => _timecodeLabel; private set => Set(ref _timecodeLabel, value); }
	public string Health { get => _health; private set => Set(ref _health, value); }
	public string MediaState { get => _mediaState; private set => Set(ref _mediaState, value); }
	public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
	public string? MediaFileName { get => _mediaFileName; private set => Set(ref _mediaFileName, value); }
	public bool IsPreview { get => _isPreview; private set { if (Set(ref _isPreview, value)) OnPropertyChanged(nameof(Tally)); } }
	public bool IsProgram { get => _isProgram; private set { if (Set(ref _isProgram, value)) OnPropertyChanged(nameof(Tally)); } }
	public double AudioLeftPeak { get => _audioLeftPeak; private set => Set(ref _audioLeftPeak, value); }
	public double AudioRightPeak { get => _audioRightPeak; private set => Set(ref _audioRightPeak, value); }
	public bool AudioClipping { get => _audioClipping; private set => Set(ref _audioClipping, value); }
	public bool HasAudio { get => _hasAudio; private set => Set(ref _hasAudio, value); }
	public string Tally => IsProgram && IsPreview ? "PGM + PVW" : IsProgram ? "PGM" : IsPreview ? "PVW" : "—";
	public bool IsMedia => string.Equals(Type, "MEDIA", StringComparison.OrdinalIgnoreCase);
	public bool IsTestPattern => string.Equals(Type, "TEST", StringComparison.OrdinalIgnoreCase);
	public string Detail => IsMedia && !string.IsNullOrWhiteSpace(MediaFileName)
		? MediaFileName!
		: IsTestPattern
			? $"INTERNAL TEST SIGNAL · {Format}"
			: $"{Type} · {Format}";
	public string StateDetail => IsMedia
		? $"{MediaState} · REM {Remaining}"
		: IsTestPattern
			? $"{MediaState} · GENERATED · SIGNAL {Health}"
			: $"SIGNAL {Health}";

	public void ApplyDescriptor(OperatorSourceDescriptor descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		if (!string.Equals(Id, descriptor.Id, StringComparison.Ordinal))
			throw new InvalidOperationException("Source tile identity cannot change.");

		_descriptor = descriptor;
		OnPropertyChanged(nameof(Name));
		Type = descriptor.Type;
		Format = descriptor.Format;
		Health = descriptor.Health;
		MediaState = descriptor.MediaState;
		Remaining = FormatRemaining(descriptor.Remaining);
		MediaFileName = descriptor.MediaFileName;
		OnPropertyChanged(nameof(IsMedia));
		OnPropertyChanged(nameof(IsTestPattern));
		OnPropertyChanged(nameof(Detail));
		OnPropertyChanged(nameof(StateDetail));
	}

	public void ApplyDisplayIndex(int index) => DisplayIndex = Math.Max(1, index);

	public void ApplyRouting(string previewSourceId, string programSourceId)
	{
		IsPreview = string.Equals(Id, previewSourceId, StringComparison.Ordinal);
		IsProgram = string.Equals(Id, programSourceId, StringComparison.Ordinal);
	}

	public void ApplyThumbnail(ImageSource thumbnail, string format)
	{
		Thumbnail = thumbnail ?? throw new ArgumentNullException(nameof(thumbnail));
		ThumbnailFormat = string.IsNullOrWhiteSpace(format) ? "Live source monitor" : format.Trim();
	}

	public void ApplyAudioMeter(double leftPeak, double rightPeak, bool clipping)
	{
		AudioLeftPeak = Math.Clamp(double.IsFinite(leftPeak) ? leftPeak : 0, 0, 1);
		AudioRightPeak = Math.Clamp(double.IsFinite(rightPeak) ? rightPeak : 0, 0, 1);
		AudioClipping = clipping;
		HasAudio = true;
	}

	public void ClearAudioMeter()
	{
		AudioLeftPeak = 0;
		AudioRightPeak = 0;
		AudioClipping = false;
		HasAudio = false;
	}

	public void ApplyMediaDeck(string state, string format, string remaining, string timecode, string? fileName)
	{
		if (IsTestPattern)
			return;

		Type = "MEDIA";
		Format = string.IsNullOrWhiteSpace(format) ? _descriptor.Format : format.Trim();
		Health = string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase) ? "ERROR" : "READY";
		MediaState = string.IsNullOrWhiteSpace(state) ? "UNKNOWN" : state.Trim().ToUpperInvariant();
		Remaining = string.IsNullOrWhiteSpace(remaining) ? "—" : remaining.Trim();
		TimecodeLabel = string.IsNullOrWhiteSpace(timecode) ? "—" : timecode.Trim();
		MediaFileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
		OnPropertyChanged(nameof(IsMedia));
		OnPropertyChanged(nameof(IsTestPattern));
		OnPropertyChanged(nameof(Detail));
		OnPropertyChanged(nameof(StateDetail));
	}

	public void ClearMediaDeck()
	{
		Type = _descriptor.Type;
		Format = _descriptor.Format;
		Health = _descriptor.Health;
		MediaState = _descriptor.MediaState;
		Remaining = FormatRemaining(_descriptor.Remaining);
		TimecodeLabel = "—";
		MediaFileName = _descriptor.MediaFileName;
	}

	private static string ExtractFrameRate(string format)
	{
		if (string.IsNullOrWhiteSpace(format))
			return "—";

		var normalized = format.Trim();
		var lastSpace = normalized.LastIndexOf(' ');
		if (lastSpace >= 0 && lastSpace < normalized.Length - 1)
			return normalized[(lastSpace + 1)..];

		var progressive = normalized.LastIndexOf('p');
		if (progressive >= 0 && progressive < normalized.Length - 1)
			return normalized[(progressive + 1)..];

		return normalized;
	}

	private static string FormatRemaining(TimeSpan? remaining)
	{
		if (!remaining.HasValue) return "—";
		var value = remaining.Value;
		return value.TotalHours >= 1
			? value.ToString(@"hh\:mm\:ss")
			: value.ToString(@"mm\:ss");
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		OnPropertyChanged(propertyName);
		if (propertyName is nameof(Type) or nameof(Format) or nameof(Health) or nameof(MediaState) or nameof(Remaining) or nameof(MediaFileName))
		{
			if (propertyName == nameof(Format))
				OnPropertyChanged(nameof(FrameRateLabel));
			OnPropertyChanged(nameof(IsMedia));
			OnPropertyChanged(nameof(IsTestPattern));
			OnPropertyChanged(nameof(Detail));
			OnPropertyChanged(nameof(StateDetail));
		}
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
