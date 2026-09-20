// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

public readonly record struct MotionTimingTimecode(
	int Hours,
	int Minutes,
	int Seconds,
	int Frames);

public readonly record struct MotionTimingTestSignalRegion(
	int X,
	int Y,
	int Width,
	int Height,
	ReadOnlyMemory<byte> Pixels);

/// <summary>
/// Renders the dynamic portion of the internal test signal from authoritative media timing only.
/// The backing storage is retained and reused; Render performs no full-frame allocation.
/// </summary>
public sealed class MotionTimingTestSignalGenerator
{
	private static readonly Rgba Panel = new(8, 11, 16);
	private static readonly Rgba PanelMuted = new(32, 39, 50);
	private static readonly Rgba White = new(235, 235, 235);
	private static readonly Rgba Accent = new(0, 198, 255);
	private static readonly Rgba Warning = new(255, 196, 64);
	private static readonly Rgba Dark = new(4, 6, 9);

	private static readonly IReadOnlyDictionary<char, byte[]> Glyphs =
		new ReadOnlyDictionary<char, byte[]>(new Dictionary<char, byte[]>
		{
			[' '] = [0, 0, 0, 0, 0, 0, 0],
			['-'] = [0, 0, 0, 31, 0, 0, 0],
			[':'] = [0, 12, 12, 0, 12, 12, 0],
			['0'] = [14, 17, 19, 21, 25, 17, 14],
			['1'] = [4, 12, 4, 4, 4, 4, 14],
			['2'] = [14, 17, 1, 2, 4, 8, 31],
			['3'] = [30, 1, 1, 14, 1, 1, 30],
			['4'] = [2, 6, 10, 18, 31, 2, 2],
			['5'] = [31, 16, 16, 30, 1, 1, 30],
			['6'] = [14, 16, 16, 30, 17, 17, 14],
			['7'] = [31, 1, 2, 4, 8, 8, 8],
			['8'] = [14, 17, 17, 14, 17, 17, 14],
			['9'] = [14, 17, 17, 15, 1, 1, 14],
			['A'] = [14, 17, 17, 31, 17, 17, 17],
			['C'] = [14, 17, 16, 16, 16, 17, 14],
			['D'] = [30, 17, 17, 17, 17, 17, 30],
			['E'] = [31, 16, 16, 30, 16, 16, 31],
			['F'] = [31, 16, 16, 30, 16, 16, 16],
			['I'] = [14, 4, 4, 4, 4, 4, 14],
			['M'] = [17, 27, 21, 21, 17, 17, 17],
			['N'] = [17, 25, 25, 21, 19, 19, 17],
			['R'] = [30, 17, 17, 30, 20, 18, 17],
			['S'] = [15, 16, 16, 14, 1, 1, 30],
			['T'] = [31, 4, 4, 4, 4, 4, 4],
			['V'] = [17, 17, 17, 17, 17, 10, 4]
		});

	private readonly VideoFormat _format;
	private readonly byte[] _pixels;
	private readonly int _width;
	private readonly int _height;
	private readonly int _regionY;
	private readonly int _scale;
	private readonly int _margin;
	private readonly int _trackLeft;
	private readonly int _trackRight;
	private readonly uint _syncAudioSampleRate;
	private readonly AvSyncEventTimeline _syncTimeline;

	public MotionTimingTestSignalGenerator(VideoFormat format, uint syncAudioSampleRate = 48_000)
	{
		if (syncAudioSampleRate == 0)
			throw new ArgumentOutOfRangeException(nameof(syncAudioSampleRate));
		if (format.PixelFormat != PixelFormat.Rgba8)
			throw new NotSupportedException("Motion/timing test signal generation currently supports RGBA8 only.");

		_format = format;
		_width = checked((int)format.Width);
		var frameHeight = checked((int)format.Height);
		_height = Math.Min(frameHeight, Math.Max(140, frameHeight * 16 / 100));
		_regionY = frameHeight - _height;
		_scale = Math.Max(2, Math.Min(_width / 640, _height / 45));
		_margin = Math.Max(16, _width / 40);
		_trackLeft = _margin;
		_trackRight = Math.Max(_trackLeft + 1, _width - _margin - 1);
		_syncAudioSampleRate = syncAudioSampleRate;
		_syncTimeline = new AvSyncEventTimeline();
		_pixels = new byte[checked(_width * _height * 4)];
	}

	public VideoFormat Format => _format;
	public int RegionX => 0;
	public int RegionY => _regionY;
	public int RegionWidth => _width;
	public int RegionHeight => _height;

