// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace rtaime.IntegrationHost;

public enum MidiMessageKind
{
	NoteOff = 1,
	NoteOn = 2,
	ControlChange = 3,
	ProgramChange = 4
}

public sealed record MidiMessage(MidiMessageKind Kind, int Channel, int Data1, int Data2)
{
	public MidiMessage
	{
		if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
		if (Channel is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(Channel));
		if (Data1 is < 0 or > 127 || Data2 is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(Data1));
	}
}

public interface IMidiBackend : IAsyncDisposable
{
	event Action<MidiMessage>? MessageReceived;
	string State { get; }
	ValueTask StartAsync(CancellationToken cancellationToken);
	ValueTask SendAsync(MidiMessage message, CancellationToken cancellationToken);
	ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed class VirtualMidiBackend : IMidiBackend
{
	private readonly ConcurrentQueue<MidiMessage> _sent = new();
	public event Action<MidiMessage>? MessageReceived;
	public string State { get; private set; } = "STOPPED";
	public IReadOnlyList<MidiMessage> SentMessages => _sent.ToArray();

	public ValueTask StartAsync(CancellationToken cancellationToken)
	{
		State = "HEALTHY";
		return ValueTask.CompletedTask;
	}

	public void Inject(MidiMessage message)
	{
		if (State != "HEALTHY") throw new InvalidOperationException("Virtual MIDI backend is not active.");
		MessageReceived?.Invoke(message);
	}

	public ValueTask SendAsync(MidiMessage message, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_sent.Enqueue(message);
		return ValueTask.CompletedTask;
	}

	public ValueTask StopAsync(CancellationToken cancellationToken)
	{
		State = "STOPPED";
		return ValueTask.CompletedTask;
	}

	public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
}

public sealed class WinMmMidiBackend : IMidiBackend
{
	private const uint CallbackFunction = 0x00030000;
	private const uint MimData = 0x3C3;
	private const uint MimClose = 0x3C2;
	private readonly object _gate = new();
	private readonly MidiAdapterOptions _options;
	private readonly MidiInProc _callback;
	private CancellationTokenSource? _lifetime;
	private Task? _reconnectTask;
	private IntPtr _inputHandle;
	private IntPtr _outputHandle;
	private uint? _inputDeviceId;
	private uint? _outputDeviceId;

	public WinMmMidiBackend(MidiAdapterOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_callback = OnMidiInput;
	}

	public event Action<MidiMessage>? MessageReceived;
	public string State { get; private set; } = "STOPPED";

	public ValueTask StartAsync(CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Windows MIDI backend requires Windows.");
		if (_lifetime is not null) return ValueTask.CompletedTask;
		State = "STARTING";
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_reconnectTask = Task.Run(() => ReconnectLoopAsync(_lifetime.Token), CancellationToken.None);
		return ValueTask.CompletedTask;
	}

	public ValueTask SendAsync(MidiMessage message, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IntPtr output;
		lock (_gate) output = _outputHandle;
		if (output == IntPtr.Zero)
			throw new InvalidOperationException("MIDI output device is unavailable.");
		var packed = Pack(message);
		var result = midiOutShortMsg(output, packed);
		if (result != 0)
		{
			lock (_gate)
			{
				if (_outputHandle != IntPtr.Zero)
				{
					midiOutClose(_outputHandle);
					_outputHandle = IntPtr.Zero;
				}
			}
			throw new IOException($"Windows MIDI output failed with MMRESULT {result}.");
		}
		return ValueTask.CompletedTask;
	}

