// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Provider.Contracts;

/// <summary>
/// Provider capability and observational boundary for external media references.
/// This does not read Windows wall clock to infer physical PTP or Genlock lock.
/// </summary>
public sealed record MediaReferenceClockCapabilities(
	string ProviderId,
	MediaReferenceClockSource Source,
	bool SupportsHoldover,
	bool HardwareTimestamping,
	string Qualification)
{
	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 80 ||
			!ProviderId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ||
			!Enum.IsDefined(Source) ||
			string.IsNullOrWhiteSpace(Qualification) || Qualification.Length > 128 ||
			HardwareTimestamping && Source is not (MediaReferenceClockSource.HardwarePtp or
				MediaReferenceClockSource.ExternalGenlock))
			throw new ArgumentException("Invalid reference clock provider capabilities.");
	}
}

public interface IMediaReferenceClockProvider
{
	MediaReferenceClockCapabilities Capabilities { get; }

	// The monotonic time originates with the Runtime's existing scheduler.
	// Provider readings are observations; they cannot drive another Program loop.
	MediaReferenceClockObservation Observe(TimeSpan runtimeMonotonicTime, DateTimeOffset observedAtUtc);
}