	public MotionTimingTestSignalRegion Render(FrameTiming timing)
	{
		if (timing.PresentationTimestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timing), "Generated test-signal media time must not be negative.");

		var sync = InspectSyncEvent(timing);
		Fill(sync.IsFlashFrame ? Warning : Panel);
		DrawFrameCounter(timing.SequenceNumber);
		DrawTimecode(CalculateTimecode(timing.SequenceNumber, _format.FrameRate));
		DrawMotionTrack(timing);
		DrawFrameIndicator(timing.SequenceNumber);
		DrawSyncEvent(sync);

		return new MotionTimingTestSignalRegion(
			0,
			_regionY,
			_width,
			_height,
			_pixels);
	}

	public AvSyncVideoEventObservation InspectSyncEvent(FrameTiming timing) =>
		_syncTimeline.InspectVideo(timing, _format.FrameRate, _syncAudioSampleRate);

	public int GetMarkerX(FrameTiming timing)
	{
		if (timing.PresentationTimestamp < 0)
			throw new ArgumentOutOfRangeException(nameof(timing));

		var numerator = checked((UInt128)(ulong)timing.PresentationTimestamp * (ulong)timing.Timebase.Numerator);
		var denominator = checked((ulong)timing.Timebase.Denominator);
		var phase = numerator % denominator;
		var range = checked((uint)Math.Max(1, _trackRight - _trackLeft));
		return _trackLeft + checked((int)(phase * range / denominator));
	}

	public static MotionTimingTimecode CalculateTimecode(ulong sequenceNumber, FrameRate frameRate)
	{
		var rateNumerator = checked((ulong)frameRate.Numerator);
		var rateDenominator = checked((ulong)frameRate.Denominator);
		var elapsedSeconds = (UInt128)sequenceNumber * rateDenominator / rateNumerator;
		if (elapsedSeconds > ulong.MaxValue)
			throw new OverflowException("Motion/timing media time exceeded the supported range.");

		var secondsTotal = (ulong)elapsedSeconds;
		var firstFrameOfSecond =
			((UInt128)secondsTotal * rateNumerator + rateDenominator - 1) / rateDenominator;
		var frameWithinSecond = (UInt128)sequenceNumber - firstFrameOfSecond;
		if (frameWithinSecond > int.MaxValue)
			throw new OverflowException("Frame-within-second exceeded the supported display range.");

		return new MotionTimingTimecode(
			checked((int)((secondsTotal / 3600) % 100)),
			checked((int)((secondsTotal / 60) % 60)),
			checked((int)(secondsTotal % 60)),
			checked((int)frameWithinSecond));
	}

	private void DrawFrameCounter(ulong sequenceNumber)
	{
		var y = Math.Max(8, _height / 12);
		DrawText("FRAME", _margin, y, _scale, White);

		Span<char> digits = stackalloc char[20];
		if (!sequenceNumber.TryFormat(digits, out var written))
			throw new InvalidOperationException("Frame counter formatting failed.");
		DrawText(digits[..written], _margin + 34 * _scale, y, _scale, Accent);
	}

	private void DrawTimecode(MotionTimingTimecode timecode)
	{
		var y = Math.Max(8, _height / 12);
		var x = Math.Max(_width / 2, _margin + 300);
		DrawText("TC", x, y, _scale, White);
		x += 16 * _scale;
		DrawTwoDigits(timecode.Hours, ref x, y);
		DrawText(":", x, y, _scale, White);
		x += 6 * _scale;
		DrawTwoDigits(timecode.Minutes, ref x, y);
		DrawText(":", x, y, _scale, White);
		x += 6 * _scale;
		DrawTwoDigits(timecode.Seconds, ref x, y);
		DrawText(":", x, y, _scale, White);
		x += 6 * _scale;
		DrawTwoDigits(timecode.Frames, ref x, y);
	}

	private void DrawMotionTrack(FrameTiming timing)
	{
		var top = _height / 2;
		var centerY = top + Math.Max(8, _scale * 4);
		var lineHeight = Math.Max(2, _scale);
		FillRect(_trackLeft, centerY - lineHeight / 2, _trackRight - _trackLeft + 1, lineHeight, PanelMuted);

		for (var tick = 0; tick <= 10; tick++)
		{
			var x = _trackLeft + (_trackRight - _trackLeft) * tick / 10;
			var tickHeight = tick is 0 or 10 ? _scale * 5 : _scale * 3;
			FillRect(x, centerY - tickHeight / 2, Math.Max(1, _scale / 2), tickHeight, White);
		}

		var markerX = GetMarkerX(timing);
		var markerWidth = Math.Max(8, _scale * 5);
		var markerHeight = Math.Max(18, _scale * 10);
		FillRect(markerX - markerWidth / 2, centerY - markerHeight / 2, markerWidth, markerHeight, Accent);
		FillRect(markerX - Math.Max(1, _scale / 2), centerY - markerHeight, Math.Max(1, _scale), markerHeight * 2, Warning);

		DrawText("1S", _trackRight - 13 * _scale, centerY - 12 * _scale, _scale, White);
	}

	private void DrawSyncEvent(AvSyncVideoEventObservation observation)
	{
		var scale = Math.Max(2, _scale);
		var labelY = Math.Max(8, _height / 12);
		var labelX = Math.Max(_margin, _width / 3);
		var boxWidth = Math.Min(_width - labelX - _margin, 165 * scale);
		var boxHeight = 11 * scale;
		FillRect(labelX - 4 * scale, labelY - 2 * scale, boxWidth, boxHeight, observation.IsFlashFrame ? Dark : PanelMuted);
		DrawText("EVENT", labelX, labelY, scale, observation.IsFlashFrame ? Warning : White);

		Span<char> digits = stackalloc char[20];
		if (!observation.Event.EventId.TryFormat(digits, out var written))
			throw new InvalidOperationException("A/V sync event identifier formatting failed.");
		DrawText(digits[..written], labelX + 34 * scale, labelY, scale, Accent);
	}

	private void DrawFrameIndicator(ulong sequenceNumber)
	{
		const int cellCount = 16;
		var gap = Math.Max(2, _scale);
		var available = _width - _margin * 2 - gap * (cellCount - 1);
		var cellWidth = Math.Max(4, available / cellCount);
		var cellHeight = Math.Max(8, _scale * 4);
		var y = _height - cellHeight - Math.Max(8, _height / 16);
		var active = checked((int)(sequenceNumber % cellCount));

		for (var index = 0; index < cellCount; index++)
		{
			var x = _margin + index * (cellWidth + gap);
			var color = index == active
				? Warning
				: ((sequenceNumber + (ulong)index) & 1UL) == 0
					? PanelMuted
					: Dark;
			FillRect(x, y, cellWidth, cellHeight, color);
		}
	}

	private void DrawTwoDigits(int value, ref int x, int y)
	{
		if (value < 0 || value > 99)
			throw new ArgumentOutOfRangeException(nameof(value));

		Span<char> digits = stackalloc char[2];
		digits[0] = (char)('0' + value / 10);
		digits[1] = (char)('0' + value % 10);
		DrawText(digits, x, y, _scale, Accent);
		x += 12 * _scale;
	}

	private void DrawText(ReadOnlySpan<char> text, int x, int y, int scale, Rgba color)
	{
		var cursor = x;
		foreach (var raw in text)
		{
			var glyphKey = char.ToUpperInvariant(raw);
			if (!Glyphs.TryGetValue(glyphKey, out var rows))
				rows = Glyphs[' '];

			for (var row = 0; row < 7; row++)
			{
				for (var column = 0; column < 5; column++)
				{
					if ((rows[row] & (1 << (4 - column))) == 0)
						continue;
					FillRect(cursor + column * scale, y + row * scale, scale, scale, color);
				}
			}

			cursor += 6 * scale;
		}
	}

	private void Fill(Rgba color)
	{
		for (var offset = 0; offset < _pixels.Length; offset += 4)
		{
			_pixels[offset] = color.Red;
			_pixels[offset + 1] = color.Green;
			_pixels[offset + 2] = color.Blue;
			_pixels[offset + 3] = color.Alpha;
		}
	}

	private void FillRect(int x, int y, int width, int height, Rgba color)
	{
		if (width <= 0 || height <= 0)
			return;

		var left = Math.Max(0, x);
		var top = Math.Max(0, y);
		var right = Math.Min(_width, x + width);
		var bottom = Math.Min(_height, y + height);
		if (left >= right || top >= bottom)
			return;

		for (var row = top; row < bottom; row++)
		{
			var offset = checked((row * _width + left) * 4);
			for (var column = left; column < right; column++)
			{
				_pixels[offset] = color.Red;
				_pixels[offset + 1] = color.Green;
				_pixels[offset + 2] = color.Blue;
				_pixels[offset + 3] = color.Alpha;
				offset += 4;
			}
		}
	}

	private readonly record struct Rgba(byte Red, byte Green, byte Blue, byte Alpha = 255);
}
