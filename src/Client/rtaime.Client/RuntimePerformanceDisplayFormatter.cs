// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;

namespace rtaime.Client;

public static class RuntimePerformanceDisplayFormatter
{
	public const string Unverified = "UNVERIFIED";

	public static string FormatFrameTime(TimeSpan frameTime, TimeSpan frameBudget) =>
		frameBudget > TimeSpan.Zero && frameTime >= TimeSpan.Zero
			? $"{frameTime.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms / {frameBudget.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms"
			: Unverified;

	public static string FormatFramesPerSecond(double? framesPerSecond) =>
		framesPerSecond is { } value && double.IsFinite(value) && value > 0
			? $"{value.ToString("0.00", CultureInfo.InvariantCulture)} FPS"
			: Unverified;

	public static string FormatDroppedFrames(ulong droppedFrames) =>
		droppedFrames.ToString(CultureInfo.InvariantCulture);
}
