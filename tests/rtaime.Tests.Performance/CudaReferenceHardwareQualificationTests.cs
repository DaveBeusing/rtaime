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

	[Fact]
	public void Qualification_serialization_carries_p99_and_backend_timing_breakdown()
	{
		var caseResult = new CudaQualificationCaseResult(
			"1080p50",
			"CUT_A",
			30,
			20.0,
			1.0,
			2.0,
			2.5,
			3.0,
			new[]
			{
				new CudaQualificationTimingMetric(
					CudaGpuTimingOperation.KernelGpuElapsed.ToString(),
					30,
					0.2,
					0.3,
					0.4,
					0.5),
				new CudaQualificationTimingMetric(
					CudaGpuTimingOperation.ContextSynchronize.ToString(),
					30,
					0.3,
					0.4,
					0.5,
					0.6)
			},
			true,
			true,
			true);
		var report = new CudaQualificationReport(
			CudaQualificationReport.CurrentSchemaVersion,
			DateTimeOffset.Parse("2026-10-08T09:00:00Z"),
			CudaQualificationStatus.Passed,
			"NVIDIA Reference GPU",
			0,
			"NVIDIA Reference GPU",
			48UL * 1024 * 1024 * 1024,
			new[] { caseResult },
			Array.Empty<string>());

		var json = CudaReferenceHardwareQualification.Serialize(report);

		Assert.Equal("1.1", CudaQualificationReport.CurrentSchemaVersion);
		Assert.Contains("\"p99Milliseconds\": 2.5", json, StringComparison.Ordinal);
		Assert.Contains("\"backendTimings\":", json, StringComparison.Ordinal);
		Assert.Contains(CudaGpuTimingOperation.KernelGpuElapsed.ToString(), json, StringComparison.Ordinal);
		Assert.Contains(CudaGpuTimingOperation.ContextSynchronize.ToString(), json, StringComparison.Ordinal);
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
