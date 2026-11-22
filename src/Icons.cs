using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryTray
{
    // Рисование: иконки устройств, иконка-батарейка для трея, палитра
    internal enum DeviceKind { Headphones, Speaker, Mouse, Keyboard, Gamepad, Bluetooth, Usb, Monitor, Camera, Printer, Storage, Phone, Other }

    internal static class Icons
    {
        // Палитра
        public static readonly Color Green = Color.FromArgb(52, 168, 83);
        public static readonly Color Yellow = Color.FromArgb(249, 171, 0);
        public static readonly Color Red = Color.FromArgb(234, 67, 53);
        public static readonly Color Grey = Color.FromArgb(154, 160, 166);

        public static Color StatusColor(int? pct, bool connected)
        {
            if (!connected) return Grey;
            if (!pct.HasValue) return Color.FromArgb(240, 150, 20);
            int b = pct.Value;
            return b >= 50 ? Green : b >= 20 ? Yellow : Red;
        }

        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Max(0f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2f));
            if (rad <= 0.01f) { p.AddRectangle(r); return p; }
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static DeviceKind DetectKind(DeviceRow r)
        {
            if (r == null) return DeviceKind.Bluetooth;
            if (r.Kind.HasValue) return r.Kind.Value;
            if (r.Transport == DeviceRow.TransportXInput) return DeviceKind.Gamepad;
            DeviceKind byCategory;
            if (KindFromCategory(r.Category, out byCategory)) return byCategory;
            string n = (r.Name ?? "").ToLowerInvariant();
            bool usb = r.Transport == DeviceRow.TransportUsb;
            if (n.Contains("клавиатур") || n.Contains("keyboard") || n.Contains(" keys")) return DeviceKind.Keyboard;
            if (n.Contains("мышь") || n.Contains("mouse") || n.Contains("pointing") || n.Contains("trackball") || n.Contains("bt ms")) return DeviceKind.Mouse;
            if (n.Contains("buds") || n.Contains("наушник") || n.Contains("гарнитур") || n.Contains("headset") || n.Contains("headphone") ||
                n.Contains("earbud") || n.Contains("airpods") || n.Contains("wh-") || n.Contains("wf-")) return DeviceKind.Headphones;
            if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("gamepad") || n.Contains("controller") ||
                n.Contains("xbox") || n.Contains("joy-con") || n.Contains("геймпад") || n.Contains("джойстик")) return DeviceKind.Gamepad;
            if (n.Contains("jbl") || n.Contains("speaker") || n.Contains("колонк") || n.Contains("soundbar") || n.Contains("boom") || n.Contains("audio")) return DeviceKind.Speaker;
            if (usb) return DeviceKind.Usb;
            return DeviceKind.Bluetooth;
        }

        // System.Devices.Category контейнера: «Input.Mouse», «Audio.Headphone», «Display.Monitor»…
        public static bool KindFromCategory(string category, out DeviceKind kind)
        {
            string c = (category ?? "").ToLowerInvariant();
            kind = DeviceKind.Other;
            if (c.Length == 0) return false;
            if (c.Contains("mouse")) kind = DeviceKind.Mouse;
            else if (c.Contains("keyboard")) kind = DeviceKind.Keyboard;
            else if (c.Contains("gaming") || c.Contains("game")) kind = DeviceKind.Gamepad;
            else if (c.Contains("headphone") || c.Contains("headset")) kind = DeviceKind.Headphones;
            else if (c.Contains("speaker") || c.Contains("audio")) kind = DeviceKind.Speaker;
            else if (c.Contains("monitor") || c.Contains("display")) kind = DeviceKind.Monitor;
            else if (c.Contains("camera") || c.Contains("webcam") || c.Contains("imaging")) kind = DeviceKind.Camera;
            else if (c.Contains("print") || c.Contains("scanner")) kind = DeviceKind.Printer;
            else if (c.Contains("storage") || c.Contains("disk")) kind = DeviceKind.Storage;
            else if (c.Contains("phone") || c.Contains("communication")) kind = DeviceKind.Phone;
            else return false;
            return true;
        }

        // Иконка трея: батарейка с уровнем заряда
        public static Icon BuildTrayIcon(int? pct, bool anyBtConnected)
        {
            int s = SystemInformation.SmallIconSize.Width;
            if (s < 16) s = 16;
            if (s > 48) s = 48;
            return BuildTrayIcon(pct, anyBtConnected, s);
        }

        public static Icon BuildTrayIcon(int? pct, bool anyBtConnected, int size)
        {
            if (Fluent.HasIconFont) return BuildGlyphTrayIcon(pct, anyBtConnected, Math.Max(16, size), !Settings.SystemUsesLightTheme);
            return BuildVectorTrayIcon(pct, anyBtConnected, size);
        }

        // Как системный индикатор: белый глиф на тёмной панели, чёрный на светлой, цвет, только при разряде
        public static Icon BuildGlyphTrayIcon(int? pct, bool anyConnected, int s, bool darkTaskbar)
        {
            Color ink = darkTaskbar ? Color.White : Color.FromArgb(0xE4, 0, 0, 0);
            char glyph = Fluent.BatteryGlyph(anyConnected ? pct : 0);
            if (!anyConnected) ink = Color.FromArgb(0x5D, ink);                      // TextFillDisabled
            else if (pct != null && pct.Value <= 10) ink = darkTaskbar ? Color.FromArgb(0xFF, 0x99, 0xA4) : Color.FromArgb(0xC4, 0x2B, 0x1C);
            else if (pct != null && pct.Value <= 20) ink = darkTaskbar ? Color.FromArgb(0xFC, 0xE1, 0x00) : Color.FromArgb(0x9D, 0x5D, 0x00);

            using (var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font f = Fluent.Icon(s))
                using (var br = new SolidBrush(ink))
                using (var sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(glyph.ToString(), f, br, new RectangleF(0, 0, s, s), sf);
                }
                IntPtr h = bmp.GetHicon();
                Icon result = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return result;
            }
        }

        private static Icon BuildVectorTrayIcon(int? pct, bool anyBtConnected, int size)
        {
            int s = Math.Max(8, size);
            int k = 4; // суперсэмплинг для гладкости
            Bitmap hi = null;
            Icon result = null;
            try
            {
                hi = new Bitmap(s * k, s * k);
                using (Graphics g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float u = s * k / 16f; // расчёт от сетки 16px
                    DrawBattery(g, u, pct, anyBtConnected, StatusColor(pct, anyBtConnected));
                }

                using (Bitmap lo = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (Graphics g2 = Graphics.FromImage(lo))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.SmoothingMode = SmoothingMode.AntiAlias;
                    g2.DrawImage(hi, new Rectangle(0, 0, s, s), new Rectangle(0, 0, s * k, s * k), GraphicsUnit.Pixel);
                    IntPtr hIcon = lo.GetHicon();
                    result = (Icon)Icon.FromHandle(hIcon).Clone();
                    DestroyIcon(hIcon);
                }
                return result;
            }
            finally { if (hi != null) hi.Dispose(); }
        }

        private static void DrawBattery(Graphics g, float u, int? pct, bool anyBtConnected, Color col)
        {
            bool question = anyBtConnected && !pct.HasValue;
            bool dash = !anyBtConnected;
            RectangleF body = new RectangleF(1.2f * u, 4.6f * u, 11.0f * u, 6.8f * u);
            RectangleF cap = new RectangleF(12.5f * u, 6.5f * u, 1.9f * u, 3.0f * u);
            using (var capBrush = new SolidBrush(col))
                g.FillRectangle(capBrush, cap);
            using (GraphicsPath bp = Rounded(body, 1.6f * u))
            using (var pen = new Pen(col, 1.3f * u))
                g.DrawPath(pen, bp);

            if (question)
            {
                using (var f = new Font("Segoe UI", 6.2f * u, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var br = new SolidBrush(col))
                    g.DrawString("?", f, br, body, sf);
            }
            else if (dash)
            {
                using (var p2 = new Pen(col, 1.6f * u))
                    g.DrawLine(p2, body.X + 2.6f * u, body.Y + body.Height / 2f, body.Right - 2.6f * u, body.Y + body.Height / 2f);
            }
            else
            {
                int b = pct.Value;
                float frac = Math.Max(0.10f, Math.Min(1f, b / 100f));
                float inset = 1.05f * u;
                RectangleF fill = new RectangleF(body.X + inset, body.Y + inset, (body.Width - 2 * inset) * frac, body.Height - 2 * inset);
                float rad = Math.Min(0.9f * u, Math.Min(fill.Width, fill.Height) / 2f);
                using (GraphicsPath fp = Rounded(fill, rad))
                using (var br = new SolidBrush(col))
                    g.FillPath(br, fp);
            }
        }

        public static char GlyphFor(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Headphones: return Fluent.GlyphHeadphone;
                case DeviceKind.Speaker: return Fluent.GlyphSpeakers;
                case DeviceKind.Mouse: return Fluent.GlyphMouse;
                case DeviceKind.Keyboard: return Fluent.GlyphKeyboard;
                case DeviceKind.Gamepad: return Fluent.GlyphGame;
                case DeviceKind.Usb: return Fluent.GlyphUsb;
                case DeviceKind.Monitor: return Fluent.GlyphMonitor;
                case DeviceKind.Camera: return Fluent.GlyphCamera;
                case DeviceKind.Printer: return Fluent.GlyphPrinter;
                case DeviceKind.Storage: return Fluent.GlyphStorage;
                case DeviceKind.Phone: return Fluent.GlyphPhone;
                case DeviceKind.Other: return Fluent.GlyphDevices;
                default: return Fluent.GlyphBluetooth;
            }
        }

        // Глиф из Segoe Fluent Icons / MDL2 Assets; без шрифта, рисованная иконка
        public static void DrawDeviceIcon(Graphics g, DeviceKind kind, RectangleF box, Color color)
        {
            if (!Fluent.HasIconFont) { DrawDeviceIconVector(g, kind, box, color); return; }
            DrawGlyph(g, GlyphFor(kind), box, color, Math.Min(box.Width, box.Height));
        }

        public static void DrawGlyph(Graphics g, char glyph, RectangleF box, Color color, float px)
        {
            using (Font f = Fluent.Icon(px))
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph.ToString(), f, br, box, sf);
            }
        }

        // Векторные глифы устройств (расчёт от сетки 24x24)
        private static void DrawDeviceIconVector(Graphics g, DeviceKind kind, RectangleF box, Color color)
        {
            GraphicsState was = g.Save();
            try
            {
                float sc = Math.Min(box.Width, box.Height) / 24f;
                g.TranslateTransform(box.X + (box.Width - 24f * sc) / 2f, box.Y + (box.Height - 24f * sc) / 2f);
                g.ScaleTransform(sc, sc);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(color, 1.7f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;

                    switch (kind)
                    {
                        case DeviceKind.Headphones:
                            g.DrawArc(pen, 4f, 3f, 16f, 16f, 180f, 180f);
                            using (var br = new SolidBrush(color))
                            {
                                g.FillPath(br, Rounded(new RectangleF(3.0f, 11.2f, 5.0f, 8.2f), 2.2f));
                                g.FillPath(br, Rounded(new RectangleF(16.0f, 11.2f, 5.0f, 8.2f), 2.2f));
                            }
                            break;

                        case DeviceKind.Speaker:
                            using (GraphicsPath body = Rounded(new RectangleF(6.5f, 2.8f, 11f, 18.4f), 2.6f))
                                g.DrawPath(pen, body);
                            g.DrawEllipse(pen, 10.5f, 5.4f, 3f, 3f);
                            g.DrawEllipse(pen, 8.6f, 10.6f, 6.8f, 6.8f);
                            break;

                        case DeviceKind.Mouse:
                            using (GraphicsPath body = Rounded(new RectangleF(7.2f, 2.8f, 9.6f, 18.4f), 4.8f))
                                g.DrawPath(pen, body);
                            g.DrawLine(pen, 7.2f, 9.4f, 16.8f, 9.4f);
                            g.DrawLine(pen, 12f, 5.0f, 12f, 8.0f);
                            break;

                        case DeviceKind.Keyboard:
                            using (GraphicsPath body = Rounded(new RectangleF(2.2f, 6.6f, 19.6f, 10.8f), 2.0f))
                                g.DrawPath(pen, body);
                            using (var br = new SolidBrush(color))
                            {
                                for (int row = 0; row < 2; row++)
                                    for (int cx = 0; cx < 6; cx++)
                                        g.FillRectangle(br, 4.0f + cx * 2.85f, 8.8f + row * 2.5f, 1.7f, 1.7f);
                                g.FillRectangle(br, 4.0f, 13.8f, 1.7f, 1.7f);
                                g.FillRectangle(br, 6.85f, 13.8f, 1.7f, 1.7f);
                                g.FillRectangle(br, 9.7f, 13.8f, 10.25f, 1.7f);
                            }
                            break;

                        case DeviceKind.Gamepad:
                            using (GraphicsPath body = Rounded(new RectangleF(3f, 7.5f, 18f, 9.5f), 4.6f))
                                g.DrawPath(pen, body);
                            g.DrawLine(pen, 6.6f, 9.6f, 6.6f, 14.9f);
                            g.DrawLine(pen, 4.0f, 12.2f, 9.2f, 12.2f);
                            g.DrawEllipse(pen, 15.0f, 9.2f, 2.2f, 2.2f);
                            g.DrawEllipse(pen, 17.2f, 11.6f, 2.2f, 2.2f);
                            break;

                        case DeviceKind.Bluetooth:
                            g.DrawLine(pen, 8.6f, 2.2f, 8.6f, 21.8f);
                            g.DrawLine(pen, 8.6f, 2.2f, 15.6f, 8.8f);
                            g.DrawLine(pen, 15.6f, 8.8f, 8.6f, 15.4f);
                            g.DrawLine(pen, 8.6f, 8.4f, 15.6f, 15.0f);
                            g.DrawLine(pen, 15.6f, 15.0f, 8.6f, 21.8f);
                            break;

                        default: // Usb
                            g.DrawLine(pen, 12f, 21.5f, 12f, 5.6f);
                            g.DrawLine(pen, 10.2f, 7.4f, 12f, 5.6f);
                            g.DrawLine(pen, 12f, 5.6f, 13.8f, 7.4f);
                            g.DrawLine(pen, 12f, 15.5f, 8.4f, 11.9f);
                            g.DrawEllipse(pen, 5.7f, 9.2f, 2.6f, 2.6f);
                            g.DrawLine(pen, 12f, 12.4f, 15.2f, 9.2f);
                            g.DrawRectangle(pen, 15.0f, 5.8f, 2.6f, 2.6f);
                            break;
                    }
                }
            }
            finally { g.Restore(was); }
        }

        // Картинка для app.ico (та же батарейка, зелёная)
        public static Bitmap RenderAppIconBitmap(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float u = size / 16f;
                DrawBattery(g, u, 85, true, Green);
            }
            return bmp;
        }

        // Сборка .ico из PNG-кадров
        public static byte[] BuildIcoFile(int[] sizes)
        {
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (Bitmap b = RenderAppIconBitmap(s))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (var outMs = new MemoryStream())
            {
                using (var w = new BinaryWriter(outMs))
                {
                    w.Write((ushort)0);
                    w.Write((ushort)1);
                    w.Write((ushort)pngs.Count);
                    int offset = 6 + 16 * pngs.Count;
                    for (int i = 0; i < pngs.Count; i++)
                    {
                        int s = sizes[i];
                        w.Write((byte)(s >= 256 ? 0 : s));
                        w.Write((byte)(s >= 256 ? 0 : s));
                        w.Write((byte)0);
                        w.Write((byte)0);
                        w.Write((ushort)1);
                        w.Write((ushort)32);
                        w.Write((uint)pngs[i].Length);
                        w.Write((uint)offset);
                        offset += pngs[i].Length;
                    }
                    foreach (byte[] p in pngs) w.Write(p);
                    w.Flush();
                }
                return outMs.ToArray();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
