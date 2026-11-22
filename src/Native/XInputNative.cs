using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BatteryTray
{
    // Геймпады Xbox (USB-приёмник, Bluetooth, кабель) через XInput
    internal static class XInputNative
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(int dwUserIndex, [Out] byte[] pState);

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetBatteryInformation(int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

        private const byte BATTERY_DEVTYPE_GAMEPAD = 0;
        private const byte BATTERY_TYPE_DISCONNECTED = 0;
        private const byte BATTERY_TYPE_WIRED = 1;
        private const byte BATTERY_TYPE_UNKNOWN = 0xFF;

        private static bool _unavailable;

        public sealed class Pad
        {
            public int Slot;
            public int Percent;   // приблизительно: XInput отдаёт 4 уровня
        }

        // Уровни EMPTY/LOW/MEDIUM/FULL → проценты
        private static readonly int[] LevelPct = new[] { 5, 30, 65, 100 };

        public static List<Pad> Query()
        {
            var list = new List<Pad>();
            if (_unavailable) return list;
            try
            {
                var state = new byte[16];
                for (int i = 0; i < 4; i++)
                {
                    if (XInputGetState(i, state) != 0) continue;   // ERROR_DEVICE_NOT_CONNECTED
                    XINPUT_BATTERY_INFORMATION bi;
                    if (XInputGetBatteryInformation(i, BATTERY_DEVTYPE_GAMEPAD, out bi) != 0) continue;
                    if (bi.BatteryType == BATTERY_TYPE_DISCONNECTED || bi.BatteryType == BATTERY_TYPE_WIRED ||
                        bi.BatteryType == BATTERY_TYPE_UNKNOWN || bi.BatteryLevel > 3) continue;
                    var p = new Pad();
                    p.Slot = i;
                    p.Percent = LevelPct[bi.BatteryLevel];
                    list.Add(p);
                }
            }
            catch (DllNotFoundException) { _unavailable = true; }
            catch (EntryPointNotFoundException) { _unavailable = true; }
            catch (Exception ex) { AppLog.Line("xinput error: " + ex.Message); }
            return list;
        }
    }
}
