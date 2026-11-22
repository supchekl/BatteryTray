using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using Microsoft.Win32;

namespace BatteryTray
{
    // Шрифты и глифы Windows (Segoe UI Variable, Segoe Fluent Icons / Segoe MDL2 Assets).
    // Значения: из гайдов Microsoft: learn.microsoft.com/windows/apps/design/signature-experiences/typography
    internal static class Fluent
    {
        // Глифы: одинаковые коды в Segoe MDL2 Assets (Win10) и Segoe Fluent Icons (Win11)
        public const char GlyphHeadphone = '\uE7F6';
        public const char GlyphSpeakers = '\uE7F5';
        public const char GlyphMouse = '\uE962';
        public const char GlyphKeyboard = '\uE765';
        public const char GlyphGame = '\uE7FC';
        public const char GlyphBluetooth = '\uE702';
        public const char GlyphUsb = '\uE88E';
        public const char GlyphMonitor = '\uE7F4';
        public const char GlyphCamera = '\uE722';
        public const char GlyphPrinter = '\uE749';
        public const char GlyphStorage = '\uEDA2';
        public const char GlyphPhone = '\uE8EA';
        public const char GlyphDevices = '\uE772';
        public const char GlyphRefresh = '\uE72C';
        public const char GlyphSetting = '\uE713';
        public const char GlyphBattery10 = '\uE83F';
        public const char GlyphBatteryUnknown = '\uE996';

        // Battery0..Battery9 = U+E850..U+E859, Battery10 = U+E83F
        public static char BatteryGlyph(int? pct)
        {
            if (pct == null) return GlyphBatteryUnknown;
            int level = (int)Math.Round(Math.Max(0, Math.Min(100, pct.Value)) / 10.0);
            return level >= 10 ? GlyphBattery10 : (char)(0xE850 + level);
        }

        private static string _iconFont;
        private static string _textFont;
        private static string _displayFont;
        private static bool _probed;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            string[] names;
            try
            {
                using (var fonts = new InstalledFontCollection())
                    names = fonts.Families.Select(f => f.Name).ToArray();
            }
            catch { names = new string[0]; }

            if (names.Contains("Segoe Fluent Icons")) _iconFont = "Segoe Fluent Icons";
            else if (names.Contains("Segoe MDL2 Assets")) _iconFont = "Segoe MDL2 Assets";

            // Segoe UI Variable регистрируется именованными экземплярами (Text / Display / Small)
            _textFont = names.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            _displayFont = names.Contains("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        }

        public static bool HasIconFont { get { Probe(); return _iconFont != null; } }

        // Размеры: в эффективных пикселях (epx), как в гайдах
        public static Font Icon(float px) { Probe(); return new Font(_iconFont ?? "Segoe UI Symbol", px, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Body() { Probe(); return new Font(_textFont, 14f, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font BodyStrong() { Probe(); return new Font(SemiboldName(_textFont), 14f, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Caption() { Probe(); return new Font(_textFont, 12f, FontStyle.Regular, GraphicsUnit.Pixel); }

        // У Segoe UI полужирный, отдельное семейство; у Variable, экземпляр «… Semibold»
        private static string _semibold;
        private static string SemiboldName(string baseName)
        {
            if (_semibold != null) return _semibold;
            string candidate = baseName == "Segoe UI" ? "Segoe UI Semibold" : baseName + " Semibold";
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    if (fonts.Families.Any(f => f.Name == candidate)) _semibold = candidate;
                    else if (fonts.Families.Any(f => f.Name == "Segoe UI Semibold")) _semibold = "Segoe UI Semibold";
                    else _semibold = baseName;
                }
            }
            catch { _semibold = baseName; }
            return _semibold;
        }

        // Акцентный цвет пользователя (HKCU\Software\Microsoft\Windows\DWM\AccentColor, формат ABGR)
        public static Color SystemAccent
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\DWM"))
                    {
                        object v = k == null ? null : k.GetValue("AccentColor");
                        if (v is int)
                        {
                            int abgr = (int)v;
                            return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                        }
                    }
                }
                catch { }
                return Color.FromArgb(0, 120, 212);   // синий Windows по умолчанию
            }
        }

        // Текст акцентного цвета: в тёмной теме светлее, в светлой темнее, как AccentTextFillColorPrimary
        public static Color AccentText(bool dark)
        {
            Color a = SystemAccent;
            return dark ? Mix(a, Color.White, 0.55f) : Mix(a, Color.Black, 0.25f);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
