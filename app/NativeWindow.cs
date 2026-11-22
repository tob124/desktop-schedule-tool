using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ScdDesktop
{
    internal static class NativeWindow
    {
        public const int ToolWindow = 0x80;
        public const int AppWindow = 0x40000;
        public const uint ForegroundEvent = 3;
        public const int SystemCommand = 0x0112;
        public const int Minimize = 0xf020;
        public const int NcCalcSize = 0x0083;
        public const int NcHitTest = 0x0084;
        private const uint NoMoveSizeOrActivate = 0x1 | 0x2 | 0x10 | 0x200;
        public const int Caption = 0x00c00000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        public delegate void ForegroundCallback(IntPtr hook, uint change, IntPtr window,
            int objectId, int childId, uint eventThread, uint time);
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
            ForegroundCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string caption);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
            int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowStyle(IntPtr window, int index, IntPtr style);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
            ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

        public static void SetCaptionVisible(IntPtr window, bool visible)
        {
            long current = GetWindowStyle(window, -16).ToInt64();
            long next = visible ? current | Caption : current & ~Caption;
            if (current == next) return;
            SetWindowStyle(window, -16, new IntPtr(next));
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, NoMoveSizeOrActivate | 0x4 | 0x20);
        }

        public static bool SetBackdrop(IntPtr window, bool blur)
        {
            int material = blur ? 3 : 1; // DWMSBT_TRANSIENTWINDOW or DWMSBT_NONE
            bool applied = DwmSetWindowAttribute(window, 38, ref material, sizeof(int)) == 0;
            var margins = blur && applied ? new Margins { Left = -1 } : new Margins();
            bool extended = DwmExtendFrameIntoClientArea(window, ref margins) == 0;
            if (blur && applied && !extended)
            {
                material = 1;
                DwmSetWindowAttribute(window, 38, ref material, sizeof(int));
            }
            return applied && extended;
        }

        public static void BeginMove(IntPtr window)
        {
            if ((GetAsyncKeyState(1) & 0x8000) == 0) return;
            Point cursor = Cursor.Position;
            ReleaseCapture();
            var position = new IntPtr(unchecked((int)((uint)(ushort)cursor.X | ((uint)(ushort)cursor.Y << 16))));
            PostMessage(window, 0x00a1, new IntPtr(2), position); // WM_NCLBUTTONDOWN, HTCAPTION
        }

        public static Point ScreenPoint(IntPtr position)
        {
            long value = position.ToInt64();
            return new Point(unchecked((short)value), unchecked((short)(value >> 16)));
        }

        public static int ResizeHit(Rectangle client, Point position, int grip)
        {
            bool left = position.X < grip;
            bool right = position.X >= client.Right - grip;
            if (position.Y < grip) return left ? 13 : right ? 14 : 12;
            if (position.Y >= client.Bottom - grip) return left ? 16 : right ? 17 : 15;
            return left ? 10 : right ? 11 : 0;
        }

        public static void EnablePerMonitorDpi()
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }

        public static void SetDesktopWindowTraits(IntPtr window, bool compact)
        {
            int enabled = 1;
            DwmSetWindowAttribute(window, 12, ref enabled, sizeof(int));
            int disableTransitions = compact ? 1 : 0;
            DwmSetWindowAttribute(window, 3, ref disableTransitions, sizeof(int));
        }

        public static bool IsInWindow(IntPtr window, IntPtr widget)
        {
            return window != IntPtr.Zero && (window == widget || GetAncestor(window, 2) == widget);
        }

        public static IntPtr GetOwner(IntPtr window)
        {
            return GetWindow(window, 4); // GW_OWNER
        }

        public static bool SetOwner(IntPtr window, IntPtr owner)
        {
            if (GetOwner(window) != owner)
                SetWindowStyle(window, -8, owner); // GWLP_HWNDPARENT on a top-level window
            return GetOwner(window) == owner;
        }

        public static IntPtr GetDesktopOwner()
        {
            IntPtr desktop = DesktopIconsHost();
            return IsDesktop(desktop) ? desktop : GetShellWindow();
        }

        public static bool IsDesktop(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            if (window == GetShellWindow()) return true;
            var className = new StringBuilder(64);
            GetClassName(window, className, className.Capacity);
            return className.ToString() == "Progman" || className.ToString() == "WorkerW";
        }

        public static bool SetDesktopOrder(IntPtr widget, IntPtr externalForeground, bool lowering)
        {
            IntPtr foreground = GetForegroundWindow();
            if (IsInWindow(foreground, widget))
            {
                if (!lowering) return false;
                foreground = externalForeground;
            }

            IntPtr root = GetAncestor(foreground, 2);
            bool shellForeground = IsDesktop(root);
            IntPtr desktop = shellForeground ? root : DesktopIconsHost();
            if (desktop != IntPtr.Zero)
            {
                bool restored = shellForeground && IsIconic(widget);
                if (restored) ShowWindow(widget, 4);
                IntPtr aboveDesktop = GetWindow(desktop, 3);
                if (aboveDesktop == widget) return restored;
                bool wasBehindDesktop = shellForeground && IsBelow(widget, desktop);
                if (!SetWindowPos(widget, aboveDesktop, 0, 0, 0, 0, NoMoveSizeOrActivate))
                    SetWindowPos(widget, shellForeground ? IntPtr.Zero : new IntPtr(1), 0, 0, 0, 0, NoMoveSizeOrActivate);
                return restored || wasBehindDesktop;
            }
            SetWindowPos(widget, new IntPtr(1), 0, 0, 0, 0, NoMoveSizeOrActivate);
            return false;
        }

        private static bool IsBelow(IntPtr widget, IntPtr desktop)
        {
            for (IntPtr window = GetWindow(desktop, 2); window != IntPtr.Zero; window = GetWindow(window, 2))
                if (window == widget) return true;
            return false;
        }

        private static IntPtr DesktopIconsHost()
        {
            IntPtr iconsHost = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                    return true;
                iconsHost = window;
                return false;
            }, IntPtr.Zero);
            return iconsHost == IntPtr.Zero ? GetShellWindow() : iconsHost;
        }

        public static Rectangle BoundsFor(WindowPlacement placement, bool expanded)
        {
            Screen screen = placement == null ? Screen.PrimaryScreen :
                Screen.AllScreens.FirstOrDefault(item => item.DeviceName == placement.Screen) ?? Screen.PrimaryScreen;
            var work = screen.WorkingArea;
            double scale = MonitorScale(work);
            bool saved = placement != null && placement.Width > 0 && placement.Height > 0;
            int width = Math.Min(work.Width, Scale(saved ? placement.Width : expanded ? 1120 : 720, scale));
            int height = Math.Min(work.Height, Scale(saved ? placement.Height : expanded ? 690 : 420, scale));
            width = Math.Max(width, Math.Min(work.Width, Scale(410, scale)));
            height = Math.Max(height, Math.Min(work.Height, Scale(240, scale)));
            int x = saved ? work.Left + Scale(placement.X, scale)
                : expanded ? work.Left + (work.Width - width) / 2 : work.Right - width - Scale(20, scale);
            int y = saved ? work.Top + Scale(placement.Y, scale)
                : expanded ? work.Top + (work.Height - height) / 2 : work.Top + Scale(20, scale);
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            return new Rectangle(x, y, width, height);
        }

        public static WindowPlacement CaptureBounds(Form window)
        {
            Screen screen = Screen.FromHandle(window.Handle);
            Rectangle work = screen.WorkingArea;
            Rectangle bounds = window.Bounds;
            double scale = Math.Max(96u, GetDpiForWindow(window.Handle)) / 96.0;
            return new WindowPlacement {
                Screen = screen.DeviceName,
                X = Scale(bounds.Left - work.Left, 1 / scale),
                Y = Scale(bounds.Top - work.Top, 1 / scale),
                Width = Scale(bounds.Width, 1 / scale),
                Height = Scale(bounds.Height, 1 / scale)
            };
        }

        private static double MonitorScale(Rectangle work)
        {
            var point = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
            uint x, y;
            return GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out x, out y) == 0 ? x / 96.0 : 1;
        }

        private static int Scale(int value, double scale)
        {
            return (int)Math.Round(Math.Max(-30000, Math.Min(30000, value)) * scale);
        }
    }
}
