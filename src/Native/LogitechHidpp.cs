using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray
{
    // Заряд беспроводных устройств Logitech (приёмники Unifying, Lightspeed, Bolt и устройства по кабелю)
    // по протоколу HID++ 2.0. Обмен идёт через вендорскую HID-коллекцию (Usage Page 0xFF00) длинными
    // отчётами 0x11 по 20 байт. Нужные функции:
    //   0x0000 IRoot: номер функции по её ID
    //   0x0005 DeviceName: имя и тип устройства
    //   0x1004 UnifiedBattery: процент (новые устройства)
    //   0x1000 BatteryStatus: процент (старые устройства)
    //   0x1001 BatteryVoltage: напряжение, переводим в процент (игровые G-серии)
    internal static class LogitechHidpp
    {
        public sealed class Result
        {
            public string ContainerId = "";
            public bool BehindReceiver;      // устройство за радиоприёмником (иначе это сам контейнер)
            public int Index;
            public string Name = "";
            public int? Battery;
            public bool Approx;
            public DeviceKind Kind = DeviceKind.Other;
        }

        private const ushort VidLogitech = 0x046D;
        private const byte ReportLong = 0x11;
        private const byte SoftwareId = 0x0B;
        private const int TimeoutMs = 300;

        // Lightspeed: одно устройство на индексе 1. Unifying / Bolt / Nano: до 6 устройств.
        private static readonly HashSet<ushort> LightspeedReceivers = new HashSet<ushort> { 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D };
        private static readonly HashSet<ushort> MultiReceivers = new HashSet<ushort> { 0xC52B, 0xC532, 0xC52F, 0xC531, 0xC534, 0xC537, 0xC548 };

        public static bool IsReceiverPid(ushort pid) { return LightspeedReceivers.Contains(pid) || MultiReceivers.Contains(pid); }

        // ---------- WinAPI ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                          NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                          NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, ref PnpNative.SP_DEVINFO_DATA devInfo);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_RW = 0xC0000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        private sealed class HidPath { public string Path = ""; public string ContainerId = ""; }

        // Пути к HID-интерфейсам Logitech и контейнеры, которым они принадлежат
        private static List<HidPath> FindLogitechInterfaces()
        {
            var list = new List<HidPath>();
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == PnpNative.INVALID) return list;
            try
            {
                for (int i = 0; ; i++)
                {
                    var ifd = new SP_DEVICE_INTERFACE_DATA();
                    ifd.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref ifd)) break;

                    var dev = new PnpNative.SP_DEVINFO_DATA();
                    dev.cbSize = Marshal.SizeOf(typeof(PnpNative.SP_DEVINFO_DATA));
                    int required;
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out required, ref dev);
                    if (required <= 0 || required > 4096) continue;
                    IntPtr buf = Marshal.AllocHGlobal(required);
                    try
                    {
                        // cbSize структуры SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 в x64, 6 в x86
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, required, out required, ref dev)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (path == null || path.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var hp = new HidPath();
                        hp.Path = path;
                        hp.ContainerId = PnpNative.GetContainerId(dev.DevInst) ?? "";
                        list.Add(hp);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        // ---------- Обмен отчётами ----------

        private sealed class Channel : IDisposable
        {
            private readonly FileStream _fs;
            private readonly int _inLen;
            private Task<int> _pending;
            private byte[] _pendingBuf;
            public bool Dead;

            public Channel(SafeFileHandle h, int inLen)
            {
                _fs = new FileStream(h, FileAccess.ReadWrite, 1, true);
                _inLen = inLen;
            }

            public bool Write(byte[] report)
            {
                try
                {
                    Task t = _fs.WriteAsync(report, 0, report.Length);
                    if (t.Wait(TimeoutMs)) return true;
                    Observe(t);
                }
                catch { }
                Dead = true;
                return false;
            }

            // Один входной отчёт или null по таймауту. Незавершённое чтение переживает таймаут
            // и используется следующим вызовом: два чтения одновременно не запускаем.
            public byte[] Read(int timeoutMs)
            {
                if (Dead) return null;
                try
                {
                    if (_pending == null)
                    {
                        _pendingBuf = new byte[_inLen];
                        _pending = _fs.ReadAsync(_pendingBuf, 0, _inLen);
                    }
                    if (!_pending.Wait(timeoutMs)) return null;
                    int n = _pending.Result;
                    byte[] r = _pendingBuf;
                    _pending = null;
                    return n > 0 ? r : null;
                }
                catch
                {
                    Dead = true;
                    return null;
                }
            }

            public void Dispose()
            {
                if (_pending != null) Observe(_pending);
                try { _fs.Dispose(); } catch { }
            }

            private static void Observe(Task t)
            {
                t.ContinueWith(x => { var unused = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        // Запрос к функции featureIndex/function; ответ, 20 байт или null (ошибка/нет ответа)
        private static byte[] Request(Channel ch, byte dev, byte feature, byte function, params byte[] args)
        {
            var req = new byte[20];
            req[0] = ReportLong;
            req[1] = dev;
            req[2] = feature;
            req[3] = (byte)((function << 4) | SoftwareId);
            for (int i = 0; i < args.Length && i < 16; i++) req[4 + i] = args[i];
            if (!ch.Write(req)) return null;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (!ch.Dead)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                byte[] r = ch.Read(left);
                if (r == null) return null;
                if (r.Length < 20 || r[0] != ReportLong || r[1] != dev) continue;            // чужой отчёт или уведомление
                if (r[2] == feature && r[3] == req[3]) return r;
                if (r[2] == 0xFF && r[3] == feature && r[4] == req[3]) return null;          // ошибка HID++ 2.0
            }
            return null;
        }

        // Номер функции; 0: не поддерживается, -1, устройство не ответило
        private static int FeatureIndex(Channel ch, byte dev, ushort featureId)
        {
            byte[] r = Request(ch, dev, 0x00, 0, (byte)(featureId >> 8), (byte)featureId);
            return r == null ? -1 : r[4];
        }

        private static byte Idx(int v) { return v > 0 ? (byte)v : (byte)0; }

        // ---------- Опрос ----------

        public static List<Result> Scan(Func<string, bool> isReceiverContainer)
        {
            var results = new List<Result>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<HidPath> paths;
            try { paths = FindLogitechInterfaces(); }
            catch (Exception ex) { AppLog.Line("hid++ enum error: " + ex.Message); return results; }

            foreach (HidPath hp in paths)
            {
                if (done.Contains(hp.ContainerId)) continue;
                SafeFileHandle h = CreateFileW(hp.Path, GENERIC_RW, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }      // коллекции мыши и клавиатуры Windows не открывает, это нормально
                try
                {
                    var attr = new HIDD_ATTRIBUTES();
                    attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != VidLogitech) { h.Dispose(); continue; }

                    IntPtr pp;
                    if (!HidD_GetPreparsedData(h, out pp)) { h.Dispose(); continue; }
                    var caps = new HIDP_CAPS();
                    HidP_GetCaps(pp, ref caps);
                    HidD_FreePreparsedData(pp);
                    // нужна коллекция длинных отчётов HID++: Usage Page 0xFF00, Usage 0x0002, 20 байт
                    if (caps.UsagePage != 0xFF00 || caps.Usage != 0x0002 || caps.OutputReportByteLength != 20) { h.Dispose(); continue; }

                    done.Add(hp.ContainerId);
                    bool receiver = IsReceiverPid(attr.ProductID) || (isReceiverContainer != null && isReceiverContainer(hp.ContainerId));
                    int[] indices = !receiver ? new[] { 0xFF }
                                  : LightspeedReceivers.Contains(attr.ProductID) ? new[] { 1 }
                                  : new[] { 1, 2, 3, 4, 5, 6 };
                    using (var ch = new Channel(h, Math.Max((int)caps.InputReportByteLength, 20)))
                    {
                        foreach (int idx in indices)
                        {
                            if (ch.Dead) break;
                            Result r = QueryDevice(ch, (byte)idx);
                            if (r == null) continue;
                            r.ContainerId = hp.ContainerId;
                            r.BehindReceiver = receiver;
                            results.Add(r);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Line("hid++ error: " + ex.Message);
                    h.Dispose();
                }
            }
            return results;
        }

        private static Result QueryDevice(Channel ch, byte dev)
        {
            // IRoot.GetFeature(DeviceName). Не ответило, значит устройства нет или оно спит
            // Нет ответа на первый запрос: на этом индексе никого нет
            int first = FeatureIndex(ch, dev, 0x0005);
            if (first < 0 || ch.Dead) return null;
            byte nameIdx = Idx(first);
            byte uniIdx = Idx(FeatureIndex(ch, dev, 0x1004));
            byte statIdx = uniIdx == 0 ? Idx(FeatureIndex(ch, dev, 0x1000)) : (byte)0;
            byte voltIdx = uniIdx == 0 && statIdx == 0 ? Idx(FeatureIndex(ch, dev, 0x1001)) : (byte)0;
            if (nameIdx == 0 && uniIdx == 0 && statIdx == 0 && voltIdx == 0) return null;

            var res = new Result();
            res.Index = dev;
            if (nameIdx != 0)
            {
                res.Name = ReadName(ch, dev, nameIdx);
                byte[] t = Request(ch, dev, nameIdx, 2);
                if (t != null) res.Kind = KindFromType(t[4]);
            }

            if (uniIdx != 0)
            {
                byte[] r = Request(ch, dev, uniIdx, 1);   // get_status
                if (r != null)
                {
                    if (r[4] > 0 && r[4] <= 100) res.Battery = r[4];
                    else if (r[5] != 0)
                    {
                        // устройство сообщает только уровень: full / good / low / critical
                        res.Approx = true;
                        res.Battery = (r[5] & 0x08) != 0 ? 100 : (r[5] & 0x04) != 0 ? 60 : (r[5] & 0x02) != 0 ? 20 : 5;
                    }
                }
            }
            else if (statIdx != 0)
            {
                byte[] r = Request(ch, dev, statIdx, 0);   // GetBatteryLevelStatus
                if (r != null && r[4] > 0 && r[4] <= 100) res.Battery = r[4];
            }
            else if (voltIdx != 0)
            {
                byte[] r = Request(ch, dev, voltIdx, 0);   // GetBatteryInfo: напряжение в мВ (big-endian)
                if (r != null)
                {
                    int mv = (r[4] << 8) | r[5];
                    if (mv > 2500 && mv < 5000) { res.Battery = PercentFromMillivolts(mv); res.Approx = true; }
                }
            }
            return res;
        }

        private static string ReadName(Channel ch, byte dev, byte idx)
        {
            byte[] c = Request(ch, dev, idx, 0);       // getDeviceNameCount
            if (c == null) return "";
            int len = Math.Min((int)c[4], 64);
            var sb = new StringBuilder();
            while (sb.Length < len)
            {
                byte[] r = Request(ch, dev, idx, 1, (byte)sb.Length);   // getDeviceName(charIndex)
                if (r == null) break;
                int before = sb.Length;
                for (int i = 4; i < 20 && sb.Length < len; i++)
                {
                    if (r[i] == 0) { len = sb.Length; break; }
                    sb.Append((char)r[i]);
                }
                if (sb.Length == before) break;
            }
            return sb.ToString().Trim();
        }

        private static DeviceKind KindFromType(byte t)
        {
            switch (t)
            {
                case 0: case 2: return DeviceKind.Keyboard;
                case 3: case 4: case 5: return DeviceKind.Mouse;
                case 8: return DeviceKind.Headphones;
                case 9: return DeviceKind.Camera;
                case 10: case 11: case 12: case 17: case 18: return DeviceKind.Gamepad;
                case 14: return DeviceKind.Speaker;
                default: return DeviceKind.Other;
            }
        }

        // Типичная разрядная кривая Li-ion аккумулятора мышей Logitech (по таблицам Solaar)
        private static readonly int[] CurveMv = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3500 };

        private static int PercentFromMillivolts(int mv)
        {
            if (mv >= CurveMv[0]) return 100;
            for (int i = 1; i < CurveMv.Length; i++)
            {
                if (mv >= CurveMv[i])
                {
                    double t = (double)(mv - CurveMv[i]) / (CurveMv[i - 1] - CurveMv[i]);
                    return (int)Math.Round(100 - i * 10 + t * 10);
                }
            }
            return 0;
        }
    }
}
