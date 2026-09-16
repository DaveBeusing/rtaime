// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaIoVerticalSliceTests
{
	private static readonly MediaSourceId SourceA = new(Identity.Parse("96000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB = new(Identity.Parse("96000000-0000-0000-0000-000000000002"));

	[Fact]
	public void Pump_captures_two_inputs_and_releases_each_provider_lease_before_return()
	{
		using var adapter = new FakeMediaIoAdapter();
		using var slice = new MediaIoVerticalSlice(adapter, SourceA, SourceB, VideoFormat.Hd1080p50Rgba8);

		var captured = slice.PumpInputs();

		Assert.True(captured);
		Assert.NotNull(slice.LatestA);
		Assert.NotNull(slice.LatestB);
		Assert.Equal(SourceA, slice.LatestA!.RuntimeSourceId);
		Assert.Equal(SourceB, slice.LatestB!.RuntimeSourceId);
		Assert.Equal(2, adapter.ReleasedLeases);
		Assert.Equal((byte)16, slice.LatestA.Video.Pixels.Span[0]);
		Assert.Equal((byte)192, slice.LatestB.Video.Pixels.Span[0]);
		Assert.Equal((ulong)1, slice.Statistics.CapturedA);
		Assert.Equal((ulong)1, slice.Statistics.CapturedB);
	}

	[Fact]
	public void Program_submit_pins_payload_only_for_call_and_surfaces_backpressure_without_queueing()
	{
		using var adapter = new FakeMediaIoAdapter { RejectNextOutputAsBackpressure = true };
		using var slice = new MediaIoVerticalSlice(adapter, SourceA, SourceB, VideoFormat.Hd1080p50Rgba8);
		var pixels = RgbaFrameBuffer.Solid(VideoFormat.Hd1080p50Rgba8, 32, 64, 96).Pixels.ToArray();
		var timing = new FrameTiming(3, 3, new Timebase(1, 50));

		var rejected = slice.TrySubmitProgram(SourceA, timing, pixels);
		var accepted = slice.TrySubmitProgram(SourceA, timing, pixels);

		Assert.False(rejected.Accepted);
		Assert.Equal("media.io.output.backpressure", rejected.Failure?.Code);
		Assert.True(accepted.Accepted);
		Assert.Equal((ulong)1, slice.Statistics.OutputBackpressure);
		Assert.Equal((ulong)1, slice.Statistics.OutputAccepted);
		Assert.Equal(2, adapter.OutputSubmitCalls);
		Assert.All(adapter.SeenVideoAddresses, address => Assert.NotEqual((ulong)0, address));
	}

	[Fact]
	public void Construction_fails_closed_without_two_inputs_and_one_output()
	{
		using var adapter = new FakeMediaIoAdapter(includeSecondInput: false);

		Assert.Throws<InvalidOperationException>(() =>
			new MediaIoVerticalSlice(adapter, SourceA, SourceB, VideoFormat.Hd1080p50Rgba8));
	}

	private sealed class FakeMediaIoAdapter : IMediaIoProviderAdapter, IDisposable
	{
		private readonly Dictionary<MediaIoPortId, FakeInputSession> _inputs = new();
		private readonly FakeOutputSession _output;
		private int _disposed;

		public FakeMediaIoAdapter(bool includeSecondInput = true)
		{
			var providerId = new ProviderId(Identity.Parse("96000000-0000-0000-0000-000000000010"));
			var ports = new List<MediaIoPortDescriptor>();
			var resources = new List<ProviderResourceDescriptor>();
			var formats = new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 };

			AddPort(MediaIoDirection.Input, 1, providerId, ports, resources, formats);
			if (includeSecondInput)
				AddPort(MediaIoDirection.Input, 2, providerId, ports, resources, formats);
			AddPort(MediaIoDirection.Output, 3, providerId, ports, resources, formats);

			var capabilities = new[]
			{
				new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoInput, formats),
				new ProviderCapabilityDescriptor(CapabilityId.New(), MediaIoCapabilityKinds.VideoOutput, formats)
			};
			var provider = new ProviderDescriptor(
				ProviderContractVersion.Current,
				providerId,
				"Fake Media I/O",
				new ProviderAvailability(ProviderAvailabilityState.Available),
				capabilities,
				resources);
			Descriptor = new MediaIoProviderDescriptor(MediaIoContractVersion.Current, provider, ports);

			foreach (var port in ports.Where(port => port.Direction == MediaIoDirection.Input))
			{
				var red = _inputs.Count == 0 ? (byte)16 : (byte)192;
				_inputs.Add(port.PortId, new FakeInputSession(port, red, () => ReleasedLeases++));
			}
			_output = new FakeOutputSession(ports.Single(port => port.Direction == MediaIoDirection.Output), this);
		}

		public MediaIoProviderDescriptor Descriptor { get; }
		public int ReleasedLeases { get; private set; }
		public bool RejectNextOutputAsBackpressure { get; set; }
		public int OutputSubmitCalls => _output.SubmitCalls;
		public IReadOnlyList<ulong> SeenVideoAddresses => _output.SeenVideoAddresses;

		public MediaIoPortStatus GetPortStatus(MediaIoPortId portId) =>
			new(MediaIoContractVersion.Current, portId, MediaIoSignalState.Locked, new UtcTimestamp(DateTimeOffset.UtcNow), VideoFormat.Hd1080p50Rgba8);

		public IMediaIoInputSession OpenInput(MediaIoSessionRequest request) => _inputs[request.PortId];
		public IMediaIoOutputSession OpenOutput(MediaIoSessionRequest request) => _output;

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
			foreach (var input in _inputs.Values) input.Dispose();
			_output.Dispose();
		}

		private static void AddPort(
			MediaIoDirection direction,
			byte ordinal,
			ProviderId providerId,
			ICollection<MediaIoPortDescriptor> ports,
			ICollection<ProviderResourceDescriptor> resources,
			IReadOnlyList<VideoFormat> formats)
		{
			var identity = $"96000000-0000-0000-0000-{ordinal:000000000000}";
			var resourceIdentity = $"97000000-0000-0000-0000-{ordinal:000000000000}";
			var portId = new MediaIoPortId(Identity.Parse(identity));
			var resourceId = new ProviderResourceId(Identity.Parse(resourceIdentity));
			var kind = direction == MediaIoDirection.Input ? MediaIoCapabilityKinds.VideoInput : MediaIoCapabilityKinds.VideoOutput;
			resources.Add(new ProviderResourceDescriptor(resourceId, providerId, kind, 1, true));
			ports.Add(new MediaIoPortDescriptor(
				MediaIoContractVersion.Current,
				providerId,
				resourceId,
				portId,
				$"Fake {direction} {ordinal}",
				direction,
				MediaIoTransportKind.Sdi,
				formats,
				new[] { new MediaIoNativeVideoFormat(1920, 1080, FrameRate.Fps50, MediaIoNativePixelFormat.Rgba8, ScanMode.Progressive) },
				new[] { AudioFormat.Stereo48kFloat32 },
				new[] { MediaIoTransferMode.PinnedHostLease },
				supportsExternalReference: direction == MediaIoDirection.Output));
		}
	}

	private sealed class FakeInputSession : IMediaIoInputSession
	{
		private readonly byte[] _pixels;
		private readonly Action _released;
		private bool _served;

		public FakeInputSession(MediaIoPortDescriptor port, byte red, Action released)
		{
			Port = port;
			_pixels = RgbaFrameBuffer.Solid(VideoFormat.Hd1080p50Rgba8, red, 0, 0).Pixels.ToArray();
			_released = released;
		}

		public MediaIoPortDescriptor Port { get; }
		public MediaIoPortStatus Status =>
			new(MediaIoContractVersion.Current, Port.PortId, MediaIoSignalState.Locked, new UtcTimestamp(DateTimeOffset.UtcNow), VideoFormat.Hd1080p50Rgba8);

		public bool TryAcquire(out MediaIoInputFrameLease? lease)
		{
			if (_served)
			{
				lease = null;
				return false;
			}
			_served = true;
			var pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
			var leaseId = Identity.New();
			var address = checked((ulong)pin.AddrOfPinnedObject().ToInt64());
			var surface = new SurfaceDescriptor(
				SurfaceId.New(),
				VideoFormat.Hd1080p50Rgba8,
				SurfaceStorageDomain.Host,
				SurfaceOwnership.SharedLease,
				new SurfaceLifetimeDescriptor(Generation.Initial, leaseId),
				new OpaqueSurfaceHandle(PinnedHostMediaIoMemory.VideoHandleKind, address.ToString(CultureInfo.InvariantCulture)));
			var frame = new FrameDescriptor(
				MediaContractVersion.Current,
				MediaSourceId.New(),
				surface,
				new FrameTiming(0, 0, new Timebase(1, 50)));
			var descriptor = new MediaIoInputFrameDescriptor(MediaIoContractVersion.Current, Port.PortId, frame);
			lease = new MediaIoInputFrameLease(descriptor, _ =>
			{
				pin.Free();
				_released();
			});
			return true;
		}

		public void Dispose() { }
	}

	private sealed class FakeOutputSession : IMediaIoOutputSession
	{
		private readonly FakeMediaIoAdapter _owner;

		public FakeOutputSession(MediaIoPortDescriptor port, FakeMediaIoAdapter owner)
		{
			Port = port;
			_owner = owner;
		}

		public MediaIoPortDescriptor Port { get; }
		public MediaIoPortStatus Status =>
			new(MediaIoContractVersion.Current, Port.PortId, MediaIoSignalState.Locked, new UtcTimestamp(DateTimeOffset.UtcNow), VideoFormat.Hd1080p50Rgba8);
		public int SubmitCalls { get; private set; }
		public List<ulong> SeenVideoAddresses { get; } = new();

		public MediaIoOutputSubmitResult TrySubmit(MediaIoOutputFrameDescriptor frame)
		{
			SubmitCalls++;
			Assert.NotNull(frame.Video.Surface.Handle);
			Assert.True(ulong.TryParse(frame.Video.Surface.Handle!.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address));
			SeenVideoAddresses.Add(address);
			if (_owner.RejectNextOutputAsBackpressure)
			{
				_owner.RejectNextOutputAsBackpressure = false;
				return MediaIoOutputSubmitResult.Rejected(new Failure("media.io.output.backpressure", "test backpressure"));
			}
			return MediaIoOutputSubmitResult.Success();
		}

		public void Dispose() { }
	}
}
