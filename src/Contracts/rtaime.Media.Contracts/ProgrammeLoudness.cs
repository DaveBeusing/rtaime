// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media.Contracts;

// An optional, read-only measurement projection. Null means the source did not
// publish this capability; it must never be interpreted as zero or compliance.
public sealed record ProgrammeLoudnessSnapshot(
	int FormatVersion,
	string ProfileId,
	string State,
	int SampleRate,
	int ChannelCount,
	double MeasurementSeconds,
	ulong ResetGeneration,
	double? MomentaryLufs,
	double? ShortTermLufs,
	double? IntegratedLufs,
	double? LoudnessRangeLu,
	double? TruePeakDbtp,
	double? RelativeGateLufs,
	string PolicyStatus,
	string Source,
	string Qualification = "UNVERIFIED")
{
	public const int CurrentFormatVersion = 1;
	public bool HasIntegratedReading => IntegratedLufs is double value && double.IsFinite(value);
	public bool HasTruePeakReading => TruePeakDbtp is double value && double.IsFinite(value);
}

public sealed record ProgrammeLoudnessPolicy(
	string ProfileId,
	double TargetLufs,
	double ToleranceLu,
	double MaximumTruePeakDbtp)
{
	public static ProgrammeLoudnessPolicy EbuR128Linear { get; } =
		new("ebu-r128-linear", -23.0, 1.0, -1.0);

	public ProgrammeLoudnessPolicy
	{
		if (string.IsNullOrWhiteSpace(ProfileId) || ProfileId.Length > 64)
			throw new ArgumentException("A bounded loudness profile ID is required.", nameof(ProfileId));
		if (!double.IsFinite(TargetLufs) || TargetLufs < -70 || TargetLufs > 0)
			throw new ArgumentOutOfRangeException(nameof(TargetLufs));
		if (!double.IsFinite(ToleranceLu) || ToleranceLu <= 0 || ToleranceLu > 10)
			throw new ArgumentOutOfRangeException(nameof(ToleranceLu));
		if (!double.IsFinite(MaximumTruePeakDbtp) || MaximumTruePeakDbtp < -20 || MaximumTruePeakDbtp > 0)
			throw new ArgumentOutOfRangeException(nameof(MaximumTruePeakDbtp));
	}
}
