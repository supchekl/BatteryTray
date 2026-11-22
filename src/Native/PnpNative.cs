using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray
{
    // Низкоуровневый доступ к PnP (SetupAPI + CfgMgr32)
    internal static class PnpNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVPROPKEY { public Guid Fmtid; public uint Pid; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;   // DEVINST: DWORD, не IntPtr
            public IntPtr Reserved;
        }

        internal static readonly IntPtr INVALID = new IntPtr(-1);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string Enumerator, IntPtr HwndParent, uint Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_IDW(uint DevInst, StringBuilder Buffer, int BufferLen, int Flags);

        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_DevNode_PropertyW(uint DevInst, ref DEVPROPKEY Key, out uint PropType, byte[] Buffer, ref uint BufferSize, uint Flags);

        [DllImport("CfgMgr32.dll")]
        internal static extern int CM_Get_ParentW(out uint ParentDeviceInst, uint DeviceInst, uint Flags);

        internal const uint DIGCF_PRESENT = 0x2;
        internal const uint DIGCF_ALLCLASSES = 0x4;
        internal const uint SPDRP_DEVICEDESC = 0x0;
        internal const uint SPDRP_FRIENDLYNAME = 0xC;
        internal const int CR_SUCCESS = 0;
        internal const int CR_BUFFER_SMALL = 0x1A;

        internal static readonly Guid FmtFriendlyName = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        internal const uint PidFriendlyName = 14;

        // DEVPKEY_Bluetooth_Battery: заряд, который Windows показывает в «Параметры → Bluetooth».
        // Ставится на узел профиля Hands-Free (классика) или на BTHLE\Dev_* (LE).
        internal static readonly Guid FmtBattery = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
        internal const uint PidBattery = 2;

        public sealed class BtNode
        {
            public string InstanceId = "";
            public string AddressHex = "";   // 12 hex, верхний регистр
            public bool IsLe;
            public bool IsTransport;         // узел профиля/сервиса (…_ADDR_C00000000)
            public string FriendlyName = "";
            public uint DevInst;
        }

        private static bool IsHex12(string s)
        {
            if (s.Length != 12) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        public static string GetStringProp(uint devInst, Guid fmtid, uint pid)
        {
            uint type;
            byte[] buf = GetRawProp(devInst, fmtid, pid, out type);
            if (buf == null || buf.Length < 2) return null;
            string s = Encoding.Unicode.GetString(buf);
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            return s.Length > 0 ? s : null;
        }

        public static byte[] GetRawProp(uint devInst, Guid fmtid, uint pid, out uint propType)
        {
            propType = 0;
            try
            {
                var key = new DEVPROPKEY();
                key.Fmtid = fmtid;
                key.Pid = pid;
                uint type;
                uint size = 512;
                byte[] buf = new byte[512];
                int r = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
                if (r == CR_BUFFER_SMALL)
                {
                    if (size == 0 || size > 65536) return null;
                    buf = new byte[size];
                    r = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
                }
                if (r != CR_SUCCESS || size == 0 || size > (uint)buf.Length) return null;
                byte[] exact = new byte[size];
                Array.Copy(buf, exact, (int)size);
                propType = type;
                return exact;
            }
            catch { return null; }
        }

        // Заряд из DEVPKEY_Bluetooth_Battery или null
        public static int? GetBatteryProp(uint devInst)
        {
            uint type;
            byte[] b = GetRawProp(devInst, FmtBattery, PidBattery, out type);
            if (b == null || b.Length < 1) return null;
            int v = b[0];
            if (v > 100) return null;
            return v;
        }

        private static string GetDeviceId(uint devInst)
        {
            var sb = new StringBuilder(1024);
            return CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == CR_SUCCESS ? sb.ToString() : null;
        }

        private static string GetRegStringProp(IntPtr h, ref SP_DEVINFO_DATA d, uint prop)
        {
            try
            {
                uint type, required;
                byte[] buf = new byte[1024];
                if (!SetupDiGetDeviceRegistryPropertyW(h, ref d, prop, out type, buf, (uint)buf.Length, out required)) return null;
                int len = (int)Math.Min(required, (uint)buf.Length);
                if (len < 2) return null;
                string s = Encoding.Unicode.GetString(buf, 0, len);
                int z = s.IndexOf('\0');
                if (z >= 0) s = s.Substring(0, z);
                return s.Length > 0 ? s : null;
            }
            catch { return null; }
        }

        // Присутствующие сейчас BT-узлы (для BT «present» ≈ «подключено»)
        public static List<BtNode> EnumeratePresentBtNodes()
        {
            var result = new List<BtNode>();
            foreach (string en in new[] { "BTHENUM", "BTHLE" })
            {
                IntPtr h = SetupDiGetClassDevsW(IntPtr.Zero, en, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
                if (h == INVALID) continue;
                try
                {
                    for (int i = 0; ; i++)
                    {
                        var d = new SP_DEVINFO_DATA();
                        d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                        if (!SetupDiEnumDeviceInfo(h, i, ref d)) break;
                        if (d.DevInst == 0) continue;

                        string id = GetDeviceId(d.DevInst);
                        if (id == null) continue;
                        string upper = id.ToUpperInvariant();

                        var node = new BtNode();
                        node.InstanceId = id;
                        node.DevInst = d.DevInst;

                        int devIdx = upper.IndexOf("DEV_", StringComparison.Ordinal);
                        if (devIdx >= 0 && devIdx + 16 <= upper.Length && IsHex12(upper.Substring(devIdx + 4, 12)))
                        {
                            node.AddressHex = upper.Substring(devIdx + 4, 12);
                        }
                        else
                        {
                            // узел профиля вида ..._840F2AB87216_C00000000
                            int cIdx = upper.LastIndexOf("_C00000000", StringComparison.Ordinal);
                            if (cIdx >= 12 && IsHex12(upper.Substring(cIdx - 12, 12)))
                            {
                                node.AddressHex = upper.Substring(cIdx - 12, 12);
                                node.IsTransport = true;
                            }
                        }
                        if (node.AddressHex.Length == 0) continue;
                        node.IsLe = upper.StartsWith("BTHLE", StringComparison.Ordinal);

                        node.FriendlyName = GetRegStringProp(h, ref d, SPDRP_FRIENDLYNAME)
                                            ?? GetRegStringProp(h, ref d, SPDRP_DEVICEDESC)
                                            ?? GetStringProp(d.DevInst, FmtFriendlyName, PidFriendlyName)
                                            ?? "";
                        result.Add(node);
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(h); }
            }
            return result;
        }

        // DEVPKEY_Device_ContainerId: все узлы одного физического устройства имеют общий контейнер
        internal static readonly Guid FmtContainerId = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
        internal const uint PidContainerId = 2;

        public static string GetContainerId(uint devInst)
        {
            uint type;
            byte[] b = GetRawProp(devInst, FmtContainerId, PidContainerId, out type);
            if (b == null || b.Length < 16) return null;
            byte[] g = new byte[16];
            Array.Copy(b, g, 16);
            return new Guid(g).ToString();   // нижний регистр, без скобок
        }

        public sealed class ContainerNodes
        {
            public readonly List<string> InstanceIds = new List<string>();
            public int? Battery;              // DEVPKEY_Bluetooth_Battery у любого узла контейнера
        }

        // Снимок всех присутствующих устройств, сгруппированных по контейнерам
        public static Dictionary<string, ContainerNodes> SnapshotContainers()
        {
            var map = new Dictionary<string, ContainerNodes>(StringComparer.OrdinalIgnoreCase);
            IntPtr h = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (h == INVALID) return map;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVINFO_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(h, i, ref d)) break;
                    if (d.DevInst == 0) continue;
                    string cid = GetContainerId(d.DevInst);
                    if (cid == null) continue;
                    string id = GetDeviceId(d.DevInst);
                    if (id == null) continue;
                    ContainerNodes c;
                    if (!map.TryGetValue(cid, out c)) { c = new ContainerNodes(); map[cid] = c; }
                    c.InstanceIds.Add(id);
                    if (c.Battery == null) c.Battery = GetBatteryProp(d.DevInst);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(h); }
            return map;
        }

        // BT-адрес из ID узла: …DEV_AABBCCDDEEFF… или …_AABBCCDDEEFF_C00000000
        public static string AddressFromInstanceId(string id)
        {
            string upper = id.ToUpperInvariant();
            int devIdx = upper.IndexOf("DEV_", StringComparison.Ordinal);
            if (devIdx >= 0 && devIdx + 16 <= upper.Length && IsHex12(upper.Substring(devIdx + 4, 12)))
                return upper.Substring(devIdx + 4, 12);
            int cIdx = upper.LastIndexOf("_C00000000", StringComparison.Ordinal);
            if (cIdx >= 12 && IsHex12(upper.Substring(cIdx - 12, 12))) return upper.Substring(cIdx - 12, 12);
            return null;
        }
    }
}
