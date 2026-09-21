// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;

namespace rtaime.AppHost;

internal readonly record struct ApplicationConsoleBootstrapState(
	bool ConsoleAvailable,
	bool ConsoleRequested);

internal static class WindowsConsoleBootstrap
{
	private const uint AttachParentProcess = 0xFFFFFFFF;
	private const int StandardOutputHandle = -11;
	private const int StandardErrorHandle = -12;
	private const uint FileTypeDisk = 0x0001;
	private const uint FileTypePipe = 0x0003;

	public static ApplicationConsoleBootstrapState Initialize(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var explicitConsole = args.Contains("--show-console", StringComparer.OrdinalIgnoreCase);
		var windowsService = args.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
		var consoleRequested = ShouldRequestConsole(args);

		if (!OperatingSystem.IsWindows())
			return new ApplicationConsoleBootstrapState(ConsoleAvailable: true, consoleRequested);

		if (windowsService)
			return new ApplicationConsoleBootstrapState(ConsoleAvailable: false, ConsoleRequested: false);

		if (!consoleRequested)
			return new ApplicationConsoleBootstrapState(GetConsoleWindow() != IntPtr.Zero, ConsoleRequested: false);

		if (GetConsoleWindow() != IntPtr.Zero)
			return new ApplicationConsoleBootstrapState(ConsoleAvailable: true, ConsoleRequested: true);

		if (AttachConsole(AttachParentProcess))
			return new ApplicationConsoleBootstrapState(ConsoleAvailable: true, ConsoleRequested: true);

		if (!explicitConsole && StandardOutputAndErrorAreRedirected())
			return new ApplicationConsoleBootstrapState(ConsoleAvailable: false, ConsoleRequested: true);

		if (!AllocConsole())
		{
			var error = Marshal.GetLastWin32Error();
			throw new InvalidOperationException($"Unable to create the requested Windows diagnostic console. Win32 error {error}.");
		}

		return new ApplicationConsoleBootstrapState(ConsoleAvailable: true, ConsoleRequested: true);
	}

	internal static bool ShouldRequestConsole(IReadOnlyList<string> args)
	{
		if (args.Contains("--windows-service", StringComparer.OrdinalIgnoreCase))
			return false;

		return args.Contains("--show-console", StringComparer.OrdinalIgnoreCase) ||
			IsHeadlessEngine(args);
	}

	private static bool IsHeadlessEngine(IReadOnlyList<string> args)
	{
		var profileArgument = args.FirstOrDefault(argument =>
			argument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase));
		var profile = profileArgument is null
			? Environment.GetEnvironmentVariable("RTAIME_STARTUP_PROFILE")
			: profileArgument["--profile=".Length..];

		return string.Equals(profile, "HeadlessEngine", StringComparison.OrdinalIgnoreCase);
	}

	private static bool StandardOutputAndErrorAreRedirected() =>
		IsRedirectedStandardHandle(StandardOutputHandle) &&
		IsRedirectedStandardHandle(StandardErrorHandle);

	private static bool IsRedirectedStandardHandle(int standardHandle)
	{
		var handle = GetStdHandle(standardHandle);
		if (handle == IntPtr.Zero || handle == new IntPtr(-1))
			return false;

		var fileType = GetFileType(handle);
		return fileType is FileTypeDisk or FileTypePipe;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AttachConsole(uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AllocConsole();

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetStdHandle(int standardHandle);

	[DllImport("kernel32.dll")]
	private static extern uint GetFileType(IntPtr handle);
}
