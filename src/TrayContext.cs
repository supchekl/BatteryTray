using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    internal sealed class TrayContext : ApplicationContext
    {
        private const int PollIntervalMs = 15000;

        private readonly NotifyIcon _ni;
        // Нативное меню Win32: на Windows 11 скруглённое и в стиле системы, на Windows 10, классическое
        private ContextMenu _menu;
        private MenuItem _miAuto;
        private MenuItem _miLabel;
        private MenuItem _miNotify;
        private MenuItem _miUsb;
        private readonly MenuItem[] _miThemes = new MenuItem[4];
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _balloonTimer;
        private readonly DevicePoller _poller = new DevicePoller();
        private readonly SynchronizationContext _ui;
        private PopupForm _popup;
        private readonly TaskbarLabelForm _label;

        private bool _showLabel = Settings.GetBool("ShowTaskbarLabel", true);
        private bool _notifyOn = Settings.GetBool("Notifications", true);

        private readonly Queue<Tuple<string, string, ToolTipIcon>> _balloons = new Queue<Tuple<string, string, ToolTipIcon>>();
        private readonly Dictionary<string, int> _alertBucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _lastConn = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _notifyBaseline;
        private string _lastDevSig = "";

        private List<DeviceRow> _rows = new List<DeviceRow>();
        private DateTime _lastUpdate = DateTime.MinValue;
        private int _busy;              // 0/1, Interlocked
        private Icon _curIcon;

        public TrayContext()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            ThemeManager.Load();
            _poller.IncludeWired = Settings.GetBool("ShowUsbDevices", true);

            L.Load();
            BuildMenu();
            UpdateThemeChecks();

            _ni = new NotifyIcon();
            _ni.Text = AppInfo.Name;
            _ni.ContextMenu = _menu;
            _ni.MouseClick += OnTrayClick;
            SetTrayIcon(Icons.BuildTrayIcon(null, false));
            _ni.Visible = true;

            EnsurePopup();

            _label = new TaskbarLabelForm();
            _label.LabelClicked += delegate { TogglePopup(); };
            _label.SetVisible(_showLabel);

            _balloonTimer = new System.Windows.Forms.Timer();
            _balloonTimer.Interval = 4500;
            _balloonTimer.Tick += delegate { ShowNextBalloon(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = PollIntervalMs;
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            Settings.FixAutoStartPath();
            AppLog.Line(AppInfo.Name + " " + AppInfo.Version + " started, build=" + Dwm.Build + ", exe=" + Application.ExecutablePath);
            RefreshNow();
        }

        // Меню собирается заново при смене языка
        private void BuildMenu()
        {
            _menu = new ContextMenu();

            var miTitle = new MenuItem(AppInfo.Name + " " + AppInfo.Version);
            miTitle.Enabled = false;

            var miOpen = new MenuItem(L.MenuOpen, delegate { ShowPopup(); });
            miOpen.DefaultItem = true;

            var miRefresh = new MenuItem(L.MenuRefresh, delegate { RefreshNow(); });

            var miTheme = new MenuItem(L.MenuTheme);
            for (int t = 0; t < 4; t++)
            {
                int tt = t;
                var mi = new MenuItem(ThemeManager.Names[t], delegate { SetTheme((ThemeMode)tt); });
                mi.RadioCheck = true;
                _miThemes[t] = mi;
                miTheme.MenuItems.Add(mi);
            }

            _miLabel = new MenuItem(L.MenuTaskbarLabel, delegate
            {
                _showLabel = !_showLabel;
                _miLabel.Checked = _showLabel;
                Settings.SetBool("ShowTaskbarLabel", _showLabel);
                UpdateLabel();
            });
            _miLabel.Checked = _showLabel;

            _miUsb = new MenuItem(L.MenuShowWired, delegate
            {
                _poller.IncludeWired = !_poller.IncludeWired;
                _miUsb.Checked = _poller.IncludeWired;
                Settings.SetBool("ShowUsbDevices", _poller.IncludeWired);
                RefreshNow();
            });
            _miUsb.Checked = _poller.IncludeWired;

            _miNotify = new MenuItem(L.MenuNotifications, delegate
            {
                _notifyOn = !_notifyOn;
                _miNotify.Checked = _notifyOn;
                Settings.SetBool("Notifications", _notifyOn);
            });
            _miNotify.Checked = _notifyOn;

            var miTestNotify = new MenuItem(L.MenuTestNotification, delegate { ShowBalloon(AppInfo.Name, L.NotificationsWork, ToolTipIcon.Info, true); });

            _miAuto = new MenuItem(L.MenuAutostart, delegate
            {
                Settings.AutoStart = !Settings.AutoStart;
                _miAuto.Checked = Settings.AutoStart;
            });
            _miAuto.Checked = Settings.AutoStart;

            var miLang = new MenuItem(L.MenuLanguage);
            string[] langNames = { L.LanguageAuto, "Русский", "English" };
            for (int i = 0; i < 3; i++)
            {
                int li = i;
                var mi = new MenuItem(langNames[i], delegate { SetLanguage((UiLanguage)li); });
                mi.RadioCheck = true;
                mi.Checked = (int)L.Setting == i;
                miLang.MenuItems.Add(mi);
            }

            var miLog = new MenuItem(L.MenuOpenLog, delegate { OpenPath(AppInfo.DataDir); });
            var miExit = new MenuItem(L.MenuExit, delegate { ExitApp(); });

            _menu.MenuItems.AddRange(new[]
            {
                miTitle, new MenuItem("-"),
                miOpen, miRefresh, new MenuItem("-"),
                miTheme, miLang, _miLabel, _miUsb, _miNotify, miTestNotify, new MenuItem("-"),
                _miAuto, miLog, new MenuItem("-"),
                miExit
            });
            NativeMenuTheme.Apply(ThemeManager.IsDark);
            UpdateThemeChecks();
            if (_ni != null)
            {
                ContextMenu old = _ni.ContextMenu;
                _ni.ContextMenu = _menu;
                if (old != null && old != _menu) old.Dispose();
            }
        }

        private void SetLanguage(UiLanguage lang)
        {
            L.Setting = lang;
            L.Save();
            BuildMenu();
            if (_popup != null && !_popup.IsDisposed) _popup.Fill(_rows, _lastUpdate, _poller.LastError);
            UpdateTooltip();
            RefreshNow();     // имена по умолчанию («Геймпад Xbox» и т.п.) тоже зависят от языка
        }

        // ---------- Системные события ----------

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle &&
                e.Category != UserPreferenceCategory.Color) return;
            // светлая/тёмная тема Windows или прозрачность могли смениться
            _ui.Post(delegate
            {
                ThemeManager.Apply();
                NativeMenuTheme.Apply(ThemeManager.IsDark);
                if (_popup != null && !_popup.IsDisposed) _popup.ApplyTheme();
                _label.ApplyTheme();
                UpdateUi();      // значок трея зависит от светлой/тёмной панели задач
            }, null);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) _ui.Post(delegate { RefreshNow(); }, null);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _ui.Post(delegate { UpdateLabel(); }, null);
        }

        // ---------- Панель ----------

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        }

        private void TogglePopup()
        {
            EnsurePopup();
            if (_popup.Visible) { _popup.HidePopup(); return; }
            if ((DateTime.Now - _popup.AutoHideAt).TotalMilliseconds < 350) return;   // только что закрылась сама по клику
            ShowPopup();
        }

        private void EnsurePopup()
        {
            if (_popup != null && !_popup.IsDisposed) return;
            _popup = new PopupForm();
            _popup.RefreshRequested += delegate { RefreshNow(); };
        }

        private void ShowPopup()
        {
            EnsurePopup();
            _popup.Fill(_rows, _lastUpdate, _poller.LastError);
            _popup.PlaceNearTray(Screen.FromPoint(Cursor.Position));
            _popup.Show();
            _popup.Activate();
        }

        // ---------- Опрос ----------

        private void RefreshNow()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            Task.Run(async delegate
            {
                List<DeviceRow> rows = null;
                try
                {
                    rows = await _poller.PollAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Line("poll error: " + ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
                _ui.Post(delegate
                {
                    if (rows != null)
                    {
                        _rows = rows;
                        _lastUpdate = DateTime.Now;
                        LogSnapshot(rows);
                    }
                    UpdateUi();
                }, null);
            });
        }

        private void UpdateUi()
        {
            if (_popup != null && !_popup.IsDisposed && _popup.Visible) _popup.Fill(_rows, _lastUpdate, _poller.LastError);
            UpdateTooltip();
            UpdateLabel();
            ProcessNotifications();

            bool anyBt = _rows.Any(r => r.Connected && r.Wireless);
            int? iconPct = null;
            List<DeviceRow> withBatt = _rows.Where(r => r.Connected && r.Battery != null).ToList();
            if (withBatt.Count > 0) iconPct = withBatt.Min(r => r.Battery.Value);
            SetTrayIcon(Icons.BuildTrayIcon(iconPct, anyBt));
        }

        // в журнал: только изменения состояния
        private void LogSnapshot(List<DeviceRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("state: ").Append(rows.Count(r => r.Connected)).Append('/').Append(rows.Count).Append(" connected: ");
            foreach (DeviceRow r in rows)
            {
                sb.Append(r.Name).Append(r.Connected ? " [on" : " [off");
                if (r.Battery != null) sb.Append(' ').Append(r.BatteryText).Append(' ').Append(r.BatterySource);
                sb.Append("] ");
            }
            string sig = sb.ToString();
            if (sig == _lastDevSig) return;
            _lastDevSig = sig;
            AppLog.Line(sig);
        }

        // ---------- Уведомления ----------

        private void ProcessNotifications()
        {
            foreach (DeviceRow r in _rows)
            {
                if (!r.Wireless) continue;     // провод: подключение флешки: не повод для уведомления
                string key = r.Key;

                if (!_notifyBaseline || !_lastConn.ContainsKey(key))
                {
                    // первый опрос или новое устройство, запоминаем без уведомления
                    _lastConn[key] = r.Connected;
                    _alertBucket[key] = BucketOf(r.Battery);
                    continue;
                }

                if (r.Connected != _lastConn[key])
                {
                    string batt = r.Connected && r.Battery != null ? " (" + r.BatteryText + ")" : "";
                    ShowBalloon(r.Name, r.Connected ? L.Connected + batt : L.Disconnected, ToolTipIcon.Info, false);
                }
                _lastConn[key] = r.Connected;

                if (r.Connected && r.Battery != null)
                {
                    int prev;
                    _alertBucket.TryGetValue(key, out prev);
                    int b = r.Battery.Value;
                    int nb;
                    if (b <= 10) nb = 2;
                    else if (b <= 20) nb = 1;
                    else if (b >= 25) nb = 0;
                    else nb = prev;          // 21–24%: гистерезис, чтобы не дребезжало
                    if (nb > prev)
                    {
                        if (nb == 1) ShowBalloon(r.Name, L.LowBattery(r.BatteryText), ToolTipIcon.Warning, false);
                        else ShowBalloon(r.Name, L.CriticalBattery(r.BatteryText), ToolTipIcon.Error, false);
                    }
                    _alertBucket[key] = nb;
                }
            }
            _notifyBaseline = true;
        }

        private static int BucketOf(int? battery)
        {
            if (battery == null) return 0;
            return battery.Value <= 10 ? 2 : (battery.Value <= 20 ? 1 : 0);
        }

        // Очередь: несколько событий сразу (например, наушники + мышь после сна) не теряются
        private void ShowBalloon(string title, string text, ToolTipIcon icon, bool force)
        {
            if (!_notifyOn && !force) return;
            if (title.Length > 60) title = title.Substring(0, 59) + "…";
            _balloons.Enqueue(Tuple.Create(title, text, icon));
            if (!_balloonTimer.Enabled) ShowNextBalloon();
        }

        private void ShowNextBalloon()
        {
            if (_balloons.Count == 0) { _balloonTimer.Stop(); return; }
            Tuple<string, string, ToolTipIcon> b = _balloons.Dequeue();
            try { _ni.ShowBalloonTip(4000, b.Item1, b.Item2, b.Item3); } catch { }
            _balloonTimer.Start();
        }

        // ---------- Подпись, подсказка, тема ----------

        private void UpdateLabel()
        {
            if (_label == null || _label.IsDisposed) return;
            _label.SetVisible(_showLabel);
            if (_showLabel) _label.UpdateContent(_rows);
        }

        private void SetTheme(ThemeMode mode)
        {
            ThemeManager.Mode = mode;
            ThemeManager.Apply();
            ThemeManager.Save();
            NativeMenuTheme.Apply(ThemeManager.IsDark);
            UpdateThemeChecks();
            if (_popup != null && !_popup.IsDisposed) _popup.ApplyTheme();
        }

        private void UpdateThemeChecks()
        {
            for (int t = 0; t < _miThemes.Length; t++)
                if (_miThemes[t] != null) _miThemes[t].Checked = (t == (int)ThemeManager.Mode);
        }

        private void UpdateTooltip()
        {
            List<DeviceRow> conn = _rows.Where(r => r.Connected && r.Wireless).ToList();
            string tip;
            if (conn.Count == 0)
            {
                string err = _poller.LastError;
                tip = err.Length > 0 ? err : L.NoDevices;
            }
            else
            {
                var parts = new List<string>();
                foreach (DeviceRow r in conn)
                {
                    string name = r.Name.Length > 22 ? r.Name.Substring(0, 21) + "…" : r.Name;
                    parts.Add(name + " " + (r.Battery != null ? r.BatteryText : "?"));
                }
                tip = string.Join("\n", parts);
            }
            // NotifyIcon.Text в .NET Framework ограничен 63 символами
            if (tip.Length > 63) tip = tip.Substring(0, 62) + "…";
            _ni.Text = tip;
        }

        private void SetTrayIcon(Icon ic)
        {
            Icon old = _curIcon;
            _ni.Icon = ic;
            _curIcon = ic;
            if (old != null) old.Dispose();
        }

        private static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { AppLog.Line("open path error: " + ex.Message); }
        }

        private void ExitApp()
        {
            try
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _timer.Stop();
                _balloonTimer.Stop();
                _ni.Visible = false;
                _ni.Dispose();
                if (_popup != null) _popup.Dispose();
                _label.Dispose();
                if (_curIcon != null) _curIcon.Dispose();
                AppLog.Line(AppInfo.Name + " exited");
            }
            finally
            {
                ExitThread();
            }
        }
    }
}
