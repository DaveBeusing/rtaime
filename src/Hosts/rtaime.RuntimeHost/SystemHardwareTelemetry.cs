// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace rtaime.RuntimeHost;

internal sealed record SystemHardwareTelemetrySnapshot(
	string CpuDeviceName,
	int CpuLogicalProcessorCount,
	double? CpuUtilizationPercent,
	ulong? SystemMemoryUsedBytes,
	ulong? SystemMemoryTotalBytes,
	string SystemTelemetryEvidence,
	string? GpuDeviceName,
	double? GpuUtilizationPercent,
	ulong? GpuVramUsedBytes,
	ulong? GpuVramTotalBytes,
	string GpuTelemetryEvidence);

/// <summary>
/// Bounded management-plane hardware telemetry for the Windows V1 reference runtime.
/// Sampling is cached and never runs on the Program frame hot path.
/// </summary>
internal sealed class SystemHardwareTelemetry : IDisposable
{
	private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

	private readonly object _gate = new();
	private readonly WindowsCpuSampler _cpuSampler = new();
	private readonly NvidiaGpuTelemetry _gpuTelemetry = new();
	private readonly string _cpuDeviceName = DetectCpuDeviceName();
	private DateTimeOffset _lastSampleAtUtc;
	private SystemHardwareTelemetrySnapshot? _cached;
	private bool _disposed;

	public SystemHardwareTelemetrySnapshot Sample()
	{
		lock (_gate)
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(SystemHardwareTelemetry));

			var now = DateTimeOffset.UtcNow;
			if (_cached is not null && now - _lastSampleAtUtc < SampleInterval)
				return _cached;

			var cpu = _cpuSampler.Sample();
			var memoryAvailable = TryReadMemory(out var memoryUsedBytes, out var memoryTotalBytes);
			var gpu = _gpuTelemetry.Sample();

			var systemEvidence = !OperatingSystem.IsWindows()
				? "UNVERIFIED: system hardware telemetry is currently qualified for Windows only."
				: !memoryAvailable
					? "UNVERIFIED: Windows system-memory telemetry could not be read."
					: cpu is null
						? "UNVERIFIED: CPU utilization sampler is warming up."
						: "PASS: Windows CPU and system-memory telemetry is available.";

