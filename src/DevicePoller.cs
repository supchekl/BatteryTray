using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTray
{
    internal sealed class DeviceRow
    {
        public const string TransportBt = "Bluetooth";
        public const string TransportRadio = "Radio";       // свой радиоприёмник 2,4 ГГц
        public const string TransportXInput = "XInput";     // Xbox Wireless (адаптер или встроенный)
        public const string TransportUsb = "USB";
        public const string TransportDisplay = "Display";   // HDMI / DisplayPort

        public string Key = "";               // стабильный ключ строки (адрес / USB|имя / XInput|слот)
        public string AddressHex = "";        // 12 hex, верхний регистр (только Bluetooth)
        public string Name = "";
        public bool IsLe;
        public bool Connected;
        public int? Battery;                  // 0..100 или null
        public bool Approx;                   // заряд известен грубо (XInput: 4 уровня)
        public string BatterySource = "";
        public string Transport = TransportBt;
        public string Category = "";          // System.Devices.Category контейнера
        public DeviceKind? Kind;              // известен точно (например, от HID++)
        public string ContainerId = "";

        // Группа в списке: 0, Bluetooth, 1, радиоканал, 2, провод
        public int Group
        {
            get
            {
                if (Transport == TransportBt) return 0;
                if (Transport == TransportRadio || Transport == TransportXInput) return 1;
                return 2;
            }
        }

        public bool Wireless { get { return Group < 2; } }

        public string TransportLabel
        {
            get
            {
                if (Transport == TransportUsb) return "USB";
                if (Transport == TransportDisplay) return "HDMI / DisplayPort";
                if (Transport == TransportRadio) return L.Receiver24;
                if (Transport == TransportXInput) return "Xbox Wireless";
                return IsLe ? "Bluetooth LE" : "Bluetooth";
            }
        }

        public string BatteryText
        {
            get
            {
                if (Battery == null) return "";
                return (Approx ? "~" : "") + Battery.Value.ToString(CultureInfo.InvariantCulture) + "%";
            }
        }
    }

    internal sealed class DevicePoller
    {
        private sealed class GattCache { public int? Value; public DateTime At; }

        // Сопряжённые устройства: классический Bluetooth и Bluetooth LE
        private const string BtSelector =
            "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
            "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND " +
            "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

        private static readonly string[] AepProps = { "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired" };
        private static readonly Guid BatterySvcUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        private readonly Dictionary<string, GattCache> _gatt = new Dictionary<string, GattCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GattCache> _lastGood = new Dictionary<string, GattCache>(StringComparer.OrdinalIgnoreCase);

        public volatile string LastError = "";
        public bool IncludeWired = true;

        private List<LogitechHidpp.Result> _hidpp = new List<LogitechHidpp.Result>();
        private DateTime _hidppAt = DateTime.MinValue;

        private static readonly string[] ContainerProps =
        {
            "System.Devices.Connected", "System.Devices.LocalMachine", "System.Devices.Category", "System.Devices.Manufacturer"
        };

        // Признаки собственного радиоприёмника (не Bluetooth)
        private static readonly string[] RadioWords =
        {
            "receiver", "dongle", "wireless", "2.4g", "2,4", "unifying", "lightspeed", "bolt", "hyperspeed",
            "приемник", "приёмник", "беспровод"
        };

        private static readonly string[] SkipWords = { "hub", "концентратор", "root" };

        private static string AddrFromAepId(string id)
        {
            int i = id.LastIndexOf('-');
            if (i < 0 || i == id.Length - 1) return null;
            string a = id.Substring(i + 1).Trim();
            string[] parts = a.Split(':');
            if (parts.Length != 6) return null;
            foreach (string p in parts)
                if (p.Length != 2 || !Uri.IsHexDigit(p[0]) || !Uri.IsHexDigit(p[1])) return null;
            return a.Replace(":", "").ToUpperInvariant();
        }

        private static async Task<List<DeviceInformation>> QueryAepsAsync()
        {
            DeviceInformationCollection col = await WinRtAsync.AsTask(
                DeviceInformation.FindAllAsync(BtSelector, AepProps, DeviceInformationKind.AssociationEndpoint));
            var list = new List<DeviceInformation>();
            foreach (DeviceInformation d in col) list.Add(d);
            return list;
        }

        private static async Task<int?> ReadGattBatteryAsync(string hex12)
        {
            ulong address = ulong.Parse(hex12, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            BluetoothLEDevice ble = await WinRtAsync.AsTask(BluetoothLEDevice.FromBluetoothAddressAsync(address));
            if (ble == null) return null;
            try
            {
                GattDeviceServicesResult svcRes = await WinRtAsync.AsTask(ble.GetGattServicesForUuidAsync(BatterySvcUuid, BluetoothCacheMode.Uncached));
                if (svcRes == null || svcRes.Status != GattCommunicationStatus.Success || svcRes.Services.Count == 0) return null;
                GattDeviceService svc = svcRes.Services[0];
                try
                {
                    GattCharacteristicsResult chRes = await WinRtAsync.AsTask(svc.GetCharacteristicsForUuidAsync(BatteryLevelUuid));
                    if (chRes == null || chRes.Status != GattCommunicationStatus.Success || chRes.Characteristics.Count == 0) return null;
                    GattReadResult readRes = await WinRtAsync.AsTask(chRes.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached));
                    if (readRes == null || readRes.Status != GattCommunicationStatus.Success || readRes.Value == null || readRes.Value.Length == 0) return null;
                    int v = DataReader.FromBuffer(readRes.Value).ReadByte();
                    return v <= 100 ? (int?)v : null;
                }
                finally { svc.Dispose(); }
            }
            finally { ble.Dispose(); }
        }

        private static async Task<int?> ReadGattBatteryWithTimeout(string hex12)
        {
            try
            {
                Task<int?> t = ReadGattBatteryAsync(hex12);
                Task done = await Task.WhenAny(t, Task.Delay(8000));
                if (done != t)
                {
                    AppLog.Line("gatt timeout " + hex12);
                    return null;
                }
                return await t;
            }
            catch (Exception ex)
            {
                AppLog.Line("gatt error " + hex12 + ": " + ex.Message);
                return null;
            }
        }

        private async Task<int?> GetBatteryAsync(DeviceRow row, List<PnpNative.BtNode> nodes)
        {
            string hex = row.AddressHex;

            // Свойство Windows: то же число, что в «Параметрах». Дёшево, без радиообмена.
            foreach (PnpNative.BtNode n in nodes)
            {
                if (!string.Equals(n.AddressHex, hex, StringComparison.OrdinalIgnoreCase)) continue;
                int? v = PnpNative.GetBatteryProp(n.DevInst);
                if (v != null)
                {
                    row.BatterySource = "Windows";
                    Remember(hex, v.Value);
                    return v;
                }
            }

            // BLE: прямое чтение Battery Service (0x180F/0x2A19), не чаще раза в 90 с
            if (row.IsLe)
            {
                GattCache c;
                DateTime now = DateTime.UtcNow;
                if (!_gatt.TryGetValue(hex, out c) || (now - c.At).TotalSeconds >= 90)
                {
                    c = new GattCache();
                    c.Value = await ReadGattBatteryWithTimeout(hex);
                    c.At = now;
                    _gatt[hex] = c;
                }
                if (c.Value != null)
                {
                    row.BatterySource = "GATT";
                    Remember(hex, c.Value.Value);
                    return c.Value;
                }
            }

            // Кратковременный сбой чтения, до 10 минут показываем последнее известное значение
            GattCache last;
            if (_lastGood.TryGetValue(hex, out last) && (DateTime.UtcNow - last.At).TotalMinutes < 10)
            {
                row.BatterySource = "cache";
                return last.Value;
            }
            return null;
        }

        private void Remember(string hex, int value)
        {
            var e = new GattCache();
            e.Value = value;
            e.At = DateTime.UtcNow;
            _lastGood[hex] = e;
        }

        private static DeviceRow GetOrAdd(Dictionary<string, DeviceRow> rows, string hex)
        {
            DeviceRow row;
            if (!rows.TryGetValue(hex, out row))
            {
                row = new DeviceRow();
                row.Key = hex;
                row.AddressHex = hex;
                rows[hex] = row;
            }
            return row;
        }

        private static async Task<List<DeviceInformation>> QueryContainersAsync()
        {
            DeviceInformationCollection col;
            try
            {
                col = await WinRtAsync.AsTask(DeviceInformation.FindAllAsync(
                    "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False", ContainerProps, DeviceInformationKind.DeviceContainer));
            }
            catch (ArgumentException)
            {
                // запасной вариант без фильтра, отфильтруем сами
                col = await WinRtAsync.AsTask(DeviceInformation.FindAllAsync("", ContainerProps, DeviceInformationKind.DeviceContainer));
            }
            var list = new List<DeviceInformation>();
            foreach (DeviceInformation d in col) list.Add(d);
            return list;
        }

        private static bool PropBool(DeviceInformation d, string name)
        {
            object o;
            return d.Properties.TryGetValue(name, out o) && o is bool && (bool)o;
        }

        private static string PropText(DeviceInformation d, string name)
        {
            object o;
            if (!d.Properties.TryGetValue(name, out o) || o == null) return "";
            string[] arr = o as string[];
            return arr != null ? string.Join(";", arr) : o.ToString();
        }

        private static bool ContainsAny(string text, string[] words)
        {
            foreach (string w in words)
                if (text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // USB\VID_046D&PID_C52B… → 0xC52B, если VID совпадает
        private static ushort PidOf(List<string> ids, string vid)
        {
            foreach (string id in ids)
            {
                string u = id.ToUpperInvariant();
                int v = u.IndexOf("VID_" + vid, StringComparison.Ordinal);
                int p = u.IndexOf("PID_", v < 0 ? 0 : v, StringComparison.Ordinal);
                ushort pid;
                if (v >= 0 && p >= 0 && p + 8 <= u.Length &&
                    ushort.TryParse(u.Substring(p + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                    return pid;
            }
            return 0;
        }

        private async Task AddContainersAsync(List<DeviceRow> result, Dictionary<string, PnpNative.ContainerNodes> snapshot)
        {
            // BT-адрес → строка списка (категорию контейнера используем для иконки)
            var btByAddr = new Dictionary<string, DeviceRow>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceRow r in result)
                if (r.AddressHex.Length > 0) btByAddr[r.AddressHex] = r;

            foreach (DeviceInformation d in await QueryContainersAsync())
            {
                if (PropBool(d, "System.Devices.LocalMachine")) continue;    // сам компьютер и встроенное железо
                string cid = d.Id.Trim('{', '}').ToLowerInvariant();
                PnpNative.ContainerNodes nodes;
                if (!snapshot.TryGetValue(cid, out nodes)) continue;          // нет присутствующих узлов: не подключено
                string category = PropText(d, "System.Devices.Category");
                string name = (d.Name ?? "").Trim();

                bool bt = false, usb = false, display = false;
                foreach (string id in nodes.InstanceIds)
                {
                    if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                    {
                        bt = true;
                        string addr = PnpNative.AddressFromInstanceId(id);
                        DeviceRow row;
                        if (addr != null && btByAddr.TryGetValue(addr, out row))
                        {
                            row.Category = category;
                            row.ContainerId = cid;
                        }
                    }
                    else if (id.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || id.StartsWith("HID", StringComparison.OrdinalIgnoreCase)) usb = true;
                    else if (id.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) display = true;
                }
                if (bt) continue;                         // Bluetooth уже в списке
                if (!usb && !display) continue;           // программные и сетевые устройства
                if (name.Length == 0 || ContainsAny(name, SkipWords)) continue;

                ushort logiPid = PidOf(nodes.InstanceIds, "046D");
                bool sony = PidOf(nodes.InstanceIds, "054C") != 0;   // DualSense по кабелю называется «Wireless Controller»
                var dr = new DeviceRow();
                dr.Key = "C|" + cid;
                dr.ContainerId = cid;
                dr.Name = name;
                dr.Category = category;
                dr.Connected = true;
                if (display && !usb) dr.Transport = DeviceRow.TransportDisplay;
                else if (LogitechHidpp.IsReceiverPid(logiPid) || (!sony && ContainsAny(name, RadioWords))) dr.Transport = DeviceRow.TransportRadio;
                else dr.Transport = DeviceRow.TransportUsb;
                if (nodes.Battery != null)
                {
                    dr.Battery = nodes.Battery;
                    dr.BatterySource = "Windows";
                }
                result.Add(dr);
            }
        }

        private void ApplyHidpp(List<DeviceRow> result)
        {
            if ((DateTime.UtcNow - _hidppAt).TotalSeconds >= 60)
            {
                var radio = new HashSet<string>(result.Where(r => r.Transport == DeviceRow.TransportRadio).Select(r => r.ContainerId),
                                                 StringComparer.OrdinalIgnoreCase);
                _hidpp = LogitechHidpp.Scan(cid => radio.Contains(cid));
                _hidppAt = DateTime.UtcNow;
                foreach (LogitechHidpp.Result h in _hidpp)
                    AppLog.Line("hid++: " + h.Name + " idx=" + h.Index + " battery=" + (h.Battery == null ? "-" : h.Battery.Value.ToString()));
            }

            foreach (IGrouping<string, LogitechHidpp.Result> g in _hidpp.GroupBy(h => h.ContainerId))
            {
                DeviceRow host = result.FirstOrDefault(r => string.Equals(r.ContainerId, g.Key, StringComparison.OrdinalIgnoreCase));
                if (host == null) continue;                          // приёмник/устройство отключили
                List<LogitechHidpp.Result> items = g.ToList();
                if (items.Count == 1 && !items[0].BehindReceiver)
                {
                    // само устройство (по кабелю или Lightspeed в режиме провода)
                    host.Battery = items[0].Battery ?? host.Battery;
                    host.Approx = items[0].Approx;
                    host.BatterySource = "HID++";
                    continue;
                }
                // за приёмником: вместо строки «USB Receiver», сами устройства
                result.Remove(host);
                foreach (LogitechHidpp.Result h in items)
                {
                    var row = new DeviceRow();
                    row.Key = "L|" + g.Key + "|" + h.Index;
                    row.ContainerId = g.Key;
                    row.Name = h.Name.Length > 0 ? h.Name : host.Name;
                    row.Kind = h.Kind == DeviceKind.Other ? (DeviceKind?)null : h.Kind;
                    row.Category = host.Category;
                    row.Transport = DeviceRow.TransportRadio;
                    row.Connected = true;
                    row.Battery = h.Battery;
                    row.Approx = h.Approx;
                    row.BatterySource = "HID++";
                    result.Add(row);
                }
            }
        }

        public async Task<List<DeviceRow>> PollAsync()
        {
            string error = "";
            var rows = new Dictionary<string, DeviceRow>(StringComparer.OrdinalIgnoreCase);

            // Сопряжённые Bluetooth-устройства (WinRT: имена и признак подключения)
            try
            {
                foreach (DeviceInformation d in await QueryAepsAsync())
                {
                    string hex = AddrFromAepId(d.Id);
                    if (hex == null) continue;
                    DeviceRow row = GetOrAdd(rows, hex);
                    if (!string.IsNullOrEmpty(d.Name)) row.Name = d.Name;
                    if (d.Id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase)) row.IsLe = true;
                    object o;
                    if (d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out o) && o is bool && (bool)o) row.Connected = true;
                }
            }
            catch (Exception ex)
            {
                error = "Bluetooth: " + ex.Message;
                AppLog.Line("AEP poll error: " + ex.Message);
            }

            // PnP: какие адреса реально присутствуют сейчас
            List<PnpNative.BtNode> nodes = new List<PnpNative.BtNode>();
            try
            {
                nodes = PnpNative.EnumeratePresentBtNodes();
                foreach (PnpNative.BtNode n in nodes)
                {
                    // узлы профилей есть и у отключённых сопряжённых устройств, подключением их не считаем
                    if (n.IsTransport && !rows.ContainsKey(n.AddressHex)) continue;
                    DeviceRow row = GetOrAdd(rows, n.AddressHex);
                    if (n.IsLe) row.IsLe = true;
                    if (string.IsNullOrEmpty(row.Name) && !string.IsNullOrEmpty(n.FriendlyName)) row.Name = n.FriendlyName;
                    if (!n.IsTransport) row.Connected = true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Line("PnP poll error: " + ex.Message);
            }

            // Заряд: только для подключённых Bluetooth-устройств
            foreach (DeviceRow row in rows.Values)
            {
                if (!row.Connected) continue;
                try { row.Battery = await GetBatteryAsync(row, nodes); }
                catch (Exception ex) { AppLog.Line("battery error " + row.AddressHex + ": " + ex.Message); }
            }

            // Слияние дубликатов по имени (классика + LE у одной гарнитуры)
            var result = new List<DeviceRow>();
            foreach (IGrouping<string, DeviceRow> g in rows.Values.GroupBy(
                         r => string.IsNullOrEmpty(r.Name) ? "\u0001" + r.AddressHex : r.Name, StringComparer.OrdinalIgnoreCase))
            {
                List<DeviceRow> items = g.ToList();
                if (items.Count == 1) { result.Add(items[0]); continue; }
                DeviceRow best = items.FirstOrDefault(x => x.Connected && x.Battery != null)
                                 ?? items.FirstOrDefault(x => x.Connected)
                                 ?? items[0];
                best.Connected = items.Any(x => x.Connected);
                best.IsLe = best.IsLe || items.Any(x => x.IsLe);
                result.Add(best);
            }
            foreach (DeviceRow r in result)
                if (string.IsNullOrEmpty(r.Name)) r.Name = L.DeviceN + r.AddressHex;

            // Все остальные внешние устройства: провод и свои радиоприёмники (контейнеры Windows)
            Dictionary<string, PnpNative.ContainerNodes> snapshot = null;
            try { snapshot = PnpNative.SnapshotContainers(); }
            catch (Exception ex) { AppLog.Line("container snapshot error: " + ex.Message); }
            if (snapshot != null)
            {
                try { await AddContainersAsync(result, snapshot); }
                catch (Exception ex) { AppLog.Line("container poll error: " + ex.Message); }
            }

            // Logitech HID++: заряд устройств за радиоприёмником (раз в минуту, опрос небыстрый)
            try { ApplyHidpp(result); }
            catch (Exception ex) { AppLog.Line("hid++ poll error: " + ex.Message); }

            // Геймпады Xbox: заряд из XInput (Windows не отдаёт его через свойства устройств)
            try
            {
                foreach (XInputNative.Pad pad in XInputNative.Query())
                {
                    DeviceRow target = result.FirstOrDefault(r => r.Connected && r.Battery == null &&
                        Icons.DetectKind(r) == DeviceKind.Gamepad && r.Name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (target == null)
                    {
                        target = new DeviceRow();
                        target.Key = "XInput|" + pad.Slot;
                        target.Name = L.XboxController + (pad.Slot > 0 ? " " + (pad.Slot + 1) : "");
                        target.Transport = DeviceRow.TransportXInput;
                        target.Connected = true;
                        result.Add(target);
                    }
                    else if (target.Transport == DeviceRow.TransportUsb)
                    {
                        target.Transport = DeviceRow.TransportXInput;   // беспроводной через адаптер
                    }
                    target.Battery = pad.Percent;
                    target.Approx = true;
                    target.BatterySource = "XInput";
                }
            }
            catch (Exception ex) { AppLog.Line("xinput poll error: " + ex.Message); }

            if (!IncludeWired)
                result.RemoveAll(r => !r.Wireless && r.Battery == null);

            LastError = error;
            return result
                .OrderBy(r => r.Group)
                .ThenBy(r => r.Connected ? 0 : 1)
                .ThenBy(r => r.Battery == null ? 1 : 0)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
