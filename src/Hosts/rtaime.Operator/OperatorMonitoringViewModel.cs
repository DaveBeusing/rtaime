// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed class OperatorMonitoringViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly OperatorViewModel _controlState;
	private readonly NamedPipeOperatorMonitoringTransport _transport;
	private readonly SynchronizationContext _uiContext;
	private readonly CancellationTokenSource _stop = new();
	private readonly Dictionary<string, BitmapSource> _sourceFrames = new(StringComparer.Ordinal);
	private Task? _reader;
	private Task? _watchdog;
	private DateTimeOffset _lastFrameAt;
	private ImageSource? _previewImage;
	private ImageSource? _programImage;
	private string _state = "WAITING";
	private string _detail = "Waiting for the independent RuntimeHost monitoring plane.";
	private string _previewFormat = "No Preview monitor frame received.";
	private string _programFormat = "No Program monitor frame received.";

	public OperatorMonitoringViewModel(
		OperatorViewModel controlState,
		NamedPipeOperatorMonitoringTransport transport,
		SynchronizationContext? uiContext = null)
	{
		_controlState = controlState ?? throw new ArgumentNullException(nameof(controlState));
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
		_controlState.PropertyChanged += ControlStatePropertyChanged;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ImageSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
	public ImageSource? ProgramImage { get => _programImage; private set => Set(ref _programImage, value); }
	public string State { get => _state; private set => Set(ref _state, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string PreviewFormat { get => _previewFormat; private set => Set(ref _previewFormat, value); }
	public string ProgramFormat { get => _programFormat; private set => Set(ref _programFormat, value); }
	public bool HasPreview => PreviewImage is not null;
	public bool HasProgram => ProgramImage is not null;

	public void Start()
	{
		if (_reader is not null) return;
		_reader = Task.Run(() => ReadAsync(_stop.Token));
		_watchdog = Task.Run(() => WatchdogAsync(_stop.Token));
	}

	public async ValueTask DisposeAsync()
	{
		_controlState.PropertyChanged -= ControlStatePropertyChanged;
		_stop.Cancel();
		if (_reader is not null)
		{
			try { await _reader.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		if (_watchdog is not null)
		{
			try { await _watchdog.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_stop.Dispose();
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		await foreach (var frame in _transport.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
		{
			var bitmap = CreateBitmap(frame);
			var descriptor = frame.Descriptor;
			lock (_gate) _lastFrameAt = DateTimeOffset.UtcNow;
			_uiContext.Post(_ => ApplyFrame(descriptor, bitmap), null);
		}
	}

	private async Task WatchdogAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(500, cancellationToken).ConfigureAwait(false);
			DateTimeOffset last;
			lock (_gate) last = _lastFrameAt;
			if (last == default) continue;
			if (DateTimeOffset.UtcNow - last <= TimeSpan.FromSeconds(2)) continue;
			_uiContext.Post(_ =>
			{
				State = "STALE";
				Detail = "Monitoring frames are stale. Control and Program continuity remain independent.";
			}, null);
		}
	}

	private void ApplyFrame(MonitoringFrameDescriptor descriptor, BitmapSource bitmap)
	{
		State = "LIVE";
		Detail = $"Independent monitoring endpoint {_transport.Endpoint}; monitor loss does not block Program.";
		var format = $"{descriptor.Width}x{descriptor.Height} RGBA8 • sequence {descriptor.Timing.SequenceNumber}";
		if (descriptor.StreamKind == MonitoringStreamKind.Program)
		{
			ProgramImage = bitmap;
			ProgramFormat = format;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProgram)));
			return;
		}

		var sourceId = descriptor.SourceId.ToString();
		_sourceFrames[sourceId] = bitmap;
		_controlState.ApplySourceThumbnail(sourceId, bitmap, format);
		if (string.Equals(sourceId, _controlState.PreviewSourceId, StringComparison.Ordinal))
		{
			PreviewImage = bitmap;
			PreviewFormat = format;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview)));
		}
	}

	private void ControlStatePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (!string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.PreviewSourceId), StringComparison.Ordinal)) return;
		_uiContext.Post(_ => RefreshPreviewFromCache(), null);
	}

	private void RefreshPreviewFromCache()
	{
		if (_sourceFrames.TryGetValue(_controlState.PreviewSourceId, out var frame))
		{
			PreviewImage = frame;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview)));
		}
	}

	private static BitmapSource CreateBitmap(MonitoringFrame frame)
	{
		var descriptor = frame.Descriptor;
		var rgba = frame.Pixels.Span;
		var bgra = new byte[rgba.Length];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			bgra[offset] = rgba[offset + 2];
			bgra[offset + 1] = rgba[offset + 1];
			bgra[offset + 2] = rgba[offset];
			bgra[offset + 3] = rgba[offset + 3];
		}

		var width = checked((int)descriptor.Width);
		var height = checked((int)descriptor.Height);
		var bitmap = BitmapSource.Create(
			width,
			height,
			96,
			96,
			PixelFormats.Bgra32,
			null,
			bgra,
			checked(width * 4));
		bitmap.Freeze();
		return bitmap;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}