			_cached = new SystemHardwareTelemetrySnapshot(
				_cpuDeviceName,
				Environment.ProcessorCount,
				cpu,
				memoryAvailable ? memoryUsedBytes : null,
				memoryAvailable ? memoryTotalBytes : null,
				systemEvidence,
				gpu.DeviceName,
				gpu.UtilizationPercent,
				gpu.VramUsedBytes,
				gpu.VramTotalBytes,
				gpu.Evidence);
			_lastSampleAtUtc = now;
			return _cached;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			_gpuTelemetry.Dispose();
		}
	}

	private static string DetectCpuDeviceName()
	{
		if (OperatingSystem.IsWindows())
		{
			try
			{
				using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
				if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
					return name.Trim();
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
			{
				// Fall through to the environment-provided identifier.
			}
		}

		var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
		return string.IsNullOrWhiteSpace(identifier)
			? $"{RuntimeInformation.ProcessArchitecture} CPU"
			: identifier.Trim();
	}

	private static bool TryReadMemory(out ulong usedBytes, out ulong totalBytes)
	{
		usedBytes = 0;
		totalBytes = 0;
		if (!OperatingSystem.IsWindows())
			return false;

		var status = new MemoryStatusEx
		{
			Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
		};
		if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
			return false;

		totalBytes = status.TotalPhysical;
		usedBytes = status.TotalPhysical >= status.AvailablePhysical
			? status.TotalPhysical - status.AvailablePhysical
			: 0;
		return true;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhysical;
		public ulong AvailablePhysical;
		public ulong TotalPageFile;
		public ulong AvailablePageFile;
		public ulong TotalVirtual;
		public ulong AvailableVirtual;
		public ulong AvailableExtendedVirtual;
	}

	private sealed class WindowsCpuSampler
	{
		private CpuTimes? _previous;

		public double? Sample()
		{
			if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user))
				return null;

			var current = new CpuTimes(idle.Value, kernel.Value, user.Value);
			var previous = _previous;
			_previous = current;
			if (previous is null)
				return null;

			var idleDelta = current.Idle - previous.Value.Idle;
			var kernelDelta = current.Kernel - previous.Value.Kernel;
			var userDelta = current.User - previous.Value.User;
			var total = kernelDelta + userDelta;
			if (total == 0 || idleDelta > total)
				return null;

			var busy = total - idleDelta;
			return Math.Clamp(busy * 100d / total, 0d, 100d);
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

		private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

		[StructLayout(LayoutKind.Sequential)]
		private readonly struct FileTime
		{
			private readonly uint _low;
			private readonly uint _high;

			public ulong Value => ((ulong)_high << 32) | _low;
		}
	}

	private sealed class NvidiaGpuTelemetry : IDisposable
	{
		private const int NvmlSuccess = 0;
		private const uint DeviceNameBufferSize = 96;
		private IntPtr _device;
		private bool _initialized;
		private string? _deviceName;
		private string _evidence = "UNVERIFIED: NVIDIA NVML telemetry is unavailable.";

		public NvidiaGpuTelemetry()
		{
			if (!OperatingSystem.IsWindows())
			{
				_evidence = "UNVERIFIED: GPU telemetry is currently qualified for Windows NVIDIA drivers only.";
				return;
			}

			try
			{
				if (NvmlInit() != NvmlSuccess)
				return;

				_initialized = true;
				if (NvmlDeviceGetCount(out var count) != NvmlSuccess || count == 0)
				return;

				ulong largestMemory = 0;
				for (uint index = 0; index < count; index++)
				{
					if (NvmlDeviceGetHandleByIndex(index, out var candidate) != NvmlSuccess)
						continue;

					var total = NvmlDeviceGetMemoryInfo(candidate, out var memory) == NvmlSuccess
						? memory.Total
						: 0;
					if (_device == IntPtr.Zero || total > largestMemory)
					{
						_device = candidate;
						largestMemory = total;
					}
				}

				if (_device == IntPtr.Zero)
					return;

				_deviceName = ReadDeviceName(_device);
				_evidence = "PASS: NVIDIA NVML utilization and VRAM telemetry is available.";
			}
			catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				_evidence = "UNVERIFIED: NVIDIA NVML is not available from the installed driver.";
			}
		}

		public GpuSample Sample()
		{
			if (!_initialized || _device == IntPtr.Zero)
				return new GpuSample(_deviceName, null, null, null, _evidence);

			try
			{
				double? utilization = null;
				ulong? used = null;
				ulong? total = null;

				if (NvmlDeviceGetUtilizationRates(_device, out var rates) == NvmlSuccess)
					utilization = Math.Clamp(rates.Gpu, 0u, 100u);
				if (NvmlDeviceGetMemoryInfo(_device, out var memory) == NvmlSuccess)
				{
					used = memory.Used;
					total = memory.Total;
				}

				var evidence = utilization is not null || used is not null
					? "PASS: NVIDIA NVML utilization and VRAM telemetry is available."
					: "UNVERIFIED: NVIDIA NVML did not return utilization or VRAM samples.";
				return new GpuSample(_deviceName, utilization, used, total, evidence);
			}
			catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				return new GpuSample(_deviceName, null, null, null, "UNVERIFIED: NVIDIA NVML telemetry became unavailable.");
			}
		}

		public void Dispose()
		{
			if (!_initialized)
				return;

			try
			{
				NvmlShutdown();
			}
			catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				// Process teardown remains best-effort if the driver disappears.
			}
			_initialized = false;
		}

		private static string? ReadDeviceName(IntPtr device)
		{
			var buffer = new byte[DeviceNameBufferSize];
			if (NvmlDeviceGetName(device, buffer, checked((uint)buffer.Length)) != NvmlSuccess)
				return null;

			var length = Array.IndexOf(buffer, (byte)0);
			if (length < 0)
				length = buffer.Length;
			return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length).Trim();
		}

		[DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlInit();

		[DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlShutdown();

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlDeviceGetCount(out uint deviceCount);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

		[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = CallingConvention.Cdecl)]
		private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

		[StructLayout(LayoutKind.Sequential)]
		private readonly struct NvmlUtilization
		{
			public readonly uint Gpu;
			public readonly uint Memory;
		}

		[StructLayout(LayoutKind.Sequential)]
		private readonly struct NvmlMemory
		{
			public readonly ulong Total;
			public readonly ulong Free;
			public readonly ulong Used;
		}

		public sealed record GpuSample(
			string? DeviceName,
			double? UtilizationPercent,
			ulong? VramUsedBytes,
			ulong? VramTotalBytes,
			string Evidence);
	}
}
