using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XboxEmulatorUI;

internal static class WindowAppearance
{
    public static void UseDarkFrame(Window window, bool borderless = false)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Set(handle, 20, 1); // Immersive dark mode.
            Set(handle, 34, borderless ? 0xfffffffeu : 0x00323232u);
            Set(handle, 35, 0x00181818);
            Set(handle, 36, 0x00f0eeeb);
            if (borderless)
                HwndSource.FromHwnd(handle)?.AddHook(FillMonitorWhenMaximized);
        };
    }

    private static nint FillMonitorWhenMaximized(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        const int calculateClientArea = 0x0083;
        const uint nearestMonitor = 2;
        if (message == calculateClientArea && IsZoomed(window))
        {
            // Use the proposed window rectangle as the client area without a resize frame.
            handled = true;
            return 0;
        }
        if (message != getMinMaxInfo) return 0;

        var monitor = MonitorFromWindow(window, nearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return 0;

        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new Point();
        limits.MaxSize = new Point { X = info.Monitor.Right - info.Monitor.Left, Y = info.Monitor.Bottom - info.Monitor.Top };
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    private static void Set(nint handle, int attribute, uint value)
        => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
}
