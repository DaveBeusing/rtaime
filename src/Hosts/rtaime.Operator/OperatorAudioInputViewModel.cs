// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class OperatorAudioInputViewModel : INotifyPropertyChanged
{
	private OperatorAudioInputDescriptor _descriptor;
	private string _sourceName;
	private string _sourceType;
	private double _gain;
	private bool _muted;
	private double _leftPeak;
	private double _rightPeak;
	private double _masterPeak;
	private bool _clipping;
	private string _health;
	private bool _isAfv;

	public OperatorAudioInputViewModel(
		OperatorAudioInputDescriptor descriptor,
		string sourceName,
		string sourceType,
		bool isAfv)
	{
		_descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		_sourceName = Normalize(sourceName, descriptor.SourceId);
		_sourceType = Normalize(sourceType, "LIVE").ToUpperInvariant();
		_gain = descriptor.Gain;
		_muted = descriptor.Muted;
		_leftPeak = descriptor.LeftPeak;
		_rightPeak = descriptor.RightPeak;
		_masterPeak = descriptor.MasterPeak;
		_clipping = descriptor.Clipping;
		_health = descriptor.Health;
		_isAfv = isAfv;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string SourceId => _descriptor.SourceId;
	public string StreamId => _descriptor.StreamId;
	public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }
	public string SourceType { get => _sourceType; private set => Set(ref _sourceType, value); }
	public double Gain
	{
		get => _gain;
		set => Set(ref _gain, Math.Clamp(value, 0, 4));
	}
	public bool Muted { get => _muted; private set { if (Set(ref _muted, value)) OnPropertyChanged(nameof(MuteActionLabel)); } }
	public double LeftPeak { get => _leftPeak; private set => Set(ref _leftPeak, value); }
	public double RightPeak { get => _rightPeak; private set => Set(ref _rightPeak, value); }
	public double MasterPeak { get => _masterPeak; private set => Set(ref _masterPeak, value); }
	public bool Clipping { get => _clipping; private set => Set(ref _clipping, value); }
	public string Health { get => _health; private set => Set(ref _health, value); }
	public bool IsAfv { get => _isAfv; private set { if (Set(ref _isAfv, value)) OnPropertyChanged(nameof(AfvLabel)); } }
	public string AfvLabel => IsAfv ? "AFV / PGM" : "INPUT";
	public string MuteActionLabel => Muted ? "UNMUTE" : "MUTE";

	public void Apply(
		OperatorAudioInputDescriptor descriptor,
		string sourceName,
		string sourceType,
		bool isAfv,
		bool preserveGainEdit = false)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		if (!string.Equals(SourceId, descriptor.SourceId, StringComparison.Ordinal))
			throw new InvalidOperationException("Audio input source identity cannot change.");

		_descriptor = descriptor;
		OnPropertyChanged(nameof(StreamId));
		SourceName = Normalize(sourceName, descriptor.SourceId);
		SourceType = Normalize(sourceType, "LIVE").ToUpperInvariant();
		if (!preserveGainEdit)
			Gain = descriptor.Gain;
		Muted = descriptor.Muted;
		LeftPeak = descriptor.LeftPeak;
		RightPeak = descriptor.RightPeak;
		MasterPeak = descriptor.MasterPeak;
		Clipping = descriptor.Clipping;
		Health = descriptor.Health;
		IsAfv = isAfv;
	}

	private static string Normalize(string value, string fallback) =>
		string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
