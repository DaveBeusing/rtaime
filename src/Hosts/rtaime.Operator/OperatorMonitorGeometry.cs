// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace rtaime.Operator;

internal static class OperatorMonitorGeometry
{
    public static IReadOnlyList<OperatorMonitorArea> Read()
    {
        var result = new List<OperatorMonitorArea>(8);
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr state) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                uint x = 96, y = 96;
                try { if (GetDpiForMonitor(monitor, 0, out x, out y) != 0) x = 96; }
                catch (DllNotFoundException) { x = 96; }
                result.Add(new(info.Device ?? "", info.Work.Left, info.Work.Top,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, x, (info.Flags & 1) != 0));
            }
            return result.Count < 32;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }
    public static OperatorPanelPlacement Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return OperatorPanelPlacement.Default;
        var monitors = Read();
        var monitor = monitors.Where(m => Intersection(m, bounds) > 0).OrderByDescending(m => Intersection(m, bounds)).FirstOrDefault()
            ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        var dpi = GetDpiForWindow(handle);
        return new(monitor?.Id ?? "", bounds.Left, bounds.Top, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, monitor?.Left ?? 0, monitor?.Top ?? 0, dpi == 0 ? 96 : dpi);
    }
    public static void Restore(Window window, OperatorPanelPlacement placement)
    {
        var monitors = Read();
        var monitor = monitors.FirstOrDefault(m => m.Id == placement.Monitor) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        if (monitor is not null)
        {
            window.MinWidth = Math.Min(window.MinWidth, monitor.Width * 96 / monitor.Dpi);
            window.MinHeight = Math.Min(window.MinHeight, monitor.Height * 96 / monitor.Dpi);
        }
        var bounds = OperatorPanelRestore.Restore(placement, monitors);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0014);
    }
    private static double Intersection(OperatorMonitorArea m, NativeRect r) =>
        Math.Max(0, Math.Min(m.Left + m.Width, r.Right) - Math.Max(m.Left, r.Left)) *
        Math.Max(0, Math.Min(m.Top + m.Height, r.Bottom) - Math.Max(m.Top, r.Top));
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
