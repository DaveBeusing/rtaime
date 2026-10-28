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

public sealed record ProgrammeLoudnessPolicy
{
	public ProgrammeLoudnessPolicy(string profileId, double targetLufs, double toleranceLu, double maximumTruePeakDbtp)
	{
		if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 64)
			throw new ArgumentException("A bounded loudness profile ID is required.", nameof(profileId));
		if (!double.IsFinite(targetLufs) || targetLufs < -70 || targetLufs > 0)
			throw new ArgumentOutOfRangeException(nameof(targetLufs));
		if (!double.IsFinite(toleranceLu) || toleranceLu <= 0 || toleranceLu > 10)
			throw new ArgumentOutOfRangeException(nameof(toleranceLu));
		if (!double.IsFinite(maximumTruePeakDbtp) || maximumTruePeakDbtp < -20 || maximumTruePeakDbtp > 0)
			throw new ArgumentOutOfRangeException(nameof(maximumTruePeakDbtp));
		ProfileId = profileId;
		TargetLufs = targetLufs;
		ToleranceLu = toleranceLu;
		MaximumTruePeakDbtp = maximumTruePeakDbtp;
	}

	public string ProfileId { get; }
	public double TargetLufs { get; }
	public double ToleranceLu { get; }
	public double MaximumTruePeakDbtp { get; }

	public static ProgrammeLoudnessPolicy EbuR128Linear { get; } =
		new("ebu-r128-linear", -23.0, 1.0, -1.0);
}
