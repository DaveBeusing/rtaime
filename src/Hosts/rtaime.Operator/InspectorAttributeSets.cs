// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

// Local clipboard values only: no commands, resource identities or authoritative state.
public abstract record InspectorAttributeValues
{
    public abstract string Kind { get; }
    public abstract bool IsValid { get; }
}
public sealed record InspectorTransformAttributes(double X, double Y, double Scale, double Rotation,
    double AnchorX, double AnchorY, double Left, double Top, double Right, double Bottom) : InspectorAttributeValues
{
    public override string Kind => "TRANSFORM";
    public override bool IsValid => InspectorProperties.LayerPositionX.Accepts(X) && InspectorProperties.LayerPositionY.Accepts(Y) &&
        InspectorProperties.LayerScale.Accepts(Scale) && InspectorProperties.LayerRotation.Accepts(Rotation) &&
        InspectorProperties.AnchorX.Accepts(AnchorX) && InspectorProperties.AnchorY.Accepts(AnchorY) &&
        InspectorProperties.CropLeft.Accepts(Left) && InspectorProperties.CropTop.Accepts(Top) &&
        InspectorProperties.CropRight.Accepts(Right) && InspectorProperties.CropBottom.Accepts(Bottom) &&
        Left + Right < 1 && Top + Bottom < 1;
}
public sealed record InspectorGradeAttributes(bool Enabled, double Brightness, double Contrast, double Saturation) : InspectorAttributeValues
{
    public override string Kind => "COLOR GRADE";
    public override bool IsValid => InspectorProperties.Brightness.Accepts(Brightness) &&
        InspectorProperties.Contrast.Accepts(Contrast) && InspectorProperties.Saturation.Accepts(Saturation);
}
public sealed record InspectorKeyAttributes(bool Enabled, double Red, double Green, double Blue,
    double Tolerance, double Softness, double Spill) : InspectorAttributeValues
{
    public override string Kind => "CHROMA KEY";
    public override bool IsValid => InspectorProperties.KeyRed.Accepts(Red) && InspectorProperties.KeyGreen.Accepts(Green) &&
        InspectorProperties.KeyBlue.Accepts(Blue) && InspectorProperties.Tolerance.Accepts(Tolerance) &&
        InspectorProperties.Softness.Accepts(Softness) && InspectorProperties.Spill.Accepts(Spill);
}
public sealed record InspectorGainAttributes(double Gain) : InspectorAttributeValues
{
    public override string Kind => "AUDIO GAIN";
    public override bool IsValid => InspectorProperties.AudioGain.Accepts(Gain);
}
public sealed record InspectorCueAttributes(string Name) : InspectorAttributeValues
{
    public override string Kind => "CUE NAME";
    public override bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= global::rtaime.Media.Contracts.MediaCuePoint.MaximumNameLength && !Name.Any(char.IsControl);
}
public sealed record InspectorAttributePayload(int Version, string RevisionToken, InspectorAttributeValues Values);
