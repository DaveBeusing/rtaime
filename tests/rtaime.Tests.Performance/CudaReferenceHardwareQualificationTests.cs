// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Provider.Gpu;

namespace rtaime.Tests.Performance;

public sealed class CudaReferenceHardwareQualificationTests
{
	[Fact]
	[Trait("Qualification", "CudaReferenceHardware")]
	public void Reference_hardware_profile_must_pass_when_explicitly_enabled()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("RTAIME_CUDA_REFERENCE_QUALIFICATION"), "1", StringComparison.Ordinal))
			return;

		var expectedDevice = Environment.GetEnvironmentVariable("RTAIME_CUDA_REFERENCE_DEVICE");
		Assert.False(string.IsNullOrWhiteSpace(expectedDevice), "RTAIME_CUDA_REFERENCE_DEVICE is required for an explicit CUDA qualification run.");
		var deviceOrdinal = ParseNonNegativeInt("RTAIME_CUDA_REFERENCE_DEVICE_ORDINAL", 0);
		var samples = ParsePositiveInt("RTAIME_CUDA_REFERENCE_SAMPLES", 30);
		var warmup = ParseNonNegativeInt("RTAIME_CUDA_REFERENCE_WARMUP", 4);
		var evidencePath = Environment.GetEnvironmentVariable("RTAIME_CUDA_REFERENCE_EVIDENCE");
		Assert.False(string.IsNullOrWhiteSpace(evidencePath), "RTAIME_CUDA_REFERENCE_EVIDENCE is required for an explicit CUDA qualification run.");

		var profile = new CudaQualificationProfile(expectedDevice!, deviceOrdinal, warmup, samples);
		var report = CudaReferenceHardwareQualification.Run(profile);
		var directory = Path.GetDirectoryName(Path.GetFullPath(evidencePath!));
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		File.WriteAllText(evidencePath!, CudaReferenceHardwareQualification.Serialize(report));

		Assert.True(
			report.Status == CudaQualificationStatus.Passed,
			$"CUDA reference hardware qualification returned {report.Status}: {string.Join(" | ", report.Failures)}");
	}

	[Fact]
	public void Unverified_report_can_never_be_serialized_as_passed()
	{
		var profile = new CudaQualificationProfile("NVIDIA Reference GPU");
		var report = CudaReferenceHardwareQualification.Unverified(profile, "Physical reference hardware was not exercised.");
		var json = CudaReferenceHardwareQualification.Serialize(report);

		Assert.Equal(CudaQualificationStatus.Unverified, report.Status);
		Assert.Contains("\"status\": \"UNVERIFIED\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"status\": \"PASSED\"", json, StringComparison.Ordinal);
	}

	private static int ParsePositiveInt(string name, int defaultValue)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value)) return defaultValue;
		Assert.True(int.TryParse(value, out var parsed) && parsed > 0, $"{name} must be a positive integer.");
		return parsed;
	}

	private static int ParseNonNegativeInt(string name, int defaultValue)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value)) return defaultValue;
		Assert.True(int.TryParse(value, out var parsed) && parsed >= 0, $"{name} must be a non-negative integer.");
		return parsed;
	}
}
