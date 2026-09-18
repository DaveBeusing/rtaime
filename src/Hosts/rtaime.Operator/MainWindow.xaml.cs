// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using rtaime.Client;
using rtaime.Control.Contracts;

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
		var client = new OperatorControlClient(controlTransport);
		var viewModel = new OperatorViewModel(
			client,
			PickGraphicsAsset,
			new DispatcherSynchronizationContext(Dispatcher));
		MediaDeck = CreateMediaDeck(viewModel, client);
		MediaDeck.SnapshotChanged += viewModel.ApplyMediaDeckSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport(monitoringEndpoint),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		InitializeComponent();
		DataContext = viewModel;
		MediaDeck.Start();
		Monitoring.Start();
		viewModel.StartAudioMetering();
		Closed += OnClosedAsync;
	}

	public MainWindow(OperatorViewModel viewModel)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		viewModel.SetGraphicsAssetPicker(PickGraphicsAsset);
		viewModel.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
		var client = viewModel.Client ?? new OperatorControlClient(new UnavailableOperatorControlTransport());
		MediaDeck = CreateMediaDeck(viewModel, client);
		MediaDeck.SnapshotChanged += viewModel.ApplyMediaDeckSnapshot;
		DemoProduction = new DemoProductionPackageController(client, viewModel, MediaDeck);
		Monitoring = new OperatorMonitoringViewModel(
			viewModel,
			new NamedPipeOperatorMonitoringTransport("rtaime.v1.runtime.default.monitor"),
			new DispatcherSynchronizationContext(Dispatcher));
		ProgramOutput = new ProgramOutputController(
			Monitoring,
			new DispatcherSynchronizationContext(Dispatcher));
		InitializeComponent();
		DataContext = viewModel;
		MediaDeck.Start();
		viewModel.StartAudioMetering();
		Closed += OnClosedAsync;
	}

	public OperatorMonitoringViewModel Monitoring { get; }
	public ProgramOutputController ProgramOutput { get; }
	public MediaDeckViewModel MediaDeck { get; }
	public DemoProductionPackageController DemoProduction { get; }
	public MediaTimelineViewModel Timeline => MediaDeck.Timeline;

	private MediaDeckViewModel CreateMediaDeck(
		OperatorViewModel viewModel,
		OperatorControlClient client) =>
		new(
			new MediaDeckController(client),
			PickLocalMediaFile,
			() => viewModel.SelectedSource?.Id,
			new DispatcherSynchronizationContext(Dispatcher));

	private static OperatorGraphicsAsset? PickGraphicsAsset()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Load graphics overlay",
			Filter = "PNG Image (*.png)|*.png",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true
			? GraphicsOverlayAssetLoader.LoadPng(dialog.FileName)
			: null;
	}

	private static string? PickLocalMediaFile()
	{
		var dialog = new OpenFileDialog
		{
			Title = "Open local media",
			Filter = "MP4 Video (*.mp4)|*.mp4",
			CheckFileExists = true,
			Multiselect = false
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private async void OnClosedAsync(object? sender, EventArgs e)
	{
		ProgramOutput.Dispose();
		await Monitoring.DisposeAsync();
		await MediaDeck.DisposeAsync();
		if (DataContext is OperatorViewModel viewModel)
			await viewModel.DisposeAsync();
	}

	private sealed class UnavailableOperatorControlTransport : IOperatorControlTransport
	{
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorStatusSnapshot>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Operator transport is not configured."));
	}
}
