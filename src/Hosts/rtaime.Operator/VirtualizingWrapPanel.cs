// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace rtaime.Operator;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
	public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
		nameof(ItemWidth),
		typeof(double),
		typeof(VirtualizingWrapPanel),
		new FrameworkPropertyMetadata(168.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

	public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
		nameof(ItemHeight),
		typeof(double),
		typeof(VirtualizingWrapPanel),
		new FrameworkPropertyMetadata(126.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

	public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
		nameof(HorizontalSpacing),
		typeof(double),
		typeof(VirtualizingWrapPanel),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

	public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
		nameof(VerticalSpacing),
		typeof(double),
		typeof(VirtualizingWrapPanel),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

	private Size _extent;
	private Size _viewport;
	private double _verticalOffset;
	private int _itemsPerRow = 1;
	private bool _generatorRetryPending;

	public double ItemWidth
	{
		get => (double)GetValue(ItemWidthProperty);
		set => SetValue(ItemWidthProperty, value);
	}

	public double ItemHeight
	{
		get => (double)GetValue(ItemHeightProperty);
		set => SetValue(ItemHeightProperty, value);
	}

	public double HorizontalSpacing
	{
		get => (double)GetValue(HorizontalSpacingProperty);
		set => SetValue(HorizontalSpacingProperty, value);
	}

	public double VerticalSpacing
	{
		get => (double)GetValue(VerticalSpacingProperty);
		set => SetValue(VerticalSpacingProperty, value);
	}

	public bool CanHorizontallyScroll { get; set; }
	public bool CanVerticallyScroll { get; set; } = true;
	public double ExtentHeight => _extent.Height;
	public double ExtentWidth => _extent.Width;
	public double HorizontalOffset => 0;
	public double VerticalOffset => _verticalOffset;
	public double ViewportHeight => _viewport.Height;
	public double ViewportWidth => _viewport.Width;
	public ScrollViewer? ScrollOwner { get; set; }

	protected override Size MeasureOverride(Size availableSize)
	{
		var owner = ItemsControl.GetItemsOwner(this);
		var itemCount = owner?.Items.Count ?? 0;
		var itemWidth = NormalizeLength(ItemWidth, 168.0);
		var itemHeight = NormalizeLength(ItemHeight, 126.0);
		var horizontalSpacing = NormalizeSpacing(HorizontalSpacing);
		var verticalSpacing = NormalizeSpacing(VerticalSpacing);
		var columnStride = itemWidth + horizontalSpacing;
		var rowStride = itemHeight + verticalSpacing;
		var viewportWidth = NormalizeViewport(availableSize.Width, (itemWidth * 3.0) + (horizontalSpacing * 2.0));
		var viewportHeight = NormalizeViewport(availableSize.Height, (itemHeight * 4.0) + (verticalSpacing * 3.0));

		_itemsPerRow = Math.Max(1, (int)Math.Floor((viewportWidth + horizontalSpacing) / columnStride));
		var rowCount = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)_itemsPerRow);
		var extentHeight = rowCount == 0 ? 0 : (rowCount * itemHeight) + ((rowCount - 1) * verticalSpacing);
		_extent = new Size(viewportWidth, extentHeight);
		_viewport = new Size(viewportWidth, viewportHeight);
		CoerceVerticalOffset();
		ScrollOwner?.InvalidateScrollInfo();

		if (itemCount == 0)
		{
			var emptyGenerator = ResolveGenerator();
			if (emptyGenerator is not null)
				CleanUpItems(emptyGenerator, 0, -1);
			return new Size(viewportWidth, 0);
		}

		var firstVisibleRow = Math.Max(0, (int)Math.Floor(_verticalOffset / rowStride));
		var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / rowStride) + 1);
		var firstIndex = Math.Min(itemCount - 1, firstVisibleRow * _itemsPerRow);
		var lastIndex = Math.Min(itemCount - 1, ((firstVisibleRow + visibleRowCount) * _itemsPerRow) - 1);

		var generator = ResolveGenerator();
		if (generator is null)
		{
			QueueGeneratorRetry();
			return new Size(viewportWidth, Math.Min(_extent.Height, viewportHeight));
		}

		RealizeItems(generator, firstIndex, lastIndex, itemWidth, itemHeight);
		CleanUpItems(generator, firstIndex, lastIndex);

		return new Size(viewportWidth, Math.Min(_extent.Height, viewportHeight));
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var itemWidth = NormalizeLength(ItemWidth, 168.0);
		var itemHeight = NormalizeLength(ItemHeight, 126.0);
		var horizontalSpacing = NormalizeSpacing(HorizontalSpacing);
		var verticalSpacing = NormalizeSpacing(VerticalSpacing);
		var generator = ResolveGenerator();
		if (generator is null)
			return finalSize;

		for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
		{
			var child = InternalChildren[childIndex];
			var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
			if (itemIndex < 0)
				continue;

			var row = itemIndex / _itemsPerRow;
			var column = itemIndex % _itemsPerRow;
			var x = column * (itemWidth + horizontalSpacing);
			var y = (row * (itemHeight + verticalSpacing)) - _verticalOffset;
			child.Arrange(new Rect(x, y, itemWidth, itemHeight));
		}

		return finalSize;
	}

	public void LineDown() => SetVerticalOffset(VerticalOffset + NormalizeLength(ItemHeight, 126.0) + NormalizeSpacing(VerticalSpacing));
	public void LineUp() => SetVerticalOffset(VerticalOffset - NormalizeLength(ItemHeight, 126.0) - NormalizeSpacing(VerticalSpacing));
	public void LineLeft() { }
	public void LineRight() { }
	public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ((NormalizeLength(ItemHeight, 126.0) + NormalizeSpacing(VerticalSpacing)) * 3.0));
	public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ((NormalizeLength(ItemHeight, 126.0) + NormalizeSpacing(VerticalSpacing)) * 3.0));
	public void MouseWheelLeft() { }
	public void MouseWheelRight() { }
	public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
	public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
	public void PageLeft() { }
	public void PageRight() { }
	public void SetHorizontalOffset(double offset) { }

	public void SetVerticalOffset(double offset)
	{
		var normalized = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
		if (Math.Abs(normalized - _verticalOffset) < 0.5)
			return;

		_verticalOffset = normalized;
		InvalidateMeasure();
		ScrollOwner?.InvalidateScrollInfo();
	}

	public Rect MakeVisible(Visual visual, Rect rectangle)
	{
		if (visual is null)
			return Rect.Empty;

		DependencyObject? current = visual;
		while (current is not null && VisualTreeHelper.GetParent(current) != this)
			current = VisualTreeHelper.GetParent(current);

		if (current is not UIElement container)
			return rectangle;

		var childIndex = InternalChildren.IndexOf(container);
		if (childIndex < 0)
			return rectangle;

		var generator = ResolveGenerator();
		if (generator is null)
			return rectangle;

		var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
		if (itemIndex < 0)
			return rectangle;

		var itemHeight = NormalizeLength(ItemHeight, 126.0);
		var verticalSpacing = NormalizeSpacing(VerticalSpacing);
		var row = itemIndex / _itemsPerRow;
		var top = row * (itemHeight + verticalSpacing);
		var bottom = top + itemHeight;
		if (top < VerticalOffset)
			SetVerticalOffset(top);
		else if (bottom > VerticalOffset + ViewportHeight)
			SetVerticalOffset(bottom - ViewportHeight);

		return new Rect(0, top - VerticalOffset, NormalizeLength(ItemWidth, 168.0), itemHeight);
	}

	private void RealizeItems(
		IItemContainerGenerator generator,
		int firstIndex,
		int lastIndex,
		double itemWidth,
		double itemHeight)
	{
		var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
		var childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

		using (generator.StartAt(startPosition, GeneratorDirection.Forward, true))
		{
			for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
			{
				var child = generator.GenerateNext(out var newlyRealized) as UIElement;
				if (child is null)
					continue;

				if (newlyRealized)
				InsertInternalChild(Math.Min(childIndex, InternalChildren.Count), child);

				generator.PrepareItemContainer(child);
				child.Measure(new Size(itemWidth, itemHeight));
			}
		}
	}

	private void CleanUpItems(IItemContainerGenerator generator, int firstIndex, int lastIndex)
	{
		for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
		{
			var position = new GeneratorPosition(childIndex, 0);
			var itemIndex = generator.IndexFromGeneratorPosition(position);
			if (itemIndex < 0)
			{
				RemoveInternalChildRange(childIndex, 1);
				continue;
			}
			if (itemIndex >= firstIndex && itemIndex <= lastIndex)
				continue;

			if (generator is IRecyclingItemContainerGenerator recyclingGenerator)
				recyclingGenerator.Recycle(position, 1);
			else
				generator.Remove(position, 1);
			RemoveInternalChildRange(childIndex, 1);
		}
	}

	private IItemContainerGenerator? ResolveGenerator() => ItemContainerGenerator;

	private void QueueGeneratorRetry()
	{
		if (_generatorRetryPending)
			return;

		_generatorRetryPending = true;
		Dispatcher.BeginInvoke(
			DispatcherPriority.Loaded,
			new Action(() =>
			{
				_generatorRetryPending = false;
				InvalidateMeasure();
			}));
	}

	private void CoerceVerticalOffset()
	{
		var maximum = Math.Max(0, ExtentHeight - ViewportHeight);
		_verticalOffset = Math.Clamp(_verticalOffset, 0, maximum);
	}

	private static double NormalizeLength(double value, double fallback) =>
		double.IsFinite(value) && value > 0 ? value : fallback;

	private static double NormalizeSpacing(double value) =>
		double.IsFinite(value) && value >= 0 ? value : 0;

	private static double NormalizeViewport(double value, double fallback) =>
		double.IsFinite(value) && value > 0 ? value : fallback;
}
