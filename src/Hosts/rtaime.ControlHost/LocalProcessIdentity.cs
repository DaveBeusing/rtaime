// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace rtaime.ControlHost;

public sealed record LocalProcessIdentity(
	[property: JsonPropertyName("processId")] int ProcessId,
	[property: JsonPropertyName("creationTimeUtcTicks")] string CreationTimeUtcTicks,
	[property: JsonPropertyName("executablePath")] string ExecutablePath,
	[property: JsonPropertyName("hostPath")] string HostPath,
	[property: JsonPropertyName("launchId")] string LaunchId,
	[property: JsonPropertyName("lifecycleRunId")] string LifecycleRunId)
{
	internal static LocalProcessIdentity Capture(Process process, string hostPath, string launchId, string? lifecycleRunId)
	{
		// Retain the handle returned for this launch before inspecting its identity. Never reacquire ownership by PID.
		_ = process.SafeHandle;
		if (process.HasExited)
			throw new InvalidOperationException("The owned process exited before its identity could be captured.");
		var creationTime = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
		var executablePath = ReadExecutablePath(process);
		if (string.IsNullOrWhiteSpace(executablePath) || process.HasExited)
			throw new InvalidOperationException("The owned process image identity could not be captured before exit.");
		return new LocalProcessIdentity(
			process.Id,
			creationTime,
			Path.GetFullPath(executablePath),
			Path.GetFullPath(hostPath),
			launchId,
			lifecycleRunId ?? string.Empty);
	}

	private static string? ReadExecutablePath(Process process)
	{
		if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName;
		// Image lookup uses the retained process handle and does not wait for module-loader initialization.
		var path = new StringBuilder(32768);
		var length = (uint)path.Capacity;
		if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
			throw new Win32Exception(Marshal.GetLastWin32Error());
		return path.ToString();
	}

	[DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder imagePath, ref uint size);
}
