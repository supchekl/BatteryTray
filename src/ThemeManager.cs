using System.Drawing;

namespace BatteryTray
{
    internal enum ThemeMode { System = 0, Light = 1, Dark = 2, Glass = 3 }

    // Палитры на токенах Fluent (WinUI theme resources).
    // Источник значений: learn.microsoft.com/visualstudio/extensibility/ux-guidelines/theme-color-token-reference
    internal static class ThemeManager
    {
        public static ThemeMode Mode = ThemeMode.System;

        public static bool IsGlass { get { return Mode == ThemeMode.Glass; } }
        public static bool IsDark { get; private set; }

        public static Color Surface;         // фон панели (SurfaceBackgroundFillDefault)
        public static Color SurfaceStroke;   // рамка панели (SurfaceStrokeFlyout): рисуем сами только на Win10
        public static Color TextPrimary;     // TextFillPrimary
        public static Color TextSecondary;   // TextFillSecondary
        public static Color TextTertiary;    // TextFillTertiary
        public static Color Divider;         // DividerStrokeDefault
        public static Color SubtleHover;     // SubtleFillSecondary
        public static Color SubtlePressed;   // SubtleFillTertiary
        public static Color AccentText;      // AccentTextFillPrimary (от системного акцента)
        public static Color Critical;        // SystemFillCritical
        public static Color Caution;         // SystemFillCaution

        public static string[] Names { get { return L.ThemeNames; } }

        public static void Load()
        {
            int? v = Settings.GetInt("ThemeMode");
            if (v.HasValue && v.Value >= 0 && v.Value <= 3)
            {
                Mode = (ThemeMode)v.Value;
            }
            else
            {
                // миграция со старого ключа: 0 светлая, 1 тёмная, 2 стекло
                int? old = Settings.GetInt("Theme");
                if (old == 1) Mode = ThemeMode.Dark;
                else if (old == 2) Mode = ThemeMode.Glass;
                else if (old == 0) Mode = ThemeMode.Light;
                else Mode = ThemeMode.System;
            }
            Apply();
        }

        public static void Save() { Settings.SetInt("ThemeMode", (int)Mode); }

        public static void Apply() { Apply(null); }

        // forceDark: для превью: «как в системе»/стекло нужной яркости независимо от темы Windows
        public static void Apply(bool? forceDark)
        {
            if (Mode == ThemeMode.Dark) IsDark = true;
            else if (Mode == ThemeMode.Light) IsDark = false;
            else IsDark = forceDark ?? !Settings.AppsUseLightTheme;

            if (IsDark)
            {
                Surface = Color.FromArgb(0x2C, 0x2C, 0x2C);
                SurfaceStroke = Color.FromArgb(0x33, 0, 0, 0);
                TextPrimary = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                TextSecondary = Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
                TextTertiary = Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF);
                Divider = Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF);
                SubtleHover = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
                SubtlePressed = Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF);
                Critical = Color.FromArgb(0xFF, 0x99, 0xA4);
                Caution = Color.FromArgb(0xFC, 0xE1, 0x00);
            }
            else
            {
                Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
                SurfaceStroke = Color.FromArgb(0x0F, 0, 0, 0);
                TextPrimary = Color.FromArgb(0xE4, 0, 0, 0);
                TextSecondary = Color.FromArgb(0x9E, 0, 0, 0);
                TextTertiary = Color.FromArgb(0x72, 0, 0, 0);
                Divider = Color.FromArgb(0x14, 0, 0, 0);
                SubtleHover = Color.FromArgb(0x0F, 0, 0, 0);
                SubtlePressed = Color.FromArgb(0x0A, 0, 0, 0);
                Critical = Color.FromArgb(0xC4, 0x2B, 0x1C);
                Caution = Color.FromArgb(0x9D, 0x5D, 0x00);
            }
            AccentText = Fluent.AccentText(IsDark);
        }

        // Цвет уровня заряда: обычный текст, ≤20%, предупреждение, ≤10%, критично
        public static Color BatteryColor(int pct)
        {
            if (pct <= 10) return Critical;
            if (pct <= 20) return Caution;
            return TextPrimary;
        }
    }
}
