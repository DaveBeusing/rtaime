// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using rtaime.Client;

namespace rtaime.Operator;

/// <summary>
/// Presentation-only projection of a governed Scene. Selection is local Operator state;
/// Active evidence is derived exclusively from the authoritative Control snapshot.
/// </summary>
public sealed class OperatorSceneViewModel : INotifyPropertyChanged
{
	private OperatorSceneDescriptor _descriptor;
	private OperatorSourceTileViewModel _programSource;
	private int _displayIndex;
	private bool _isPreview;
	private bool _isActive;

	public OperatorSceneViewModel(
		OperatorSceneDescriptor descriptor,
		OperatorSourceTileViewModel programSource,
		int displayIndex)
	{
		_descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		_programSource = programSource ?? throw new ArgumentNullException(nameof(programSource));
		_displayIndex = Math.Max(1, displayIndex);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Id => _descriptor.Id;
	public string Name => _descriptor.Name;
	public string PreviewSourceId => _descriptor.PreviewSourceId;
	public string ProgramSourceId => _descriptor.ProgramSourceId;
	public string Type => "SCENE";
	public int DisplayIndex { get => _displayIndex; private set => Set(ref _displayIndex, value); }
	public OperatorSourceTileViewModel ProgramSource { get => _programSource; private set => Set(ref _programSource, value); }
	public bool IsPreview { get => _isPreview; private set => Set(ref _isPreview, value); }
	public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
	public string Evidence => IsActive ? "ACTIVE" : IsPreview ? "PREVIEW" : "READY";

	public void Apply(
		OperatorSceneDescriptor descriptor,
		OperatorSourceTileViewModel programSource,
		int displayIndex)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(programSource);
		if (!string.Equals(Id, descriptor.Id, StringComparison.Ordinal))
			throw new InvalidOperationException("Scene presentation identity cannot change.");

		_descriptor = descriptor;
		ProgramSource = programSource;
		DisplayIndex = Math.Max(1, displayIndex);
		OnPropertyChanged(nameof(Name));
		OnPropertyChanged(nameof(PreviewSourceId));
		OnPropertyChanged(nameof(ProgramSourceId));
	}

	public void ApplyEvidence(string previewSourceId, string? activeSceneId)
	{
		IsPreview = string.Equals(PreviewSourceId, previewSourceId, StringComparison.Ordinal);
		IsActive = !string.IsNullOrWhiteSpace(activeSceneId) &&
			string.Equals(Id, activeSceneId, StringComparison.Ordinal);
		OnPropertyChanged(nameof(Evidence));
	}

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
