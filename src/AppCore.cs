using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    internal static class AppInfo
    {
        public const string Name = "BatteryTray";
        public const string RepoUrl = "https://github.com/supchekl/BatteryTray";

        public static string Version
        {
            get
            {
                try
                {
                    var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                        Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                    if (a != null) return a.InformationalVersion;
                }
                catch { }
                return Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            }
        }

        // %LOCALAPPDATA%\BatteryTray: exe может лежать там, куда писать нельзя (Program Files)
        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }
    }

    internal static class AppLog
    {
        private static readonly object Lock = new object();
        public static readonly string LogPath = Path.Combine(AppInfo.DataDir, "BatteryTray.log");
        public static bool Verbose;

        public static void Line(string msg)
        {
            try
            {
                lock (Lock)
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                    {
                        string old = Path.ChangeExtension(LogPath, ".old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // Настройки: HKCU\Software\BatteryTray
    internal static class Settings
    {
        private const string KeyPath = "Software\\BatteryTray";
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        public static int? GetInt(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(name);
                        if (v is int) return (int)v;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void SetInt(string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                    k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch (Exception ex) { AppLog.Line("settings write error " + name + ": " + ex.Message); }
        }

        public static bool GetBool(string name, bool def)
        {
            int? v = GetInt(name);
            return v.HasValue ? v.Value != 0 : def;
        }

        public static void SetBool(string name, bool on) { SetInt(name, on ? 1 : 0); }

        public static bool AutoStart
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                        return k != null && k.GetValue(AppInfo.Name) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                    {
                        if (value) k.SetValue(AppInfo.Name, "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue(AppInfo.Name, false);
                    }
                    AppLog.Line("autostart set to " + value);
                }
                catch (Exception ex) { AppLog.Line("autostart error: " + ex.Message); }
            }
        }

        // если exe перенесли, запись автозапуска указывает в никуда, тихо обновляем путь
        public static void FixAutoStartPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    string v = k.GetValue(AppInfo.Name) as string;
                    if (v == null) return;
                    string want = "\"" + Application.ExecutablePath + "\"";
                    if (!string.Equals(v, want, StringComparison.OrdinalIgnoreCase))
                    {
                        k.SetValue(AppInfo.Name, want);
                        AppLog.Line("autostart path updated");
                    }
                }
            }
            catch { }
        }

        // Тема Windows: true = светлая
        public static bool AppsUseLightTheme { get { return ReadPersonalize("AppsUseLightTheme", 1) != 0; } }
        public static bool SystemUsesLightTheme { get { return ReadPersonalize("SystemUsesLightTheme", 0) != 0; } }
        public static bool TransparencyEnabled { get { return ReadPersonalize("EnableTransparency", 1) != 0; } }

        private static int ReadPersonalize(string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(name);
                        if (v is int) return (int)v;
                    }
                }
            }
            catch { }
            return def;
        }
    }
}
