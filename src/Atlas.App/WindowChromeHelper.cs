using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Atlas.App;

/// <summary>
/// 自定义标题栏窗口的公共设置：Win11 圆角 / 深色边框，以及最大化时按工作区修正尺寸。
/// </summary>
internal static class WindowChromeHelper
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    /// <summary>在窗口显示前调用一次即可（会自行挂接 SourceInitialized）。</summary>
    public static void Enable(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            TrySetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, 1);
            TrySetDwmAttribute(hwnd, DwmwaWindowCornerPreference, 2); // DWMWCP_ROUND
            // COLORREF = 0x00BBGGRR；这里对应 #26323F，作为窗口的细边框
            TrySetDwmAttribute(hwnd, DwmwaBorderColor, unchecked((int)0x003F3226));

            if (window.ResizeMode != ResizeMode.NoResize)
            {
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            }
        };
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            AdjustMaximizedBounds(hwnd, lParam);
        }
        return IntPtr.Zero;
    }

    /// <summary>无边框窗口最大化时会盖住任务栏，这里按显示器工作区修正最大尺寸和位置。</summary>
    private static void AdjustMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var work = info.WorkArea;
        var screen = info.Monitor;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition.X = work.Left - screen.Left;
        mmi.MaxPosition.Y = work.Top - screen.Top;
        mmi.MaxSize.X = Math.Max(0, work.Right - work.Left);
        mmi.MaxSize.Y = Math.Max(0, work.Bottom - work.Top);
        mmi.MaxTrackSize.X = mmi.MaxSize.X;
        mmi.MaxTrackSize.Y = mmi.MaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private static void TrySetDwmAttribute(IntPtr hwnd, int attribute, int value)
    {
        try { _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch { /* Win10 上部分属性不存在，忽略即可 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
