// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public partial class OperatorMultiviewControl : UserControl, INotifyPropertyChanged
{
	private const int MaximumDisplayedSources = 16;
	private INotifyCollectionChanged? _sourceNotifications;
	private int _sourceGridColumns = 3;
	private string _sourceGridPreset = "3×3";
	private string _displayedSourceSummary = "0 sources";

	public static readonly DependencyProperty PreviewImageProperty = DependencyProperty.Register(
		nameof(PreviewImage),
		typeof(ImageSource),
		typeof(OperatorMultiviewControl));

	public static readonly DependencyProperty ProgramImageProperty = DependencyProperty.Register(
		nameof(ProgramImage),
		typeof(ImageSource),
		typeof(OperatorMultiviewControl));

	public static readonly DependencyProperty PreviewStateProperty = DependencyProperty.Register(
		nameof(PreviewState),
		typeof(string),
		typeof(OperatorMultiviewControl),
		new PropertyMetadata("NO SIGNAL"));

	public static readonly DependencyProperty ProgramStateProperty = DependencyProperty.Register(
		nameof(ProgramState),
		typeof(string),
		typeof(OperatorMultiviewControl),
		new PropertyMetadata("NO SIGNAL"));

	public static readonly DependencyProperty SourcesProperty = DependencyProperty.Register(
		nameof(Sources),
		typeof(IEnumerable),
		typeof(OperatorMultiviewControl),
		new PropertyMetadata(null, SourcesChanged));

	public static readonly DependencyProperty SelectedSourceProperty = DependencyProperty.Register(
		nameof(SelectedSource),
		typeof(object),
		typeof(OperatorMultiviewControl),
		new FrameworkPropertyMetadata(
			null,
			FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	public OperatorMultiviewControl()
	{
		DisplayedSources = [];
		InitializeComponent();
		IsVisibleChanged += OnVisibilityChanged;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<object> DisplayedSources { get; }

	public ImageSource? PreviewImage
	{
		get => (ImageSource?)GetValue(PreviewImageProperty);
		set => SetValue(PreviewImageProperty, value);
	}

	public ImageSource? ProgramImage
	{
		get => (ImageSource?)GetValue(ProgramImageProperty);
		set => SetValue(ProgramImageProperty, value);
	}

	public string PreviewState
	{
		get => (string)GetValue(PreviewStateProperty);
		set => SetValue(PreviewStateProperty, value);
	}

	public string ProgramState
	{
		get => (string)GetValue(ProgramStateProperty);
		set => SetValue(ProgramStateProperty, value);
	}

	public IEnumerable? Sources
	{
		get => (IEnumerable?)GetValue(SourcesProperty);
		set => SetValue(SourcesProperty, value);
	}

	public object? SelectedSource
	{
		get => GetValue(SelectedSourceProperty);
		set => SetValue(SelectedSourceProperty, value);
	}

	public int SourceGridColumns
	{
		get => _sourceGridColumns;
		private set => Set(ref _sourceGridColumns, value);
	}

	public string SourceGridPreset
	{
		get => _sourceGridPreset;
		private set => Set(ref _sourceGridPreset, value);
	}

	public string DisplayedSourceSummary
	{
		get => _displayedSourceSummary;
		private set => Set(ref _displayedSourceSummary, value);
	}

	private static void SourcesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		var control = (OperatorMultiviewControl)dependencyObject;
		control.AttachSources(args.OldValue as IEnumerable, args.NewValue as IEnumerable);
	}

	private void AttachSources(IEnumerable? oldSources, IEnumerable? newSources)
	{
		if (_sourceNotifications is not null)
			_sourceNotifications.CollectionChanged -= SourcesCollectionChanged;

		_sourceNotifications = newSources as INotifyCollectionChanged;
		if (_sourceNotifications is not null)
			_sourceNotifications.CollectionChanged += SourcesCollectionChanged;

		RefreshDisplayedSources();
	}

	private void SourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
		RefreshDisplayedSources();

	private void RefreshDisplayedSources()
	{
		if (!IsVisible && DisplayedSources.Count > 0)
			return;

		var sources = Sources?.Cast<object>().ToArray() ?? [];
		var capacity = Math.Min(MaximumDisplayedSources, SourceGridColumns * SourceGridColumns);
		var displayed = sources.Take(capacity).ToArray();

		if (!DisplayedSources.SequenceEqual(displayed))
		{
			DisplayedSources.Clear();
			foreach (var source in displayed)
				DisplayedSources.Add(source);
		}

		DisplayedSourceSummary = sources.Length > capacity
			? $"{displayed.Length} of {sources.Length} sources"
			: $"{displayed.Length} source{(displayed.Length == 1 ? string.Empty : "s")}";
	}

	private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is true)
			RefreshDisplayedSources();
	}

	private void Grid2x2_Click(object sender, RoutedEventArgs e) => SetGridPreset(2);

	private void Grid3x3_Click(object sender, RoutedEventArgs e) => SetGridPreset(3);

	private void Grid4x4_Click(object sender, RoutedEventArgs e) => SetGridPreset(4);

	private void SetGridPreset(int columns)
	{
		SourceGridColumns = Math.Clamp(columns, 2, 4);
		SourceGridPreset = $"{SourceGridColumns}×{SourceGridColumns}";
		RefreshDisplayedSources();
	}

	private void PreviewTile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount < 2)
			return;
		ShowExpanded("PREVIEW", PreviewImage, PreviewState);
		e.Handled = true;
	}

	private void ProgramTile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount < 2)
			return;
		ShowExpanded("PROGRAM", ProgramImage, ProgramState);
		e.Handled = true;
	}

	private void SourceTile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount < 2 ||
			sender is not FrameworkElement { DataContext: OperatorSourceTileViewModel source })
			return;

		ShowExpanded(
			source.Name,
			source.Thumbnail,
			$"{source.Format} · {source.StateDetail} · {source.Tally}");
		e.Handled = true;
	}

	private void ShowExpanded(string title, ImageSource? image, string detail)
	{
		ExpandedTitle.Text = title;
		ExpandedImage.Source = image;
		ExpandedDetail.Text = detail;
		ExpandedPreviewOverlay.Visibility = Visibility.Visible;
	}

	private void CloseExpanded_Click(object sender, RoutedEventArgs e)
	{
		ExpandedPreviewOverlay.Visibility = Visibility.Collapsed;
		ExpandedImage.Source = null;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}