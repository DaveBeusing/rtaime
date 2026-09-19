// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Controls;
using System.Windows.Data;

namespace rtaime.Operator;

public partial class LiveSceneCueControl : UserControl
{
	public LiveSceneCueControl()
	{
		InitializeComponent();
	}

	private void SceneSearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (FindResource("LiveSceneSourceView") is CollectionViewSource viewSource)
			viewSource.View?.Refresh();
	}

	private void LiveSceneSourceView_Filter(object sender, FilterEventArgs e)
	{
		if (e.Item is not OperatorSourceTileViewModel source)
		{
			e.Accepted = false;
			return;
		}

		var query = SceneSearchBox?.Text?.Trim();
		if (string.IsNullOrWhiteSpace(query))
		{
			e.Accepted = true;
			return;
		}

		e.Accepted =
			source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
			source.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
			source.Format.Contains(query, StringComparison.OrdinalIgnoreCase);
	}

	private void SceneOverflow_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: OperatorSourceTileViewModel source } &&
			DataContext is OperatorViewModel viewModel)
		{
			viewModel.SelectedSource = source;
		}
	}
}