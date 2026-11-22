using System.Globalization;

namespace BatteryTray
{
    internal enum UiLanguage { Auto = 0, Russian = 1, English = 2 }

    // Строки интерфейса. Язык берётся из настроек, по умолчанию по языку Windows.
    internal static class L
    {
        public static UiLanguage Setting = UiLanguage.Auto;

        public static bool Ru
        {
            get
            {
                if (Setting == UiLanguage.Russian) return true;
                if (Setting == UiLanguage.English) return false;
                string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return lang == "ru" || lang == "uk" || lang == "be" || lang == "kk";
            }
        }

        public static void Load()
        {
            int? v = Settings.GetInt("Language");
            Setting = v.HasValue && v.Value >= 0 && v.Value <= 2 ? (UiLanguage)v.Value : UiLanguage.Auto;
        }

        public static void Save() { Settings.SetInt("Language", (int)Setting); }

        private static string T(string ru, string en) { return Ru ? ru : en; }

        // окно со списком
        public static string Title { get { return T("Устройства", "Devices"); } }
        public static string GroupBluetooth { get { return "Bluetooth"; } }
        public static string GroupRadio { get { return T("Радиоканал", "Wireless receiver"); } }
        public static string GroupWired { get { return T("По проводу", "Wired"); } }
        public static string Updating { get { return T("Обновление…", "Updating…"); } }
        public static string UpdatedAt(string time) { return T("Обновлено в ", "Updated at ") + time; }
        public static string ListError { get { return T("Не удалось получить список", "Couldn't get the device list"); } }
        public static string NoDevices { get { return T("Нет подключённых устройств", "No connected devices"); } }
        public static string NoDevicesHint { get { return T("Подключите наушники, мышь или геймпад по Bluetooth, через приёмник или кабелем",
                                                            "Connect headphones, a mouse or a gamepad via Bluetooth, a receiver or a cable"); } }
        public static string MoreInSettings(int n) { return T("И ещё " + n + " в параметрах Windows", n + " more in Windows Settings"); }
        public static string NotConnected { get { return T("Не подключено", "Not connected"); } }
        public static string NoBatteryInfo { get { return T("заряд не сообщается", "battery level not reported"); } }
        public static string Unnamed { get { return T("Без имени", "Unnamed"); } }
        public static string BluetoothSettings { get { return T("Параметры Bluetooth", "Bluetooth settings"); } }

        // типы подключения и имена по умолчанию
        public static string Receiver24 { get { return T("Радиоприёмник 2,4 ГГц", "2.4 GHz receiver"); } }
        public static string XboxController { get { return T("Геймпад Xbox", "Xbox controller"); } }
        public static string DeviceN { get { return T("Устройство ", "Device "); } }

        // темы
        public static string[] ThemeNames
        {
            get
            {
                return Ru ? new[] { "Как в системе", "Светлая", "Тёмная", "Стекло (Acrylic)" }
                          : new[] { "System default", "Light", "Dark", "Glass (Acrylic)" };
            }
        }

        // меню
        public static string MenuOpen { get { return T("Панель устройств", "Show devices"); } }
        public static string MenuRefresh { get { return T("Обновить сейчас", "Refresh now"); } }
        public static string MenuTheme { get { return T("Тема панели", "Theme"); } }
        public static string MenuLanguage { get { return T("Язык", "Language"); } }
        public static string LanguageAuto { get { return T("Как в системе", "System default"); } }
        public static string MenuTaskbarLabel { get { return T("Подпись на панели задач", "Show on taskbar"); } }
        public static string MenuShowWired { get { return T("Показывать проводные устройства", "Show wired devices"); } }
        public static string MenuNotifications { get { return T("Уведомления", "Notifications"); } }
        public static string MenuTestNotification { get { return T("Проверить уведомление", "Test notification"); } }
        public static string NotificationsWork { get { return T("Уведомления работают", "Notifications are working"); } }
        public static string MenuAutostart { get { return T("Запускать вместе с Windows", "Start with Windows"); } }
        public static string MenuOpenLog { get { return T("Открыть папку с журналом", "Open log folder"); } }
        public static string MenuExit { get { return T("Выход", "Exit"); } }

        // уведомления
        public static string Connected { get { return T("Подключено", "Connected"); } }
        public static string Disconnected { get { return T("Отключено", "Disconnected"); } }
        public static string LowBattery(string pct) { return T("Низкий заряд: " + pct + ", пора зарядить", "Low battery: " + pct + ", time to charge"); }
        public static string CriticalBattery(string pct) { return T("Почти разряжено: " + pct, "Almost empty: " + pct); }
    }
}
