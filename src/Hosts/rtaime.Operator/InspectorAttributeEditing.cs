// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;

namespace rtaime.Operator;

public sealed partial class MediaPoolInspectorViewModel
{
	private sealed record AttributeDraft(string Kind, double[] Values, bool Enabled);
	private AttributeDraft? _attributeClipboard;
	private bool _inspectorInputsValid = true;
	public ICommand CopyAttributesCommand { get; }
	public ICommand PasteAttributesCommand { get; }
	public string EditingSelectionIdentity => _compositingNode?.Id ?? _timelineCue?.Name ??
		_timelineItem?.Label ?? SelectedItem?.Key ?? "none";
	public bool InspectorInputsValid
	{
		get => _inspectorInputsValid;
		internal set
		{
			if (!Set(ref _inspectorInputsValid, value)) return;
			RaiseSelectionState();
		}
	}
	public string InspectorEditState => _operator.IsBusy ? "PENDING · awaiting confirmation" :
		_operator.IsStale ? "STALE · resynchronize before editing" :
		!_operator.IsConnected ? "DISCONNECTED" :
		!InspectorInputsValid ? "INVALID INPUT · correct the numeric field" :
		!string.IsNullOrWhiteSpace(_operator.LastError) ? "REJECTED · " + _operator.LastError :
		_transformDraftDirty || _processingDraftDirty ? "DRAFT · APPLY requires confirmation" : "CONFIRMED · local edits require APPLY";
	private string AttributeKind => IsSelectedColorGradeProcessingNode ? "COLOR GRADE" :
		IsSelectedChromaKeyProcessingNode ? "CHROMA KEY" : "TRANSFORM";
	public string AttributeCompatibilityLabel => HasMultipleSelection
		? "Batch editing deferred: no atomic multi-selection property contract."
		: !HasAuthoritativeCompositingSelection
			? "Attribute paste requires an authoritative layer or compatible processing node."
			: _attributeClipboard is null
				? "Copy selected attributes; paste changes only a compatible draft."
				: _attributeClipboard.Kind == AttributeKind
					? $"Copied {AttributeKind}; paste changes the draft only. APPLY separately."
					: $"Incompatible: copied {_attributeClipboard.Kind}; selected {AttributeKind}.";
	private bool CanPasteAttributes() => CanEditAuthoritativeCompositingSelection() && !HasMultipleSelection &&
		_attributeClipboard?.Kind == AttributeKind;
	private Task CopyAttributesAsync()
	{
		if (!HasAuthoritativeCompositingSelection || !InspectorInputsValid || HasMultipleSelection) return Task.CompletedTask;
		var values = AttributeKind switch
		{
			"COLOR GRADE" => new[] { SelectedColorGradeBrightness, SelectedColorGradeContrast, SelectedColorGradeSaturation },
			"CHROMA KEY" => new[] { (double)SelectedChromaKeyRed, SelectedChromaKeyGreen, SelectedChromaKeyBlue, SelectedChromaKeyTolerance, SelectedChromaKeySoftness, SelectedChromaKeySpillSuppression },
			_ => new[] { SelectedLayerPositionX, SelectedLayerPositionY, SelectedLayerScale, SelectedLayerRotationDegrees,
				SelectedLayerAnchorX, SelectedLayerAnchorY, SelectedLayerCropLeft, SelectedLayerCropTop, SelectedLayerCropRight, SelectedLayerCropBottom }
		};
		_attributeClipboard = new(AttributeKind, values, AttributeKind == "COLOR GRADE" ? SelectedColorGradeEnabled : SelectedChromaKeyEnabled);
		OnPropertyChanged(nameof(AttributeCompatibilityLabel));
		(PasteAttributesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		return Task.CompletedTask;
	}
	private Task PasteAttributesAsync()
	{
		if (!CanPasteAttributes() || _attributeClipboard is not { } attributes) return Task.CompletedTask;
		var values = attributes.Values;
		var definitions = attributes.Kind switch
		{
			"COLOR GRADE" => new[] { global::rtaime.Operator.InspectorProperties.Brightness, global::rtaime.Operator.InspectorProperties.Contrast, global::rtaime.Operator.InspectorProperties.Saturation },
			"CHROMA KEY" => new[] { global::rtaime.Operator.InspectorProperties.KeyRed, global::rtaime.Operator.InspectorProperties.KeyGreen, global::rtaime.Operator.InspectorProperties.KeyBlue, global::rtaime.Operator.InspectorProperties.Tolerance, global::rtaime.Operator.InspectorProperties.Softness, global::rtaime.Operator.InspectorProperties.Spill },
			_ => new[] { global::rtaime.Operator.InspectorProperties.LayerPositionX, global::rtaime.Operator.InspectorProperties.LayerPositionY, global::rtaime.Operator.InspectorProperties.LayerScale, global::rtaime.Operator.InspectorProperties.LayerRotation,
				global::rtaime.Operator.InspectorProperties.AnchorX, global::rtaime.Operator.InspectorProperties.AnchorY, global::rtaime.Operator.InspectorProperties.CropLeft, global::rtaime.Operator.InspectorProperties.CropTop, global::rtaime.Operator.InspectorProperties.CropRight, global::rtaime.Operator.InspectorProperties.CropBottom }
		};
		if (values.Length != definitions.Length || values.Where((v, i) => !definitions[i].Accepts(v)).Any() ||
			(attributes.Kind == "TRANSFORM" && (values[6] + values[8] >= 1 || values[7] + values[9] >= 1)))
			throw new InvalidOperationException("Copied attributes are invalid for this selection; draft was not changed.");
		if (attributes.Kind == "COLOR GRADE")
		{
			SelectedColorGradeEnabled = attributes.Enabled;
			SelectedColorGradeBrightness = values[0]; SelectedColorGradeContrast = values[1]; SelectedColorGradeSaturation = values[2];
		}
		else if (attributes.Kind == "CHROMA KEY")
		{
			SelectedChromaKeyEnabled = attributes.Enabled;
			SelectedChromaKeyRed = (int)values[0]; SelectedChromaKeyGreen = (int)values[1]; SelectedChromaKeyBlue = (int)values[2];
			SelectedChromaKeyTolerance = values[3]; SelectedChromaKeySoftness = values[4]; SelectedChromaKeySpillSuppression = values[5];
		}
		else
		{
			SelectedLayerPositionX = values[0]; SelectedLayerPositionY = values[1]; SelectedLayerScale = values[2]; SelectedLayerRotationDegrees = values[3];
			SelectedLayerAnchorX = values[4]; SelectedLayerAnchorY = values[5]; SelectedLayerCropLeft = values[6]; SelectedLayerCropTop = values[7];
			SelectedLayerCropRight = values[8]; SelectedLayerCropBottom = values[9];
		}
		OnPropertyChanged(nameof(InspectorEditState));
		return Task.CompletedTask;
	}
}