	public async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		var lifetime = Interlocked.Exchange(ref _lifetime, null);
		if (lifetime is null) return;
		lifetime.Cancel();
		if (_reconnectTask is not null)
		{
			try { await _reconnectTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_reconnectTask = null;
		lock (_gate)
		{
			CloseInput();
			CloseOutput();
		}
		lifetime.Dispose();
		State = "STOPPED";
	}

	private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				lock (_gate)
				{
					if (_inputHandle == IntPtr.Zero)
						TryOpenInput();
					if (_outputHandle == IntPtr.Zero)
						TryOpenOutput();
					State = (_inputHandle != IntPtr.Zero || string.IsNullOrWhiteSpace(_options.InputDevice)) &&
						(_outputHandle != IntPtr.Zero || string.IsNullOrWhiteSpace(_options.OutputDevice))
							? "HEALTHY"
							: "DEGRADED";
				}
				await Task.Delay(_options.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private void TryOpenInput()
	{
		if (string.IsNullOrWhiteSpace(_options.InputDevice)) return;
		_inputDeviceId ??= FindInputDevice(_options.InputDevice);
		if (_inputDeviceId is null) return;
		if (midiInOpen(out var handle, _inputDeviceId.Value, _callback, UIntPtr.Zero, CallbackFunction) != 0)
			return;
		if (midiInStart(handle) != 0)
		{
			midiInClose(handle);
			return;
		}
		_inputHandle = handle;
	}

	private void TryOpenOutput()
	{
		if (string.IsNullOrWhiteSpace(_options.OutputDevice)) return;
		_outputDeviceId ??= FindOutputDevice(_options.OutputDevice);
		if (_outputDeviceId is null) return;
		if (midiOutOpen(out var handle, _outputDeviceId.Value, UIntPtr.Zero, UIntPtr.Zero, 0) == 0)
			_outputHandle = handle;
	}

	private void OnMidiInput(IntPtr handle, uint message, UIntPtr instance, UIntPtr param1, UIntPtr param2)
	{
		if (message == MimClose)
		{
			lock (_gate)
				if (_inputHandle == handle) _inputHandle = IntPtr.Zero;
			State = "DEGRADED";
			return;
		}
		if (message != MimData) return;
		var packed = unchecked((uint)param1.ToUInt64());
		if (TryUnpack(packed, out var midi))
			MessageReceived?.Invoke(midi!);
	}

	private static bool TryUnpack(uint packed, out MidiMessage? message)
	{
		message = null;
		var status = (byte)(packed & 0xFF);
		var command = status & 0xF0;
		var channel = (status & 0x0F) + 1;
		var data1 = (int)((packed >> 8) & 0x7F);
		var data2 = (int)((packed >> 16) & 0x7F);
		message = command switch
		{
			0x80 => new MidiMessage(MidiMessageKind.NoteOff, channel, data1, data2),
			0x90 when data2 == 0 => new MidiMessage(MidiMessageKind.NoteOff, channel, data1, 0),
			0x90 => new MidiMessage(MidiMessageKind.NoteOn, channel, data1, data2),
			0xB0 => new MidiMessage(MidiMessageKind.ControlChange, channel, data1, data2),
			0xC0 => new MidiMessage(MidiMessageKind.ProgramChange, channel, data1, 0),
			_ => null
		};
		return message is not null;
	}

	private static uint Pack(MidiMessage message)
	{
		var status = message.Kind switch
		{
			MidiMessageKind.NoteOff => 0x80,
			MidiMessageKind.NoteOn => 0x90,
			MidiMessageKind.ControlChange => 0xB0,
			MidiMessageKind.ProgramChange => 0xC0,
			_ => throw new ArgumentOutOfRangeException(nameof(message))
		};
		status |= message.Channel - 1;
		return (uint)(status | (message.Data1 << 8) | (message.Data2 << 16));
	}

	private static uint? FindInputDevice(string identity)
	{
		if (uint.TryParse(identity, out var explicitId) && explicitId < midiInGetNumDevs())
			return explicitId;
		for (uint id = 0; id < midiInGetNumDevs(); id++)
		{
			if (midiInGetDevCaps((UIntPtr)id, out var caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0 &&
				string.Equals(caps.Name, identity, StringComparison.OrdinalIgnoreCase))
				return id;
		}
		return null;
	}

	private static uint? FindOutputDevice(string identity)
	{
		if (uint.TryParse(identity, out var explicitId) && explicitId < midiOutGetNumDevs())
			return explicitId;
		for (uint id = 0; id < midiOutGetNumDevs(); id++)
		{
			if (midiOutGetDevCaps((UIntPtr)id, out var caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0 &&
				string.Equals(caps.Name, identity, StringComparison.OrdinalIgnoreCase))
				return id;
		}
		return null;
	}

	private void CloseInput()
	{
		if (_inputHandle == IntPtr.Zero) return;
		midiInStop(_inputHandle);
		midiInReset(_inputHandle);
		midiInClose(_inputHandle);
		_inputHandle = IntPtr.Zero;
	}

	private void CloseOutput()
	{
		if (_outputHandle == IntPtr.Zero) return;
		midiOutReset(_outputHandle);
		midiOutClose(_outputHandle);
		_outputHandle = IntPtr.Zero;
	}

	public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate void MidiInProc(IntPtr midiIn, uint message, UIntPtr instance, UIntPtr param1, UIntPtr param2);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MidiInCaps
	{
		public ushort ManufacturerId;
		public ushort ProductId;
		public uint DriverVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string Name;
		public uint Support;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MidiOutCaps
	{
		public ushort ManufacturerId;
		public ushort ProductId;
		public uint DriverVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string Name;
		public ushort Technology;
		public ushort Voices;
		public ushort Notes;
		public ushort ChannelMask;
		public uint Support;
	}

	[DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
	[DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCaps(UIntPtr deviceId, out MidiInCaps caps, uint size);
	[DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr handle, uint deviceId, MidiInProc callback, UIntPtr instance, uint flags);
	[DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr handle);
	[DllImport("winmm.dll")] private static extern uint midiInStop(IntPtr handle);
	[DllImport("winmm.dll")] private static extern uint midiInReset(IntPtr handle);
	[DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr handle);
	[DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
	[DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetDevCaps(UIntPtr deviceId, out MidiOutCaps caps, uint size);
	[DllImport("winmm.dll")] private static extern uint midiOutOpen(out IntPtr handle, uint deviceId, UIntPtr callback, UIntPtr instance, uint flags);
	[DllImport("winmm.dll")] private static extern uint midiOutShortMsg(IntPtr handle, uint message);
	[DllImport("winmm.dll")] private static extern uint midiOutReset(IntPtr handle);
	[DllImport("winmm.dll")] private static extern uint midiOutClose(IntPtr handle);
}

public sealed class MidiIntegrationAdapter : IntegrationAdapterBase
{
	private readonly IReadOnlyList<IntegrationFeedbackMappingOptions> _feedbackMappings;
	private readonly IMidiBackend _backend;
	private IntegrationInputSink? _input;

	public MidiIntegrationAdapter(
		IntegrationAdapterOptions adapter,
		IReadOnlyList<IntegrationFeedbackMappingOptions> feedbackMappings,
		IMidiBackend backend)
		: base(adapter.Id, IntegrationAdapterKind.Midi, adapter.Required)
	{
		_feedbackMappings = feedbackMappings ?? throw new ArgumentNullException(nameof(feedbackMappings));
		_backend = backend ?? throw new ArgumentNullException(nameof(backend));
	}

	public override async ValueTask StartAsync(IntegrationInputSink input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		_input = input;
		_backend.MessageReceived += OnMessage;
		SetHealth(IntegrationAdapterState.Starting, "Starting MIDI backend.");
		try
		{
			await _backend.StartAsync(cancellationToken).ConfigureAwait(false);
			SetHealth(
				string.Equals(_backend.State, "HEALTHY", StringComparison.Ordinal)
					? IntegrationAdapterState.Healthy
					: IntegrationAdapterState.Degraded,
				$"MIDI backend state: {_backend.State}.");
		}
		catch (Exception exception)
		{
			_backend.MessageReceived -= OnMessage;
			SetHealth(IntegrationAdapterState.Failed, $"MIDI backend failed: {exception.Message}");
			throw;
		}
	}

	public override async ValueTask EmitFeedbackAsync(IntegrationFeedbackSnapshot snapshot, CancellationToken cancellationToken)
	{
		foreach (var mapping in _feedbackMappings)
		{
			if (!TryParseTarget(mapping.TargetKey, out var kind, out var channel, out var data1))
				continue;
			var value = snapshot.Resolve(mapping);
			var normalized = ParseNormalized(value);
			var data2 = checked((int)Math.Round(normalized * 127, MidpointRounding.AwayFromZero));
			await _backend.SendAsync(new MidiMessage(kind, channel, data1, data2), cancellationToken).ConfigureAwait(false);
		}
	}

	public override async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		_backend.MessageReceived -= OnMessage;
		await _backend.StopAsync(cancellationToken).ConfigureAwait(false);
		_input = null;
		SetHealth(IntegrationAdapterState.Stopped, "MIDI adapter is stopped.");
	}

	private void OnMessage(MidiMessage message)
	{
		var key = message.Kind switch
		{
			MidiMessageKind.NoteOn => $"note:{message.Channel}:{message.Data1}",
			MidiMessageKind.NoteOff => $"note:{message.Channel}:{message.Data1}",
			MidiMessageKind.ControlChange => $"cc:{message.Channel}:{message.Data1}",
			MidiMessageKind.ProgramChange => $"program:{message.Channel}:{message.Data1}",
			_ => string.Empty
		};
		var numeric = message.Kind switch
		{
			MidiMessageKind.NoteOff => 0d,
			MidiMessageKind.ProgramChange => message.Data1 / 127d,
			_ => message.Data2 / 127d
		};
		if (!(_input?.Invoke(new IntegrationTrigger(Id, key, NumericValue: numeric, BooleanValue: numeric > 0)) ?? false))
			IncrementDroppedInputs("MIDI input could not enter the bounded gateway queue.");
	}

	private static bool TryParseTarget(string value, out MidiMessageKind kind, out int channel, out int data1)
	{
		kind = MidiMessageKind.ControlChange;
		channel = 0;
		data1 = 0;
		var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 3 || !int.TryParse(parts[1], out channel) || !int.TryParse(parts[2], out data1))
			return false;
		kind = parts[0].ToLowerInvariant() switch
		{
			"note" => MidiMessageKind.NoteOn,
			"cc" => MidiMessageKind.ControlChange,
			_ => 0
		};
		return kind != 0 && channel is >= 1 and <= 16 && data1 is >= 0 and <= 127;
	}

	private static double ParseNormalized(string value)
	{
		if (bool.TryParse(value, out var boolean)) return boolean ? 1 : 0;
		if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
			return Math.Clamp(number, 0, 1);
		return string.IsNullOrWhiteSpace(value) ||
			value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
			value.Equals("STOPPED", StringComparison.OrdinalIgnoreCase) ||
			value.Equals("UNVERIFIED", StringComparison.OrdinalIgnoreCase)
				? 0
				: 1;
	}

	public override async ValueTask DisposeAsync()
	{
		await base.DisposeAsync().ConfigureAwait(false);
		await _backend.DisposeAsync().ConfigureAwait(false);
	}
}
