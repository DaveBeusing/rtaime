// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Globalization;

namespace rtaime.Operator;

// Immutable editing metadata; values and commit authority remain in the existing view models.
public sealed record InspectorPropertyDefinition(
	string Id, string Label, double Minimum, double Maximum, double DefaultValue,
	double Step, string Unit, bool Integral = false)
{
	public bool Accepts(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum &&
		(!Integral || value == Math.Truncate(value));

	public bool TryParse(string? text, CultureInfo culture, out double value) =>
		double.TryParse(text, NumberStyles.Float, culture, out value) && Accepts(value);

	public double Adjust(double value, double delta, bool fine, bool coarse)
	{
		var step = Step * (fine ? 0.1 : coarse ? 10 : 1);
		if (Integral) step = Math.Max(1, step);
		var result = Math.Clamp(value + delta * step, Minimum, Maximum);
		return Integral ? Math.Round(result, MidpointRounding.AwayFromZero) : result;
	}

	public string Help => $"{Label}: {Minimum}..{Maximum} {Unit}. Up/Down; Shift fine; Ctrl coarse; Alt+drag scrub; Ctrl+Home reset; Escape restore. Draft only; APPLY confirms.";
}

public static class InspectorProperties
{
	public static InspectorPropertyDefinition LayerPositionX { get; } = new("layer.x", "Position X", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition LayerPositionY { get; } = new("layer.y", "Position Y", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition LayerScale { get; } = new("layer.scale", "Scale", 0.05, 4, 1, 0.01, "×");
	public static InspectorPropertyDefinition LayerRotation { get; } = new("layer.rotation", "Rotation", -180, 180, 0, 0.1, "°");
	public static InspectorPropertyDefinition AnchorX { get; } = new("layer.anchor.x", "Anchor X", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition AnchorY { get; } = new("layer.anchor.y", "Anchor Y", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition CropLeft { get; } = new("layer.crop.left", "Crop Left", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition CropTop { get; } = new("layer.crop.top", "Crop Top", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition CropRight { get; } = new("layer.crop.right", "Crop Right", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition CropBottom { get; } = new("layer.crop.bottom", "Crop Bottom", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition Brightness { get; } = new("grade.brightness", "Brightness", -1, 1, 0, 0.01, "norm");
	public static InspectorPropertyDefinition Contrast { get; } = new("grade.contrast", "Contrast", 0, 2, 1, 0.01, "×");
	public static InspectorPropertyDefinition Saturation { get; } = new("grade.saturation", "Saturation", 0, 2, 1, 0.01, "×");
	public static InspectorPropertyDefinition KeyRed { get; } = new("key.red", "Key red", 0, 255, 0, 1, "8-bit", true);
	public static InspectorPropertyDefinition KeyGreen { get; } = new("key.green", "Key green", 0, 255, 255, 1, "8-bit", true);
	public static InspectorPropertyDefinition KeyBlue { get; } = new("key.blue", "Key blue", 0, 255, 0, 1, "8-bit", true);
	public static InspectorPropertyDefinition Tolerance { get; } = new("key.tolerance", "Tolerance", 0, 1, 0.1, 0.001, "norm");
	public static InspectorPropertyDefinition Softness { get; } = new("key.softness", "Softness", 0, 1, 0.1, 0.001, "norm");
	public static InspectorPropertyDefinition Spill { get; } = new("key.spill", "Spill suppression", 0, 1, 0, 0.001, "norm");
	public static InspectorPropertyDefinition AudioGain { get; } = new("audio.gain", "Gain", 0, 4, 1, 0.01, "×");
	public static InspectorPropertyDefinition GraphicsX { get; } = new("graphics.x", "Graphics X", 0, 100, 72, 0.1, "%");
	public static InspectorPropertyDefinition GraphicsY { get; } = new("graphics.y", "Graphics Y", 0, 100, 6, 0.1, "%");
	public static InspectorPropertyDefinition GraphicsScale { get; } = new("graphics.scale", "Graphics scale", 0.05, 4, 1, 0.01, "×");
}
