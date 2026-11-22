using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace BatteryTray
{
    // Панель устройств в стиле флайаутов Windows 11 (Fluent):
    // радиус 8 px у окна и 4 px у строк, сетка 8 px, Body 14 / Caption 12, токены цвета WinUI.
    internal sealed class PopupForm : Form
    {
        private enum Backdrop { None, SystemAcrylic, AccentBlur }

        private const float W = 360f;
        private const float HeaderH = 48f;
        private const float RowH = 52f;
        private const float FooterH = 48f;
        private const float Pad = 16f;
        private const float GroupH = 32f;

        private sealed class Line { public string Header; public DeviceRow Row; }

        private static string GroupName(int g) { return g == 0 ? L.GroupBluetooth : g == 1 ? L.GroupRadio : L.GroupWired; }

        private List<DeviceRow> _rows = new List<DeviceRow>();
        private readonly List<Line> _lines = new List<Line>();
        private int _hiddenRows;
        private string _statusText = "";
        private string _errorText = "";
        private readonly float _scale;
        private Backdrop _backdrop = Backdrop.None;
        private AlphaSurface _surface;
        private bool _paintErrorLogged;
        private Screen _anchor;

        // интерактивные зоны (логические координаты) и состояние наведения
        private RectangleF _refreshRect, _settingsRect;
        private readonly List<RectangleF> _rowRects = new List<RectangleF>();
        private readonly List<DeviceRow> _rowAt = new List<DeviceRow>();
        private int _hover = -1;          // -1 ничего, -2 обновить, -3 параметры, 0..N строки
        private bool _pressed;

        public event EventHandler RefreshRequested;
        public DateTime AutoHideAt { get; private set; }

        // Windows 11: окно 8 px (скругляет DWM), элементы 4 px. Windows 10: флайауты прямоугольные.
        // PreviewWin11Style: для --preview на машине сборки с другой версией Windows.
        public static bool? PreviewWin11Style;
        private static bool Win11Style { get { return PreviewWin11Style ?? Dwm.IsWin11; } }
        private static float Radius { get { return Win11Style ? 8f : 0f; } }
        private static float ItemRadius { get { return Win11Style ? 4f : 0f; } }

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ShowIcon = false;
            KeyPreview = true;
            Text = AppInfo.Name;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            DoubleBuffered = false;
            _scale = Dwm.SystemDpiScale;
            ClientSize = new Size(Px(W), Px(HeaderH + RowH + FooterH));
            Deactivate += delegate { HidePopup(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;                     // WS_EX_TOOLWINDOW: без Alt+Tab
                cp.ClassStyle |= 0x00020000;                  // CS_DROPSHADOW: системная тень
                return cp;
            }
        }

        private int Px(float logical) { return (int)Math.Round(logical * _scale); }

        public void HidePopup()
        {
            AutoHideAt = DateTime.Now;
            Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HidePopup();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HidePopup(); return true; }
            if (keyData == Keys.F5) { RaiseRefresh(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _surface != null) { _surface.Dispose(); _surface = null; }
            base.Dispose(disposing);
        }

        public void ApplyTheme()
        {
            if (!IsHandleCreated) return;
            IntPtr h = Handle;
            Dwm.DisableBackdrop(h);
            _backdrop = Backdrop.None;
            Dwm.SetDarkMode(h, ThemeManager.IsDark);
            if (ThemeManager.IsGlass && Settings.TransparencyEnabled)
            {
                // тонировку даёт сама подложка: системный acrylic или GradientColor accent-блюра (ABGR)
                if (Dwm.EnableSystemAcrylic(h)) _backdrop = Backdrop.SystemAcrylic;
                else if (Dwm.EnableAccentBlur(h, ThemeManager.IsDark ? 0xCC2C2C2Cu : 0xCCF9F9F9u)) _backdrop = Backdrop.AccentBlur;
            }
            Dwm.SetCorners(h, Dwm.DWMWCP_ROUND);
            Dwm.SetBorderColor(h, Dwm.DWMWA_COLOR_DEFAULT);
            AppLog.Line("popup theme: " + ThemeManager.Mode + (ThemeManager.IsDark ? "/dark" : "/light") + ", backdrop=" + _backdrop + ", build=" + Dwm.Build);
            ResizeToContent();
            Invalidate();
        }

        public void Fill(List<DeviceRow> rows, DateTime updated, string error)
        {
            _rows = rows ?? new List<DeviceRow>();
            _errorText = error ?? "";
            _statusText = updated == DateTime.MinValue ? L.Updating : L.UpdatedAt(updated.ToString("HH:mm"));
            BuildLines();
            ResizeToContent();
            Invalidate();
        }

        // Заголовки групп показываем, только если групп больше одной; лишнее не влезающее по высоте, в «И ещё N»
        private void BuildLines()
        {
            _lines.Clear();
            _hiddenRows = 0;
            float maxList = Screen.PrimaryScreen.WorkingArea.Height / _scale * 0.8f - HeaderH - FooterH;
            bool headers = _rows.Select(r => r.Group).Distinct().Count() > 1;
            float used = 4f;
            int lastGroup = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                DeviceRow r = _rows[i];
                float need = RowH + (headers && r.Group != lastGroup ? GroupH : 0f);
                if (used + need + RowH > maxList && i < _rows.Count - 1)
                {
                    _hiddenRows = _rows.Count - i;
                    break;
                }
                if (headers && r.Group != lastGroup)
                {
                    var h = new Line();
                    h.Header = GroupName(r.Group);
                    _lines.Add(h);
                    lastGroup = r.Group;
                }
                var l = new Line();
                l.Row = r;
                _lines.Add(l);
                used += need;
            }
        }

        private float ListH
        {
            get
            {
                if (_rows.Count == 0) return 96f;
                float h = 4f;
                foreach (Line l in _lines) h += l.Header != null ? GroupH : RowH;
                if (_hiddenRows > 0) h += RowH;
                return h;
            }
        }

        private void ResizeToContent()
        {
            var size = new Size(Px(W), Px(HeaderH + ListH + FooterH));
            if (ClientSize != size)
            {
                ClientSize = size;
                if (Visible && _anchor != null) PlaceNearTray(_anchor);
            }
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            // на Windows 11 скругляет DWM, на Windows 10 окно прямоугольное, Region не нужен
            Region old = Region;
            Region = null;
            if (old != null) old.Dispose();
        }

        // Как системные флайауты: 12 px от края рабочей области со стороны панели задач
        public void PlaceNearTray(Screen screen)
        {
            _anchor = screen;
            Rectangle wa = screen.WorkingArea;
            Rectangle b = screen.Bounds;
            int gap = Px(12f);
            int x = wa.Right - Width - gap;
            int y = wa.Bottom - Height - gap;
            if (wa.Top > b.Top) y = wa.Top + gap;
            if (wa.Left > b.Left) x = wa.Left + gap;
            if (y < wa.Top) y = wa.Top;
            Location = new Point(x, y);
        }

        // ---------- Рисование ----------

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (_surface == null || _surface.Width != w || _surface.Height != h)
                {
                    if (_surface != null) _surface.Dispose();
                    _surface = new AlphaSurface(w, h);
                }
                using (Graphics g = Graphics.FromImage(_surface.Bitmap))
                {
                    g.Clear(Color.Transparent);
                    Render(g, _backdrop != Backdrop.None);
                    g.Flush(FlushIntention.Sync);
                }
                _surface.BlitTo(e.Graphics);
            }
            catch (Exception ex)
            {
                if (!_paintErrorLogged) { _paintErrorLogged = true; AppLog.Line("popup paint error: " + ex); }
                e.Graphics.Clear(ThemeManager.Surface);
                try { Render(e.Graphics, false); } catch { }
            }
        }

        private void Render(Graphics g, bool translucent)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = translucent ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(_scale, _scale);

            float h = ClientSize.Height / _scale;

            // фон: у стекла его рисует подложка DWM, у остальных тем, сплошной Surface
            if (!translucent)
                using (var br = new SolidBrush(ThemeManager.Surface))
                    g.FillRectangle(br, -1f, -1f, W + 2f, h + 2f);
            if (!Win11Style)   // на Windows 11 рамку рисует DWM
                using (var pen = new Pen(ThemeManager.SurfaceStroke, 1f))
                    g.DrawRectangle(pen, 0.5f, 0.5f, W - 1f, h - 1f);

            PaintHeader(g);
            PaintList(g);
            PaintFooter(g, h);
        }

        private void PaintHeader(Graphics g)
        {
            using (Font f = Fluent.BodyStrong())
                DrawText(g, L.Title, f, ThemeManager.TextPrimary, new RectangleF(Pad, 0f, W - 2 * Pad - 40f, HeaderH), StringAlignment.Near);

            // кнопка «Обновить»: 32×32, как стандартная кнопка-иконка
            _refreshRect = new RectangleF(W - 8f - 32f, 8f, 32f, 32f);
            PaintBackplate(g, _refreshRect, _hover == -2);
            Icons.DrawGlyph(g, Fluent.GlyphRefresh, _refreshRect, ThemeManager.TextPrimary, 16f);
        }

        private void PaintList(Graphics g)
        {
            _rowRects.Clear();
            _rowAt.Clear();
            float y = HeaderH;

            if (_rows.Count == 0)
            {
                string title = _errorText.Length > 0 ? L.ListError : L.NoDevices;
                string hint = _errorText.Length > 0 ? _errorText : L.NoDevicesHint;
                using (Font fb = Fluent.Body())
                    DrawText(g, title, fb, ThemeManager.TextSecondary, new RectangleF(Pad, y + 18f, W - 2 * Pad, 20f), StringAlignment.Center);
                using (Font fc = Fluent.Caption())
                    DrawText(g, hint, fc, ThemeManager.TextTertiary, new RectangleF(Pad, y + 42f, W - 2 * Pad, 36f), StringAlignment.Center, true);
                return;
            }

            foreach (Line line in _lines)
            {
                if (line.Header != null)
                {
                    using (Font fs = Fluent.BodyStrong())
                        DrawText(g, line.Header, fs, ThemeManager.TextSecondary, new RectangleF(Pad, y + 8f, W - 2 * Pad, GroupH - 8f), StringAlignment.Near);
                    y += GroupH;
                    continue;
                }
                var rect = new RectangleF(4f, y + 2f, W - 8f, RowH - 4f);
                _rowRects.Add(rect);
                _rowAt.Add(line.Row);
                PaintBackplate(g, rect, _hover == _rowRects.Count - 1);
                PaintRow(g, line.Row, y);
                y += RowH;
            }

            if (_hiddenRows > 0)
                using (Font fc = Fluent.Caption())
                    DrawText(g, L.MoreInSettings(_hiddenRows), fc, ThemeManager.TextTertiary,
                        new RectangleF(52f, y, W - 52f - Pad, RowH), StringAlignment.Near);
        }

        private void PaintRow(Graphics g, DeviceRow r, float y)
        {
            bool on = r.Connected;
            Icons.DrawDeviceIcon(g, Icons.DetectKind(r), new RectangleF(Pad, y + 16f, 20f, 20f),
                on ? ThemeManager.TextPrimary : ThemeManager.TextTertiary);

            // справа: глиф батареи + процент
            float right = W - Pad;
            if (on && r.Battery != null)
            {
                Color c = ThemeManager.BatteryColor(r.Battery.Value);
                using (Font fb = Fluent.Body())
                {
                    string text = r.BatteryText;
                    float tw = MeasureText(g, text, fb);
                    DrawText(g, text, fb, c, new RectangleF(right - tw - 1f, y, tw + 2f, RowH), StringAlignment.Far);
                    right -= tw + 8f;
                }
                var glyphBox = new RectangleF(right - 16f, y + 18f, 16f, 16f);
                Icons.DrawGlyph(g, Fluent.BatteryGlyph(r.Battery), glyphBox, c, 16f);
                right = glyphBox.X - 12f;
            }

            string sub;
            if (!on) sub = L.NotConnected;
            else if (r.Battery == null && r.Wireless) sub = r.TransportLabel + " · " + L.NoBatteryInfo;
            else sub = r.TransportLabel;

            float textW = right - 52f;
            using (Font fb = Fluent.Body())
                DrawText(g, r.Name.Length > 0 ? r.Name : L.Unnamed, fb, on ? ThemeManager.TextPrimary : ThemeManager.TextSecondary,
                    new RectangleF(52f, y + 7f, textW, 20f), StringAlignment.Near);
            using (Font fc = Fluent.Caption())
                DrawText(g, sub, fc, on ? ThemeManager.TextSecondary : ThemeManager.TextTertiary,
                    new RectangleF(52f, y + 27f, textW, 16f), StringAlignment.Near);
        }

        private void PaintFooter(Graphics g, float h)
        {
            float fy = h - FooterH;
            using (var pen = new Pen(ThemeManager.Divider, 1f))
                g.DrawLine(pen, 0f, fy, W, fy);

            using (Font fc = Fluent.Caption())
                DrawText(g, _statusText, fc, ThemeManager.TextTertiary, new RectangleF(Pad, fy, 160f, FooterH), StringAlignment.Near);

            // гиперссылка-кнопка, как HyperlinkButton в WinUI
            using (Font fb = Fluent.Body())
            {
                string label = L.BluetoothSettings;
                float tw = MeasureText(g, label, fb);
                _settingsRect = new RectangleF(W - 8f - tw - 22f, fy + 8f, tw + 22f, 32f);
                PaintBackplate(g, _settingsRect, _hover == -3);
                DrawText(g, label, fb, ThemeManager.AccentText, _settingsRect, StringAlignment.Center);
            }
        }

        private void PaintBackplate(Graphics g, RectangleF r, bool hover)
        {
            if (!hover) return;
            using (GraphicsPath p = Icons.Rounded(r, ItemRadius))
            using (var br = new SolidBrush(_pressed ? ThemeManager.SubtlePressed : ThemeManager.SubtleHover))
                g.FillPath(br, p);
        }

        private static void DrawText(Graphics g, string text, Font f, Color c, RectangleF r, StringAlignment align, bool wrap = false)
        {
            using (var br = new SolidBrush(c))
            using (var sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                if (!wrap) sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, f, br, r, sf);
            }
        }

        private static float MeasureText(Graphics g, string text, Font f)
        {
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
                return g.MeasureString(text, f, 1000, sf).Width;
        }

        // ---------- Мышь ----------

        private int HitTest(Point p)
        {
            var lp = new PointF(p.X / _scale, p.Y / _scale);
            if (_refreshRect.Contains(lp)) return -2;
            if (_settingsRect.Contains(lp)) return -3;
            for (int i = 0; i < _rowRects.Count; i++)
                if (_rowRects[i].Contains(lp)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h == -1 ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; _pressed = false; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && _hover != -1) { _pressed = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasPressed = _pressed;
            _pressed = false;
            Invalidate();
            if (e.Button != MouseButtons.Left || !wasPressed) return;
            int h = HitTest(e.Location);
            if (h == -2) RaiseRefresh();
            else if (h == -3) OpenSettings("ms-settings:bluetooth");
            else if (h >= 0 && h < _rowAt.Count)
                OpenSettings(_rowAt[h].Transport == DeviceRow.TransportBt ? "ms-settings:bluetooth" : "ms-settings:connecteddevices");
        }

        private void OpenSettings(string uri)
        {
            HidePopup();
            try { Process.Start(uri); }
            catch (Exception ex) { AppLog.Line("open settings error: " + ex.Message); }
        }

        private void RaiseRefresh()
        {
            _statusText = L.Updating;
            Invalidate();
            EventHandler h = RefreshRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---------- Превью для README (--preview) ----------

        public Bitmap RenderPreview()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int pad = Px(32f);
            bool glass = ThemeManager.IsGlass;

            var layer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(layer))
            {
                g.Clear(Color.Transparent);
                Render(g, glass);
            }

            var outBmp = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format32bppPArgb);
            var wallpaper = new Bitmap(outBmp.Width, outBmp.Height, PixelFormat.Format32bppPArgb);
            using (Graphics wg = Graphics.FromImage(wallpaper))
                PaintWallpaper(wg, new Rectangle(0, 0, wallpaper.Width, wallpaper.Height), ThemeManager.IsDark);
            using (Graphics g = Graphics.FromImage(outBmp))
            {
                g.DrawImageUnscaled(wallpaper, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath clip = Icons.Rounded(new RectangleF(pad, pad, w, h), Radius * _scale))
                {
                    if (glass)
                    {
                        // имитация acrylic: размытые обои (уменьшить и растянуть) + тон Surface
                        using (Bitmap blurred = Blur(wallpaper, 24))
                        using (var tb = new TextureBrush(blurred))
                            g.FillPath(tb, clip);
                        using (var tint = new SolidBrush(Color.FromArgb(0xCC, ThemeManager.Surface)))
                            g.FillPath(tint, clip);
                    }
                    using (var tb = new TextureBrush(layer))
                    {
                        tb.TranslateTransform(pad, pad);
                        g.FillPath(tb, clip);
                    }
                    using (var pen = new Pen(ThemeManager.IsDark ? Color.FromArgb(0x33, 0, 0, 0) : Color.FromArgb(0x0F, 0, 0, 0), 1f))
                        g.DrawPath(pen, clip);
                }
            }
            layer.Dispose();
            wallpaper.Dispose();
            return outBmp;
        }

        private static Bitmap Blur(Bitmap src, int factor)
        {
            using (var small = new Bitmap(Math.Max(1, src.Width / factor), Math.Max(1, src.Height / factor)))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, new Rectangle(0, 0, small.Width, small.Height));
                }
                var big = new Bitmap(src.Width, src.Height);
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, new Rectangle(0, 0, big.Width, big.Height));
                }
                return big;
            }
        }

        // Спокойные «обои» для превью, без неоновых пятен
        private static void PaintWallpaper(Graphics g, Rectangle r, bool dark)
        {
            Color a = dark ? Color.FromArgb(24, 33, 48) : Color.FromArgb(174, 196, 222);
            Color b = dark ? Color.FromArgb(52, 44, 58) : Color.FromArgb(226, 214, 200);
            using (var lgb = new LinearGradientBrush(r, a, b, 60f)) g.FillRectangle(lgb, r);
            using (var hill = new SolidBrush(dark ? Color.FromArgb(70, 90, 110, 140) : Color.FromArgb(90, 255, 255, 255)))
                g.FillEllipse(hill, r.X - r.Width * 0.2f, r.Y + r.Height * 0.55f, r.Width * 0.9f, r.Height * 0.8f);
        }
    }
}
