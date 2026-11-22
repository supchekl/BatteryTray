using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatteryTray
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string cmd = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { AppLog.Line("UI exception: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { AppLog.Line("fatal: " + e.ExceptionObject); };
            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e) { AppLog.Line("task exception: " + e.Exception); e.SetObserved(); };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (cmd == "--preview") return RunPreview(args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "preview"));
            if (cmd == "--selftest") return RunSelfTest();
            if (cmd == "--make-icon") return MakeIcon(args.Length > 1 ? args[1] : "app.ico");

            bool created;
            using (var m = new Mutex(true, "BatteryTray_SingleInstance", out created))
            {
                if (!created) return 0;
                Application.Run(new TrayContext());
                GC.KeepAlive(m);
            }
            return 0;
        }

        // Диагностика без интерфейса: пишет найденные устройства в журнал. Код выхода 0, всё загрузилось.
        private static int RunSelfTest()
        {
            AppLog.Verbose = true;
            AppLog.Line("selftest start, version " + AppInfo.Version + ", build " + Dwm.Build);
            try
            {
                foreach (PnpNative.BtNode n in PnpNative.EnumeratePresentBtNodes())
                {
                    int? b = PnpNative.GetBatteryProp(n.DevInst);
                    AppLog.Line("bt node: " + n.InstanceId + " name=" + n.FriendlyName + " battery=" + (b == null ? "-" : b.Value.ToString()));
                }
                var poller = new DevicePoller();
                Task<List<DeviceRow>> t = poller.PollAsync();
                List<DeviceRow> rows = t.Wait(40000) ? t.Result : new List<DeviceRow>();
                AppLog.Line("selftest rows: " + rows.Count + (poller.LastError.Length > 0 ? " lastError=" + poller.LastError : ""));
                foreach (DeviceRow r in rows)
                    AppLog.Line("row: " + r.Name + " | " + r.TransportLabel + " | conn=" + r.Connected +
                                " | batt=" + (r.Battery == null ? "-" : r.BatteryText) + " " + r.BatterySource);
                AppLog.Line("selftest done");
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Line("selftest error: " + ex);
                return 1;
            }
        }

        private static int MakeIcon(string path)
        {
            try
            {
                File.WriteAllBytes(path, Icons.BuildIcoFile(new[] { 16, 20, 24, 32, 48, 64, 128, 256 }));
                return 0;
            }
            catch (Exception ex) { AppLog.Line("make-icon error: " + ex); return 1; }
        }

        private static List<DeviceRow> DemoRows()
        {
            var rows = new List<DeviceRow>();
            rows.Add(new DeviceRow { Key = "1", Name = "realme Buds Air 5", Connected = true, Battery = 85, Category = "Audio.Headphone" });
            rows.Add(new DeviceRow { Key = "2", Name = "JBL GO 2", Connected = true, Battery = 12, Category = "Audio.Speaker" });
            rows.Add(new DeviceRow { Key = "3", Name = "DualSense Wireless Controller", Connected = false, Category = "Input.Gaming" });
            rows.Add(new DeviceRow { Key = "4", Name = "G305", Transport = DeviceRow.TransportRadio, Connected = true, Battery = 64, Kind = DeviceKind.Mouse });
            rows.Add(new DeviceRow { Key = "5", Name = L.XboxController, Transport = DeviceRow.TransportXInput, Connected = true, Battery = 30, Approx = true });
            rows.Add(new DeviceRow { Key = "6", Name = "Keychron K2", Transport = DeviceRow.TransportUsb, Connected = true, Category = "Input.Keyboard" });
            rows.Add(new DeviceRow { Key = "7", Name = "DELL P2422H", Transport = DeviceRow.TransportDisplay, Connected = true, Category = "Display.Monitor" });
            return rows;
        }

        // Скриншоты для README: панель во всех темах, значки трея, подпись
        private static int RunPreview(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                List<DeviceRow> rows = DemoRows();
                ThemeMode saved = ThemeManager.Mode;

                var shots = new[]
                {
                    Tuple.Create(ThemeMode.Light, false, "popup-light.png", true),
                    Tuple.Create(ThemeMode.Dark, false, "popup-dark.png", true),
                    Tuple.Create(ThemeMode.Glass, false, "popup-glass-light.png", true),
                    Tuple.Create(ThemeMode.Glass, true, "popup-glass-dark.png", true),
                    Tuple.Create(ThemeMode.Light, false, "popup-win10-light.png", false),
                    Tuple.Create(ThemeMode.Dark, false, "popup-win10-dark.png", false),
                };
                foreach (var s in shots)
                {
                    ThemeManager.Mode = s.Item1;
                    ThemeManager.Apply(s.Item2);
                    PopupForm.PreviewWin11Style = s.Item4;
                    using (var pf = new PopupForm())
                    {
                        pf.Fill(rows, DateTime.Now, "");
                        using (Bitmap bmp = pf.RenderPreview())
                            bmp.Save(Path.Combine(dir, s.Item3), ImageFormat.Png);
                    }
                }
                PopupForm.PreviewWin11Style = null;
                ThemeManager.Mode = saved;
                ThemeManager.Apply();

                SaveTrayStrip(Path.Combine(dir, "tray-icons.png"));
                SaveLabel(Path.Combine(dir, "taskbar-label.png"), rows);
                AppLog.Line("preview done: " + dir);
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Line("preview error: " + ex);
                return 1;
            }
        }

        private static void SaveTrayStrip(string path)
        {
            const int cell = 96;
            using (var strip = new Bitmap(cell * 5 + 20, cell + 20))
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(32, 36, 41));
                int x = 10;
                int?[] percents = { 85, 45, 12, null };
                for (int i = 0; i < 5; i++)
                {
                    using (Icon ic = Icons.BuildTrayIcon(i < 4 ? percents[i] : null, i < 4, cell))
                        g.DrawIcon(ic, new Rectangle(x, 10, cell, cell));
                    x += cell;
                }
                strip.Save(path, ImageFormat.Png);
            }
        }

        private static void SaveLabel(string path, List<DeviceRow> rows)
        {
            using (var f = new Font("Segoe UI", 9f))
            using (var bmp = new Bitmap(560, 40))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(32, 32, 32));
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                int x = 14;
                foreach (DeviceRow r in rows)
                {
                    if (!r.Connected || r.Battery == null) continue;
                    string t = r.Name + " " + r.BatteryText;
                    Color c = r.Battery.Value <= 10 ? Color.FromArgb(0xFF, 0x99, 0xA4)
                            : r.Battery.Value <= 20 ? Color.FromArgb(0xFC, 0xE1, 0x00) : Color.FromArgb(242, 242, 242);
                    TextRenderer.DrawText(g, t, f, new Point(x, 11), c);
                    x += TextRenderer.MeasureText(t, f).Width + 10;
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
