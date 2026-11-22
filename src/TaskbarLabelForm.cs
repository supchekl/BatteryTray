using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryTray
{
    // Подпись «Наушники 85%  Мышь 55%» на панели задач слева от трея
    internal sealed class TaskbarLabelForm : Form
    {
        private sealed class Seg { public string Text = ""; public Color Color; }

        private readonly List<Seg> _segs = new List<Seg>();
        private readonly Timer _zTimer;
        private bool _wanted;
        public event EventHandler LabelClicked;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string windowName);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        public TaskbarLabelForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            ApplyKeyColor();

            // Клик по панели задач поднимает её над нашими окнами, периодически возвращаемся наверх
            _zTimer = new Timer();
            _zTimer.Interval = 2000;
            _zTimer.Tick += delegate
            {
                if (!Visible) return;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                ResizeAndPlace();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _zTimer.Dispose();
            base.Dispose(disposing);
        }

        private static bool LightTaskbar { get { return Settings.SystemUsesLightTheme; } }

        // Ключ прозрачности близок к цвету панели задач, сглаженные края текста не дают ореола
        private void ApplyKeyColor()
        {
            Color key = LightTaskbar ? Color.FromArgb(238, 239, 240) : Color.FromArgb(1, 2, 3);
            BackColor = key;
            TransparencyKey = key;
        }

        // Размер в пунктах уже масштабируется по DPI системой, домножать не нужно
        private static Font MakeFont() { return new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point); }

        public void SetVisible(bool on)
        {
            _wanted = on;
            if (!on) { _zTimer.Stop(); Hide(); return; }
            if (_segs.Count > 0 && TaskbarHorizontalOnScreen()) { if (!Visible) Show(); _zTimer.Start(); }
        }

        public void ApplyTheme()
        {
            ApplyKeyColor();
            Invalidate();
        }

        public void UpdateContent(List<DeviceRow> rows)
        {
            _segs.Clear();
            bool light = LightTaskbar;
            Color on = light ? Color.FromArgb(28, 28, 30) : Color.FromArgb(242, 242, 242);
            Color off = light ? Color.FromArgb(120, 122, 126) : Color.FromArgb(140, 145, 150);

            // только подключённые и с процентом, остальное тут лишнее
            foreach (DeviceRow r in rows.Where(x => x.Connected && x.Battery != null))
            {
                string t = r.Name ?? "";
                if (t.Length > 24) t = t.Substring(0, 23) + "…";
                var s = new Seg();
                s.Text = t + " " + r.BatteryText;
                if (r.Battery.Value <= 10) s.Color = light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4);
                else if (r.Battery.Value <= 20) s.Color = light ? Color.FromArgb(0x9D, 0x5D, 0x00) : Color.FromArgb(0xFC, 0xE1, 0x00);
                else s.Color = on;
                _segs.Add(s);
            }
            if (_segs.Count == 0)
            {
                foreach (DeviceRow r in rows.Where(x => x.Connected && x.Transport == DeviceRow.TransportBt).Take(3))
                {
                    var s = new Seg();
                    s.Text = r.Name;
                    s.Color = off;
                    _segs.Add(s);
                }
            }

            ApplyKeyColor();
            ResizeAndPlace();
            if (!_wanted || _segs.Count == 0 || !TaskbarHorizontalOnScreen()) { _zTimer.Stop(); Hide(); return; }
            if (!Visible) Show();
            _zTimer.Start();
            Invalidate();
        }

        private static bool TryGetTaskbar(out RECT r)
        {
            r = new RECT();
            IntPtr tb = FindWindowW("Shell_TrayWnd", null);
            return tb != IntPtr.Zero && IsWindowVisible(tb) && GetWindowRect(tb, out r);
        }

        private static bool TaskbarHorizontalOnScreen()
        {
            RECT r;
            if (!TryGetTaskbar(out r)) return false;
            if (r.Right - r.Left < r.Bottom - r.Top) return false;          // вертикальная панель: не поддерживаем
            Rectangle scr = Screen.PrimaryScreen.Bounds;
            return r.Top >= scr.Top - 2 && r.Bottom <= scr.Bottom + 2;      // автоскрытие уводит панель за край
        }

        // Левая граница области трея (уведомления + часы)
        private int TrayLeft(RECT tb, float dpi)
        {
            IntPtr hTb = FindWindowW("Shell_TrayWnd", null);
            IntPtr notify = FindWindowExW(hTb, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT nr;
            if (notify != IntPtr.Zero && GetWindowRect(notify, out nr))
            {
                int w = nr.Right - nr.Left;
                if (w > 20 && w < (tb.Right - tb.Left) / 2) return nr.Left;
            }
            return tb.Right - (int)(240 * dpi);
        }

        private void ResizeAndPlace()
        {
            RECT tr;
            bool haveTb = TryGetTaskbar(out tr);
            float dpi = Dwm.SystemDpiScale;
            int trayLeft = haveTb ? TrayLeft(tr, dpi) : 800;
            int maxW = haveTb ? Math.Max(80, trayLeft - tr.Left - (int)(240 * dpi)) : 500;
            int gap = (int)(12 * dpi);

            int w, h;
            using (Font f = MakeFont())
            {
                w = Measure(f, gap);
                while (w > maxW && _segs.Count > 1)
                {
                    _segs.RemoveAt(_segs.Count - 1);
                    w = Measure(f, gap);
                }
                h = TextRenderer.MeasureText("Ag", f).Height + 6;
            }
            ClientSize = new Size(Math.Max(10, Math.Min(w, maxW)), h);

            if (haveTb)
            {
                int x = trayLeft - Width - (int)(6 * dpi);
                if (x < tr.Left + 4) x = tr.Left + 4;
                int y = tr.Top + ((tr.Bottom - tr.Top) - Height) / 2;
                if (Location.X != x || Location.Y != y) Location = new Point(x, y);
            }
        }

        private int Measure(Font f, int gap)
        {
            int w = 0;
            foreach (Seg s in _segs) w += TextRenderer.MeasureText(s.Text, f, Size.Empty, TextFormatFlags.NoPadding).Width + gap;
            return _segs.Count > 0 ? w - gap : 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Font f = MakeFont())
            {
                int gap = (int)(12 * Dwm.SystemDpiScale);
                int x = 0;
                foreach (Seg s in _segs)
                {
                    Size m = TextRenderer.MeasureText(s.Text, f, Size.Empty, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, s.Text, f, new Rectangle(x, 0, m.Width + 2, ClientSize.Height), s.Color, BackColor,
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    x += m.Width + gap;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            EventHandler h = LabelClicked;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
