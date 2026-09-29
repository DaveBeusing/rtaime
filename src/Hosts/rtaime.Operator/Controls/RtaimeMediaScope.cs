// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public sealed class RtaimeMediaScope : Control
{
	public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
		nameof(Snapshot), typeof(MediaScopeSnapshot), typeof(RtaimeMediaScope),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ScopeKindProperty = DependencyProperty.Register(
		nameof(ScopeKind), typeof(MediaScopeKind), typeof(RtaimeMediaScope),
		new FrameworkPropertyMetadata(MediaScopeKind.Histogram, FrameworkPropertyMetadataOptions.AffectsRender));

	public MediaScopeSnapshot? Snapshot
	{
		get => (MediaScopeSnapshot?)GetValue(SnapshotProperty);
		set => SetValue(SnapshotProperty, value);
	}

	public MediaScopeKind ScopeKind
	{
		get => (MediaScopeKind)GetValue(ScopeKindProperty);
		set => SetValue(ScopeKindProperty, value);
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		var snapshot = Snapshot;
		if (snapshot is null || ActualWidth <= 1 || ActualHeight <= 1)
			return;

		switch (ScopeKind)
		{
			case MediaScopeKind.Histogram:
				DrawHistogram(drawingContext, snapshot.LumaHistogram, Foreground);
				break;
			case MediaScopeKind.Waveform:
				DrawDensity(drawingContext, snapshot.Waveform, MediaScopeSnapshot.WaveformColumns, MediaScopeSnapshot.WaveformLevels, Foreground, 0, ActualWidth);
				break;
			case MediaScopeKind.RgbParade:
				var third = ActualWidth / 3;
				DrawDensity(drawingContext, snapshot.RedWaveform, MediaScopeSnapshot.WaveformColumns, MediaScopeSnapshot.WaveformLevels, Brushes.IndianRed, 0, third);
				DrawDensity(drawingContext, snapshot.GreenWaveform, MediaScopeSnapshot.WaveformColumns, MediaScopeSnapshot.WaveformLevels, Brushes.LightGreen, third, third);
				DrawDensity(drawingContext, snapshot.BlueWaveform, MediaScopeSnapshot.WaveformColumns, MediaScopeSnapshot.WaveformLevels, Brushes.CornflowerBlue, third * 2, third);
				break;
			case MediaScopeKind.Vectorscope:
				DrawDensity(drawingContext, snapshot.Vectorscope, MediaScopeSnapshot.VectorscopeSize, MediaScopeSnapshot.VectorscopeSize, Foreground, 0, ActualWidth);
				break;
		}
	}

	private void DrawHistogram(DrawingContext context, IReadOnlyList<int> bins, Brush brush)
	{
		var maximum = Math.Max(1, bins.Max());
		var width = ActualWidth / bins.Count;
		for (var i = 0; i < bins.Count; i++)
		{
			if (bins[i] == 0) continue;
			var height = ActualHeight * bins[i] / maximum;
			context.DrawRectangle(brush, null, new Rect(i * width, ActualHeight - height, Math.Max(1, width), height));
		}
	}

	private void DrawDensity(
		DrawingContext context,
		IReadOnlyList<int> bins,
		int columns,
		int rows,
		Brush brush,
		double left,
		double width)
	{
		var maximum = Math.Max(1, bins.Max());
		var cellWidth = width / columns;
		var cellHeight = ActualHeight / rows;
		for (var row = 0; row < rows; row++)
		for (var column = 0; column < columns; column++)
		{
			var count = bins[row * columns + column];
			if (count == 0) continue;
			var opacity = Math.Clamp((double)count / maximum, 0.12, 1.0);
			var densityBrush = brush.Clone();
			densityBrush.Opacity = opacity;
			densityBrush.Freeze();
			context.DrawRectangle(densityBrush, null, new Rect(left + column * cellWidth, row * cellHeight, Math.Max(1, cellWidth), Math.Max(1, cellHeight)));
		}
	}
}
