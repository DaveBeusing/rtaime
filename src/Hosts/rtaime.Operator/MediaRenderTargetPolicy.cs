// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

public enum MediaScalingQuality
{
	Bicubic
}

public enum MediaScalingPath
{
	WpfFallback,
	GpuProvider
}

public readonly record struct MediaRenderTarget(
	int PixelWidth,
	int PixelHeight,
	MediaScalingQuality Quality,
	MediaScalingPath Path,
	bool RequiresPresentationRescale)
{
	public static MediaRenderTarget Create(
		double viewportWidthDip,
		double viewportHeightDip,
		double dpiScaleX,
		double dpiScaleY,
		MediaScalingPath path = MediaScalingPath.WpfFallback)
	{
		var physical = MediaPresentationGeometry.ToPhysicalPixels(
			viewportWidthDip,
			viewportHeightDip,
			dpiScaleX,
			dpiScaleY);

		return new MediaRenderTarget(
			Math.Max(1, (int)Math.Round(physical.Width, MidpointRounding.AwayFromZero)),
			Math.Max(1, (int)Math.Round(physical.Height, MidpointRounding.AwayFromZero)),
			MediaScalingQuality.Bicubic,
			path,
			RequiresPresentationRescale: path == MediaScalingPath.WpfFallback);
	}
}

public readonly record struct MediaScalingDiagnostics(
	int SourceWidth,
	int SourceHeight,
	MediaRenderTarget Target,
	int PresentationScaleStages,
	long TargetRevision,
	bool ResizePending)
{
	public bool IsPreferredPath => Target.Path == MediaScalingPath.GpuProvider;
	public bool HasSingleIntentionalScaleStage => PresentationScaleStages == 1;
}

public sealed class MediaRenderTargetStabilizer
{
	private readonly TimeSpan _settleTime;
	private MediaRenderTarget? _active;
	private MediaRenderTarget? _pending;
	private DateTimeOffset _pendingSince;
	private long _revision;

	public MediaRenderTargetStabilizer(TimeSpan? settleTime = null)
	{
		_settleTime = settleTime ?? TimeSpan.FromMilliseconds(75);
		if (_settleTime < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(settleTime));
	}

	public MediaRenderTarget Adopt(MediaRenderTarget requested, DateTimeOffset now)
	{
		if (_active is null)
		{
			_active = requested;
			_revision++;
			return requested;
		}

		if (_active.Value == requested)
		{
			_pending = null;
			return _active.Value;
		}

		if (_pending is null || _pending.Value != requested)
		{
			_pending = requested;
			_pendingSince = now;
			return _active.Value;
		}

		if (now - _pendingSince >= _settleTime)
		{
			_active = requested;
			_pending = null;
			_revision++;
		}

		return _active.Value;
	}

	public bool ResizePending => _pending is not null;
	public long Revision => _revision;
}
