// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public class RtaimeSearchBox : RtaimeTextBox
{
	public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
		nameof(Placeholder),
		typeof(string),
		typeof(RtaimeSearchBox),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
		nameof(IconData),
		typeof(Geometry),
		typeof(RtaimeSearchBox));

	static RtaimeSearchBox()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeSearchBox), new FrameworkPropertyMetadata(typeof(RtaimeSearchBox)));
	}

	public string Placeholder
	{
		get => (string)GetValue(PlaceholderProperty);
		set => SetValue(PlaceholderProperty, value);
	}

	public Geometry? IconData
	{
		get => (Geometry?)GetValue(IconDataProperty);
		set => SetValue(IconDataProperty, value);
	}
}

public class RtaimeMediaTile : ContentControl
{
	public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(
		nameof(Thumbnail),
		typeof(ImageSource),
		typeof(RtaimeMediaTile));

	public static readonly DependencyProperty AssetNameProperty = DependencyProperty.Register(
		nameof(AssetName),
		typeof(string),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
		nameof(Duration),
		typeof(string),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty FileTypeProperty = DependencyProperty.Register(
		nameof(FileType),
		typeof(string),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty AvailabilityProperty = DependencyProperty.Register(
		nameof(Availability),
		typeof(string),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty IsOnlineProperty = DependencyProperty.Register(
		nameof(IsOnline),
		typeof(bool),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(true));

	public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
		nameof(IsSelected),
		typeof(bool),
		typeof(RtaimeMediaTile),
		new FrameworkPropertyMetadata(false));

	static RtaimeMediaTile()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeMediaTile), new FrameworkPropertyMetadata(typeof(RtaimeMediaTile)));
	}

	public ImageSource? Thumbnail
	{
		get => (ImageSource?)GetValue(ThumbnailProperty);
		set => SetValue(ThumbnailProperty, value);
	}

	public string AssetName
	{
		get => (string)GetValue(AssetNameProperty);
		set => SetValue(AssetNameProperty, value);
	}

	public string Duration
	{
		get => (string)GetValue(DurationProperty);
		set => SetValue(DurationProperty, value);
	}

	public string FileType
	{
		get => (string)GetValue(FileTypeProperty);
		set => SetValue(FileTypeProperty, value);
	}

	public string Availability
	{
		get => (string)GetValue(AvailabilityProperty);
		set => SetValue(AvailabilityProperty, value);
	}

	public bool IsOnline
	{
		get => (bool)GetValue(IsOnlineProperty);
		set => SetValue(IsOnlineProperty, value);
	}

	public bool IsSelected
	{
		get => (bool)GetValue(IsSelectedProperty);
		set => SetValue(IsSelectedProperty, value);
	}
}

public class RtaimeContextMenu : ContextMenu
{
	static RtaimeContextMenu()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeContextMenu), new FrameworkPropertyMetadata(typeof(RtaimeContextMenu)));
	}
}

public class RtaimeMenuItem : MenuItem
{
	static RtaimeMenuItem()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeMenuItem), new FrameworkPropertyMetadata(typeof(RtaimeMenuItem)));
	}
}

public class RtaimeMenuSeparator : Separator
{
	static RtaimeMenuSeparator()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeMenuSeparator), new FrameworkPropertyMetadata(typeof(RtaimeMenuSeparator)));
	}
}
