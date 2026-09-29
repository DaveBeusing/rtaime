// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum FrameDiagnosticAuthority
{
	Unavailable = 0,
	Authoritative = 1,
	Derived = 2,
	Optional = 3
}

public sealed record FrameDiagnosticsSnapshot(
	FrameDiagnosticAuthority TimingAuthority,
	ulong? FrameIndex,
	long? PresentationTimestamp,
	string PresentationTime,
	string Rate,
	uint SourceWidth,
	uint SourceHeight,
	string ColorPath,
	string Detail)
{
	public static FrameDiagnosticsSnapshot FromMonitoring(MonitoringFrameDescriptor descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);
		var timing = descriptor.Timing;
		double? seconds = timing.Timebase.Denominator == 0
			? null
			: timing.PresentationTimestamp * (double)timing.Timebase.Numerator / timing.Timebase.Denominator;
		var rate = timing.Timebase.Numerator > 0 && timing.Timebase.Denominator > 0
			? $"{timing.Timebase.Denominator / (double)timing.Timebase.Numerator:0.###} fps"
			: "UNAVAILABLE";
		return new(
			FrameDiagnosticAuthority.Authoritative,
			timing.SequenceNumber,
			timing.PresentationTimestamp,
			seconds is { } value && double.IsFinite(value) ? $"{value:0.000000} s" : "UNAVAILABLE",
			rate,
			descriptor.Width,
			descriptor.Height,
			MonitoringDisplayTransform.Describe(descriptor.Color),
			"Monitoring FrameTiming");
	}

	public static FrameDiagnosticsSnapshot Unavailable { get; } = new(
		FrameDiagnosticAuthority.Unavailable, null, null, "UNAVAILABLE", "UNAVAILABLE", 0, 0, "UNAVAILABLE",
		"No authoritative monitoring frame diagnostics are available.");
}
