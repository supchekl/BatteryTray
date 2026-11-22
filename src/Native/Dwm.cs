using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray
{
    // DWM: фон-подложка (acrylic), скругление, рамка, тёмный режим заголовка
    internal static class Dwm
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_TRANSIENTWINDOW = 3;   // Acrylic
        public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
        public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;          // Windows 10 до 1803
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;   // Windows 10 1803+ и Windows 11

        private static int _build = -1;

        // Номер сборки Windows (Environment.OSVersion без манифеста врёт)
        public static int Build
        {
            get
            {
                if (_build >= 0) return _build;
                int b = 0;
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
                    {
                        if (k != null) int.TryParse(k.GetValue("CurrentBuildNumber") as string, out b);
                    }
                }
                catch { }
                _build = b;
                return b;
            }
        }

        public static bool IsWin11 { get { return Build >= 22000; } }

        private static float _dpiScale;

        // Масштаб системного DPI (процесс system-aware, см. app.manifest)
        public static float SystemDpiScale
        {
            get
            {
                if (_dpiScale > 0f) return _dpiScale;
                try
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        _dpiScale = g.DpiX / 96f;
                }
                catch { _dpiScale = 1f; }
                if (_dpiScale <= 0f) _dpiScale = 1f;
                return _dpiScale;
            }
        }
        public static bool HasSystemBackdrop { get { return Build >= 22621; } }

        private static bool SetInt(IntPtr hwnd, int attr, int value)
        {
            try { return DwmSetWindowAttribute(hwnd, attr, ref value, 4) == 0; }
            catch { return false; }
        }

        public static void SetCorners(IntPtr hwnd, int pref)
        {
            if (IsWin11) SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, pref);
        }

        public static void SetBorderColor(IntPtr hwnd, uint colorRef)
        {
            if (IsWin11) SetInt(hwnd, DWMWA_BORDER_COLOR, unchecked((int)colorRef));
        }

        public static uint ToColorRef(Color c) { return (uint)(c.R | (c.G << 8) | (c.B << 16)); }

        public static void SetDarkMode(IntPtr hwnd, bool dark)
        {
            if (Build >= 18985) SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            else if (Build >= 17763) SetInt(hwnd, 19, dark ? 1 : 0);
        }

        private static void ExtendFrame(IntPtr hwnd, bool whole)
        {
            try
            {
                var m = new MARGINS();
                if (whole) { m.Left = m.Right = m.Top = m.Bottom = -1; }
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }
        }

        // Windows 11 22H2+: системный acrylic. Вернёт false, если не поддерживается.
        public static bool EnableSystemAcrylic(IntPtr hwnd)
        {
            if (!HasSystemBackdrop) return false;
            ExtendFrame(hwnd, true);
            if (SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)) return true;
            ExtendFrame(hwnd, false);
            return false;
        }

        // Windows 10 / 11 21H2: недокументированный accent-блюр. tintAbgr, дымка поверх блюра.
        public static bool EnableAccentBlur(IntPtr hwnd, uint tintAbgr)
        {
            // без расширенной рамки DWM считает клиентскую область непрозрачной и игнорирует альфу
            ExtendFrame(hwnd, true);
            int state = Build >= 17134 ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND;
            if (SetAccent(hwnd, state, tintAbgr)) return true;
            ExtendFrame(hwnd, false);
            return false;
        }

        public static void DisableBackdrop(IntPtr hwnd)
        {
            if (HasSystemBackdrop) SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
            ExtendFrame(hwnd, false);
            SetAccent(hwnd, ACCENT_DISABLED, 0);
        }

        private static bool SetAccent(IntPtr hwnd, int state, uint tint)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var policy = new ACCENT_POLICY();
                policy.AccentState = state;
                policy.AccentFlags = 2;          // использовать GradientColor
                policy.GradientColor = tint;
                int size = Marshal.SizeOf(typeof(ACCENT_POLICY));
                ptr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA();
                data.Attribute = WCA_ACCENT_POLICY;
                data.Data = ptr;
                data.SizeOfData = size;
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch { return false; }
            finally { if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr); }
        }
    }

    // Холст с честным альфа-каналом: GDI+ рисует прямо в 32-битный DIB, который потом
    // копируется в окно BitBlt'ом. DWM берёт альфу из DIB, поэтому полупрозрачные пиксели
    // (стекло) действительно просвечивают, а не превращаются в серую кашу.
    internal sealed class AlphaSurface : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression;
            public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        private const uint SRCCOPY = 0x00CC0020;

        private IntPtr _hbmp;
        public Bitmap Bitmap { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public AlphaSurface(int w, int h)
        {
            Width = Math.Max(1, w);
            Height = Math.Max(1, h);
            var bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = Width;
            bi.biHeight = -Height;     // сверху вниз: совпадает с раскладкой GDI+
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            IntPtr bits;
            _hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (_hbmp == IntPtr.Zero || bits == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection failed");
            // DWM ожидает предумноженную альфу, ровно то, что пишет GDI+ в 32bppPArgb
            Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        public void BlitTo(Graphics target)
        {
            IntPtr hdc = target.GetHdc();
            try
            {
                IntPtr mem = CreateCompatibleDC(hdc);
                IntPtr old = SelectObject(mem, _hbmp);
                BitBlt(hdc, 0, 0, Width, Height, mem, 0, 0, SRCCOPY);
                SelectObject(mem, old);
                DeleteDC(mem);
            }
            finally { target.ReleaseHdc(hdc); }
        }

        public void Dispose()
        {
            if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; }
            if (_hbmp != IntPtr.Zero) { DeleteObject(_hbmp); _hbmp = IntPtr.Zero; }
        }
    }

    // Светлое/тёмное оформление нативных меню (uxtheme, Windows 10 1903+ и Windows 11).
    // Функции экспортируются только по номеру; на старых сборках вызов тихо пропускается.
    internal static class NativeMenuTheme
    {
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

        private const int ForceDark = 2;
        private const int ForceLight = 3;

        public static void Apply(bool dark)
        {
            if (Dwm.Build < 18362) return;
            try
            {
                SetPreferredAppMode(dark ? ForceDark : ForceLight);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { AppLog.Line("menu theme error: " + ex.Message); }
        }
    }
}
