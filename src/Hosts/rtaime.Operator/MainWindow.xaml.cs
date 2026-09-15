// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using rtaime.Client;

namespace rtaime.Operator;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		var endpoint = Environment.GetEnvironmentVariable("RTAIME_CONTROL_ENDPOINT");
		if (string.IsNullOrWhiteSpace(endpoint))
			endpoint = "rtaime.v1.control.default";

		var transport = new NamedPipeOperatorControlTransport(endpoint);
		DataContext = new OperatorViewModel(new OperatorControlClient(transport));
	}

	public MainWindow(OperatorViewModel viewModel)
	{
		InitializeComponent();
		DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	}
}
