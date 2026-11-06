using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fink.App;

/// <summary>
/// Notices when the pen is flipped to its eraser end. Windows reports pens (XP-Pen, Wacom...)
/// through pointer messages whose pen flags say "inverted" or "eraser"; SDL, which MonoGame's
/// window is built on, doesn't pass those on, so this listens to the window's messages itself
/// (subclassing it) and hands every message back to SDL unchanged.
///
/// It needs the tablet driver to use Windows Ink (in XP-Pen's driver: "Windows Ink" on). Without
/// it the pen arrives as a plain mouse and there is no eraser to see; the driver can then be set
/// to send a key for the eraser end, and that key bound to "Eraser (hold)".
/// </summary>
internal static class PenInput
{
    /// <summary>True while a pen is near the screen upside down.</summary>
    public static bool Eraser { get; private set; }
    /// <summary>True while the pen's barrel (side) button is held.</summary>
    public static bool Barrel { get; private set; }
    /// <summary>True while a pen is close enough to the screen to be tracked (hovering or touching).</summary>
    public static bool InRange { get; private set; }
    /// <summary>True while the pen touches the screen.</summary>
    public static bool Contact { get; private set; }
    /// <summary>How many times the pen has touched down; with <see cref="BarrelAtDown"/>, the latest one.</summary>
    public static long Downs { get; private set; }
    /// <summary>Whether the barrel button was held when the pen last touched down.</summary>
    public static bool BarrelAtDown { get; private set; }
    /// <summary>The <see cref="Downs"/> the input code has dealt with.</summary>
    public static long DownsSeen { get; set; }
    /// <summary>What the last press turned into (for the Options screen).</summary>
    public static string LastPress { get; set; } = "";
    /// <summary>True once any pen has been seen (for the Options screen).</summary>
    public static bool PenSeen { get; private set; }
    public static bool Hooked { get; private set; }

    private static WndProc? _proc; // kept alive: the window calls it
    private static IntPtr _old;

    /// <summary>Starts listening on this process's main window. Windows only; harmless elsewhere.</summary>
    public static void Hook()
    {
        if (Hooked || !OperatingSystem.IsWindows()) return;
        var hwnd = FindOurWindow();
        if (hwnd == IntPtr.Zero) return;
        _proc = Proc;
        _old = SetWindowLongPtrW(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        Hooked = _old != IntPtr.Zero;
    }

    private const uint WmPointerUpdate = 0x0245, WmPointerDown = 0x0246, WmPointerUp = 0x0247, WmPointerEnter = 0x0249, WmPointerLeave = 0x024A;
    private const uint PointerFlagInRange = 0x2, PointerFlagInContact = 0x4;
    private const uint PtPen = 3;
    private const uint PenFlagBarrel = 0x1, PenFlagInverted = 0x2, PenFlagEraser = 0x4;

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WmPointerUpdate or WmPointerDown or WmPointerUp or WmPointerEnter or WmPointerLeave)
        {
            uint id = (uint)(wParam.ToInt64() & 0xFFFF);
            if (GetPointerType(id, out var type) && type == PtPen)
            {
                PenSeen = true;
                if (msg == WmPointerLeave) Eraser = Barrel = InRange = Contact = false;
                else if (GetPointerPenInfo(id, out var info))
                {
                    Eraser = (info.penFlags & (PenFlagInverted | PenFlagEraser)) != 0;
                    Barrel = (info.penFlags & PenFlagBarrel) != 0;
                    InRange = (info.pointerInfo.pointerFlags & PointerFlagInRange) != 0 || msg != WmPointerUp;
                    Contact = msg != WmPointerUp && (info.pointerInfo.pointerFlags & PointerFlagInContact) != 0;
                }
                if (msg == WmPointerDown)
                {
                    BarrelAtDown = Barrel;
                    Contact = true;
                    Downs++;
                }
                else if (msg == WmPointerUp) Contact = false;
            }
        }
        return CallWindowProcW(_old, hwnd, msg, wParam, lParam);
    }

    private static IntPtr FindOurWindow()
    {
        uint pid = (uint)Environment.ProcessId;
        IntPtr best = IntPtr.Zero;
        long area = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wp);
            if (wp == pid && IsWindowVisible(h) && GetWindowRect(h, out var r))
            {
                long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (a > area) { area = a; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ---------------------------------------------------------------- Win32

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    private const int GwlpWndProc = -4;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public Point ptPixelLocation;
        public Point ptHimetricLocation;
        public Point ptPixelLocationRaw;
        public Point ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetPointerType(uint pointerId, out uint pointerType);
    [DllImport("user32.dll")] private static extern bool GetPointerPenInfo(uint pointerId, out PointerPenInfo penInfo);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
