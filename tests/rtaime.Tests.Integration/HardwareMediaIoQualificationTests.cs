// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Integration;

public sealed class HardwareMediaIoQualificationTests
{
	[Fact]
	public void Reference_hardware_profile_must_pass_when_explicitly_enabled()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("RTAIME_MEDIA_IO_REFERENCE_QUALIFICATION"), "1", StringComparison.Ordinal))
			return;

		var expectedAdapter = RequireEnvironment("RTAIME_MEDIA_IO_EXPECTED_ADAPTER");
		var expectedSdkRevision = RequireEnvironment("RTAIME_AJA_SDK_REVISION");
		var evidencePath = RequireEnvironment("RTAIME_MEDIA_IO_REFERENCE_EVIDENCE");
		var minimumFrames = int.Parse(RequireEnvironment("RTAIME_MEDIA_IO_REFERENCE_FRAMES"), System.Globalization.CultureInfo.InvariantCulture);
		if (minimumFrames < 10)
			throw new InvalidOperationException("Physical Media I/O qualification requires at least 10 accepted Program frames.");

		var formatName = Environment.GetEnvironmentVariable("RTAIME_MEDIA_IO_REFERENCE_FORMAT") ?? "1080p50";
		var format = formatName.Trim().ToLowerInvariant() switch
		{
			"1080p50" => VideoFormat.Hd1080p50Rgba8,
			"1080p59.94" or "1080p59_94" => VideoFormat.Hd1080p59_94Rgba8,
			_ => throw new InvalidOperationException("Qualification format must be 1080p50 or 1080p59.94.")
		};
		var requireExternalReference = bool.Parse(Environment.GetEnvironmentVariable("RTAIME_MEDIA_IO_REFERENCE_EXTERNAL") ?? "false");

		var sourceA = new MediaSourceId(Identity.Parse("71000000-0000-0000-0000-00000000000a"));
		var sourceB = new MediaSourceId(Identity.Parse("71000000-0000-0000-0000-00000000000b"));
		var adapter = new NativeMediaIoProviderAdapter();
		Assert.Contains(expectedAdapter, adapter.Metadata.AdapterName, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(expectedSdkRevision, adapter.Metadata.SdkRevision);
		Assert.False(string.IsNullOrWhiteSpace(adapter.Metadata.DriverVersion));

		NativeMediaIoProviderMetadata metadata = adapter.Metadata;
		MediaIoVerticalSliceStatistics statistics;
		MediaIoSignalState signalA;
		MediaIoSignalState signalB;
		using (var mediaIo = new MediaIoVerticalSlice(adapter, sourceA, sourceB, format, requireExternalReference))
		{
			var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
			ulong? lastSubmittedCapture = null;
			while (mediaIo.Statistics.OutputAccepted < (ulong)minimumFrames && DateTimeOffset.UtcNow < deadline)
			{
				mediaIo.PumpInputs();
				var latestA = mediaIo.LatestA;
				var latestB = mediaIo.LatestB;
				if (latestA is not null &&
					latestB is not null &&
					latestA.PortStatus.SignalState == MediaIoSignalState.Locked &&
					latestB.PortStatus.SignalState == MediaIoSignalState.Locked &&
					lastSubmittedCapture != latestA.CaptureSequence)
				{
					var timing = new FrameTiming(
						latestA.CaptureSequence,
						checked((long)latestA.CaptureSequence),
						new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));
					var submit = mediaIo.TrySubmitProgram(
						sourceA,
						timing,
						latestA.Video.Pixels.ToArray(),
						latestA.AudioSamples,
						latestA.AudioTiming);
					if (submit.Accepted)
						lastSubmittedCapture = latestA.CaptureSequence;
					else
						Assert.Equal("media.io.output.backpressure", submit.Failure?.Code);
				}

				Thread.Sleep(1);
			}

			statistics = mediaIo.Statistics;
			signalA = mediaIo.LatestA?.PortStatus.SignalState ?? MediaIoSignalState.Unknown;
			signalB = mediaIo.LatestB?.PortStatus.SignalState ?? MediaIoSignalState.Unknown;
			Assert.Equal(MediaIoSignalState.Locked, signalA);
			Assert.Equal(MediaIoSignalState.Locked, signalB);
			Assert.True(statistics.CapturedA >= (ulong)minimumFrames, $"Captured only {statistics.CapturedA} Source A frames.");
			Assert.True(statistics.CapturedB >= (ulong)minimumFrames, $"Captured only {statistics.CapturedB} Source B frames.");
			Assert.True(statistics.OutputAccepted >= (ulong)minimumFrames, $"Accepted only {statistics.OutputAccepted} Program frames.");
			Assert.Equal(0UL, statistics.CaptureFailures);
			Assert.Equal(0UL, statistics.OutputRejected);
		}

		var evidence = new
		{
			copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
			schemaVersion = "1.0",
			status = "PASSED",
			sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
			capturedAtUtc = DateTimeOffset.UtcNow,
			expectedAdapter,
			detectedAdapter = metadata.AdapterName,
			driverVersion = metadata.DriverVersion,
			ajaSdkRevision = metadata.SdkRevision,
			abiVersion = "1.1",
			managedMediaIoContractVersion = MediaIoContractVersion.Current.ToString(),
			format = formatName,
			transferMode = MediaIoTransferMode.PinnedHostLease.ToString(),
			externalReferenceRequired = requireExternalReference,
			signalA = signalA.ToString(),
			signalB = signalB.ToString(),
			statistics = new
			{
				statistics.CapturedA,
				statistics.CapturedB,
				statistics.CaptureWouldBlock,
				statistics.CaptureFailures,
				statistics.OutputAccepted,
				statistics.OutputBackpressure,
				statistics.OutputRejected
			}
		};
		var fullEvidencePath = Path.GetFullPath(evidencePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
		File.WriteAllText(
			fullEvidencePath,
			JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
	}

	private static string RequireEnvironment(string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
			throw new InvalidOperationException($"Environment variable '{name}' is required for physical Media I/O qualification.");
		return value.Trim();
	}
}
