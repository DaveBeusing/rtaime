// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Threading;
using rtaime.Client;

namespace rtaime.Operator;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		var controlEndpoint = Environment.GetEnvironmentVariable("RTAIME_CONTROL_ENDPOINT");
		if (string.IsNullOrWhiteSpace(controlEndpoint))
			controlEndpoint = "rtaime.v1.control.default";

		var runtimeEndpoint = Environment.GetEnvironmentVariable("RTAIME_RUNTIME_ENDPOINT");
		if (string.IsNullOrWhiteSpace(runtimeEndpoint))
			runtimeEndpoint = "rtaime.v1.runtime.default";
		var monitoringEndpoint = Environment.GetEnvironmentVariable("RTAIME_MONITOR_ENDPOINT");
		if (string.IsNullOrWhiteSpace(monitoringEndpoint))
			monitoringEndpoint = $"{runtimeEndpoint}.monitor";

		var controlTransport = new NamedPipeOperatorControlTransport(controlEndpoint);
		var viewModel = new OperatorViewModel(new OperatorControlClient(controlTransport));
		Timeline = new MediaTimelineViewModel();
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport(monitoringEndpoint),
			new DispatcherSynchronizationContext(Dispatcher));
		InitializeComponent();
		DataContext = viewModel;
		Monitoring.Start();
		Closed += async (_, _) =>
		{
			await Monitoring.DisposeAsync();
			await Timeline.DisposeAsync();
		};
	}

	public MainWindow(OperatorViewModel viewModel)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		Timeline = new MediaTimelineViewModel();
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport("rtaime.v1.runtime.default.monitor"),
			new DispatcherSynchronizationContext(Dispatcher));
		InitializeComponent();
		DataContext = viewModel;
		Closed += async (_, _) =>
		{
			await Monitoring.DisposeAsync();
			await Timeline.DisposeAsync();
		};
	}

	public OperatorMonitoringViewModel Monitoring { get; }
	public MediaTimelineViewModel Timeline { get; }
}
