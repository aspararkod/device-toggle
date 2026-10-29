// Device Toggle: turn USB and Bluetooth devices on and off, and launch programs with
// selected devices turned off while they run.
//
// Uses only standard Windows APIs (SetupAPI / CfgMgr32, the same ones Device Manager uses),
// so it works on any Windows 10/11 PC. Written in C# 5 so it builds with the compiler that
// ships with Windows; see build.cmd.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Device Toggle")]
[assembly: AssemblyDescription("Turn USB and Bluetooth devices on and off")]
[assembly: AssemblyProduct("Device Toggle")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]

namespace DeviceToggle
{
    // ------------------------------------------------------------------------------------
    // Native API
    // ------------------------------------------------------------------------------------

    static class Native
    {
        public const uint DIGCF_PRESENT = 0x2;
        public const uint DIGCF_ALLCLASSES = 0x4;

        public const uint SPDRP_DEVICEDESC = 0x0;
        public const uint SPDRP_HARDWAREID = 0x1;
        public const uint SPDRP_COMPATIBLEIDS = 0x2;
        public const uint SPDRP_CLASS = 0x7;
        public const uint SPDRP_FRIENDLYNAME = 0xC;

        public const uint DIF_PROPERTYCHANGE = 0x12;
        public const uint DICS_ENABLE = 1;
        public const uint DICS_DISABLE = 2;
        public const uint DICS_FLAG_GLOBAL = 1;
        public const uint DICS_FLAG_CONFIGSPECIFIC = 2;

        public const uint DN_HAS_PROBLEM = 0x400;
        public const uint CM_PROB_DISABLED = 0x16;
        public const uint CR_SUCCESS = 0;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;
        public const int WM_DEVICECHANGE = 0x219;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        // DEVPKEY_Device_BusReportedDeviceDesc: the product name the device itself reports.
        public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc =
            new DEVPROPKEY(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;

            public DEVPROPKEY(Guid fmtid, uint pid)
            {
                this.fmtid = fmtid;
                this.pid = pid;
            }
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey, out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiSetClassInstallParamsW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, int classInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("cfgmgr32.dll")]
        public static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern uint CM_Get_Child(out uint child, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern uint CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLen, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessageW(string message);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // ------------------------------------------------------------------------------------
    // Devices
    // ------------------------------------------------------------------------------------

    // Order matters: it is the order of the groups in the device list.
    public enum DeviceKind { GameController, Keyboard, Mouse, KeyboardMouse, Audio, Camera, Storage, Bluetooth, Network, Other }

    public class DeviceInfo
    {
        public string InstanceId;
        // Stable identity across USB ports, e.g. "VID_044F&PID_B10A". Used to remember choices.
        public string Key;
        public string Name;
        public string DisplayId;
        public DeviceKind Kind;
        public bool Disabled;
        public bool HasProblem;
        public uint Problem;

        public string StatusText
        {
            get
            {
                if (Disabled) return "Off";
                if (HasProblem) return "Problem (code " + Problem + ")";
                return "On";
            }
        }

        public string KindText
        {
            get { return KindName(Kind); }
        }

        // Turning these off can take away the keyboard or mouse you are using.
        public bool IsRisky
        {
            get { return Kind == DeviceKind.Keyboard || Kind == DeviceKind.Mouse || Kind == DeviceKind.KeyboardMouse || Kind == DeviceKind.Bluetooth; }
        }

        public static string KindName(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.GameController: return "Game controller";
                case DeviceKind.Keyboard: return "Keyboard";
                case DeviceKind.Mouse: return "Mouse";
                case DeviceKind.KeyboardMouse: return "Keyboard / mouse";
                case DeviceKind.Audio: return "Audio";
                case DeviceKind.Camera: return "Camera";
                case DeviceKind.Storage: return "Storage";
                case DeviceKind.Bluetooth: return "Bluetooth adapter";
                case DeviceKind.Network: return "Network adapter";
                default: return "Device";
            }
        }

        public static string GroupName(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.GameController: return "Game controllers";
                case DeviceKind.Keyboard:
                case DeviceKind.Mouse:
                case DeviceKind.KeyboardMouse: return "Keyboards and mice";
                case DeviceKind.Audio: return "Audio";
                case DeviceKind.Camera: return "Cameras";
                case DeviceKind.Storage: return "Storage";
                default: return "Other";
            }
        }
    }

    // What we remember about a device, so it keeps its name and type while it is turned off.
    public class KnownDevice
    {
        public DeviceKind Kind;
        public string Name;
    }

    public static class DeviceManager
    {
        class Node
        {
            public string Id;
            public uint DevInst;
            public string ClassName;
            // Hardware and compatible IDs; the HID usage (game controller, keyboard, ...) can be in either.
            public string[] Ids;
            public string FriendlyName;
            public string Description;
            public string BusDescription;
            public uint Status;
            public uint Problem;
        }

        static readonly string[] XboxClasses = { "XnaComposite", "XboxComposite" };
        static readonly string[] AudioClasses = { "MEDIA", "AudioEndpoint" };
        static readonly string[] CameraClasses = { "Camera", "Image" };
        static readonly string[] StorageClasses = { "DiskDrive", "WPD" };

        // HID usage page 1 (generic desktop), usage 4 (joystick) or 5 (gamepad). Matched at the end
        // of an ID, e.g. "HID_DEVICE_UP:0001_U:0004" or "HID\VID_044F&UP:0001_U:0004".
        static readonly string[] GameIds = { "HID_DEVICE_SYSTEM_GAME", "UP:0001_U:0004", "UP:0001_U:0005" };

        // Names Windows gives every device of a kind; we prefer anything more specific.
        static readonly HashSet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "USB Input Device", "HID-compliant game controller", "HID-compliant device",
            "HID-compliant vendor-defined device", "HID-compliant consumer control device",
            "HID-compliant system controller", "HID Keyboard Device", "HID-compliant mouse",
            "USB Composite Device", "Bluetooth HID Device", "Bluetooth Low Energy GATT compliant HID device",
            "XINPUT compatible HID device", "USB Mass Storage Device", "USB Audio Device", "USB Video Device",
            "Generic USB Hub", "USB Receiver", "USB Device", "Unknown USB Device",
        };

        static readonly Regex UsbIdPattern = new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        static readonly Regex BluetoothIdPattern = new Regex(@"VID&[0-9A-F]*?([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase);
        static readonly Regex HubIdPattern = new Regex(@"(^|\\|&)(Dev)?Class_09(&|$)|_HUB$", RegexOptions.IgnoreCase);

        // Lists the physical devices: everything plugged into a USB port, plus Bluetooth input devices.
        public static List<DeviceInfo> Enumerate(IDictionary<string, KnownDevice> known)
        {
            var nodes = ReadAllNodes();
            var result = new List<DeviceInfo>();

            foreach (var node in nodes.Values)
            {
                if (!IsPhysicalDevice(node, nodes)) continue;

                var descendants = new List<Node>();
                CollectDescendants(node, nodes, descendants);

                var info = new DeviceInfo();
                info.InstanceId = node.Id;
                info.HasProblem = (node.Status & Native.DN_HAS_PROBLEM) != 0;
                info.Problem = node.Problem;
                info.Disabled = info.HasProblem && node.Problem == Native.CM_PROB_DISABLED;
                SetIdentity(info, node.Id);

                KnownDevice remembered;
                known.TryGetValue(info.Key, out remembered);
                if (descendants.Count == 0 && remembered != null)
                {
                    // Turned off: its parts are gone, so use what we saw last time.
                    info.Kind = remembered.Kind;
                    info.Name = PickName(node, descendants, null) ?? remembered.Name;
                }
                else
                {
                    info.Kind = DetectKind(node, descendants);
                    info.Name = PickName(node, descendants, info.Kind);
                }
                if (info.Name == null) info.Name = info.KindText;
                result.Add(info);
            }
            return result;
        }

        static bool IsPhysicalDevice(Node node, Dictionary<string, Node> nodes)
        {
            if (node.Id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                if (IsHub(node)) return false;
                // Only what sits directly on a USB port; parts of a device are skipped.
                var parent = GetParent(node, nodes);
                return parent != null && IsHub(parent);
            }
            if (node.Id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                node.Id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(node.ClassName, "HIDClass", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        static bool IsHub(Node node)
        {
            return node.Id.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
                   node.Ids.Any(id => HubIdPattern.IsMatch(id));
        }

        static DeviceKind DetectKind(Node node, List<Node> descendants)
        {
            var all = new List<Node>(descendants) { node };
            var ids = all.SelectMany(n => n.Ids).ToList();
            Func<string[], bool> hasClass = classes => all.Any(n => classes.Any(c => string.Equals(c, n.ClassName, StringComparison.OrdinalIgnoreCase)));

            if (hasClass(XboxClasses) || ids.Any(id => GameIds.Any(g => id.EndsWith(g, StringComparison.OrdinalIgnoreCase))))
                return DeviceKind.GameController;
            // Headsets and webcams often have a few buttons that look like a keyboard; their main job wins.
            if (hasClass(AudioClasses)) return DeviceKind.Audio;
            if (hasClass(CameraClasses)) return DeviceKind.Camera;

            bool keyboard = ids.Any(id => id.Equals("HID_DEVICE_SYSTEM_KEYBOARD", StringComparison.OrdinalIgnoreCase));
            bool mouse = ids.Any(id => id.Equals("HID_DEVICE_SYSTEM_MOUSE", StringComparison.OrdinalIgnoreCase));
            if (keyboard && mouse) return DeviceKind.KeyboardMouse;
            if (keyboard) return DeviceKind.Keyboard;
            if (mouse) return DeviceKind.Mouse;

            if (hasClass(StorageClasses)) return DeviceKind.Storage;
            if (hasClass(new[] { "Bluetooth" })) return DeviceKind.Bluetooth;
            if (hasClass(new[] { "Net" })) return DeviceKind.Network;
            return DeviceKind.Other;
        }

        // Returns null when only generic names are available and kind is given (the caller then
        // uses the type, e.g. "Keyboard"), so the list doesn't fill up with "USB Composite Device".
        static string PickName(Node node, List<Node> descendants, DeviceKind? kind)
        {
            var candidates = new List<string> { node.FriendlyName, node.BusDescription };
            foreach (var d in descendants)
            {
                candidates.Add(d.FriendlyName);
                candidates.Add(d.BusDescription);
                candidates.Add(d.Description);
            }
            candidates.Add(node.Description);

            var usable = candidates.Where(LooksLikeName).Select(s => s.Trim()).ToList();
            string specific = usable.FirstOrDefault(s => !GenericNames.Contains(s));
            if (specific != null) return specific;
            if (kind == null || kind == DeviceKind.Other) return usable.FirstOrDefault();
            return null;
        }

        // Some devices report broken names (garbage from their firmware). Real product names
        // don't contain control characters or stray combining marks.
        static bool LooksLikeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (char c in s)
            {
                var category = char.GetUnicodeCategory(c);
                if (char.IsControl(c) || category == UnicodeCategory.PrivateUse ||
                    category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherNotAssigned ||
                    category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
                    return false;
            }
            return true;
        }

        // Returns true when the device ended up in the requested state, false when Windows
        // needs a restart to finish the change. Throws on errors.
        public static bool SetEnabled(string instanceId, bool enable)
        {
            IntPtr set = Native.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == Native.INVALID_HANDLE_VALUE) throw new Win32Exception();
            try
            {
                var data = NewDevInfoData();
                if (!Native.SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
                    throw new Win32Exception();

                int error;
                if (enable)
                {
                    // Clear both kinds of "disabled" flag, the same way Device Manager does.
                    bool global = PropertyChange(set, ref data, Native.DICS_ENABLE, Native.DICS_FLAG_GLOBAL, out error);
                    int configError;
                    bool config = PropertyChange(set, ref data, Native.DICS_ENABLE, Native.DICS_FLAG_CONFIGSPECIFIC, out configError);
                    if (!global && !config) throw new Win32Exception(error);
                }
                else if (!PropertyChange(set, ref data, Native.DICS_DISABLE, Native.DICS_FLAG_CONFIGSPECIFIC, out error))
                {
                    throw new Win32Exception(error);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }

            uint devInst;
            if (Native.CM_Locate_DevNodeW(out devInst, instanceId, 0) != Native.CR_SUCCESS) return true;
            uint status, problem;
            if (Native.CM_Get_DevNode_Status(out status, out problem, devInst, 0) != Native.CR_SUCCESS) return true;
            bool disabled = (status & Native.DN_HAS_PROBLEM) != 0 && problem == Native.CM_PROB_DISABLED;
            return disabled != enable;
        }

        static bool PropertyChange(IntPtr set, ref Native.SP_DEVINFO_DATA data, uint stateChange, uint scope, out int error)
        {
            var p = new Native.SP_PROPCHANGE_PARAMS();
            p.ClassInstallHeader.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_CLASSINSTALL_HEADER));
            p.ClassInstallHeader.InstallFunction = Native.DIF_PROPERTYCHANGE;
            p.StateChange = stateChange;
            p.Scope = scope;
            p.HwProfile = 0;

            error = 0;
            if (!Native.SetupDiSetClassInstallParamsW(set, ref data, ref p, Marshal.SizeOf(typeof(Native.SP_PROPCHANGE_PARAMS))) ||
                !Native.SetupDiCallClassInstaller(Native.DIF_PROPERTYCHANGE, set, ref data))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }

        // Turns a stored key from an older version ("VID_x&PID_y&MI_00") into the current form.
        public static string NormalizeKey(string key)
        {
            var m = UsbIdPattern.Match(key);
            return m.Success && key.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                ? "VID_" + m.Groups[1].Value.ToUpperInvariant() + "&PID_" + m.Groups[2].Value.ToUpperInvariant()
                : key;
        }

        static Dictionary<string, Node> ReadAllNodes()
        {
            var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            if (set == Native.INVALID_HANDLE_VALUE) throw new Win32Exception();
            try
            {
                var data = NewDevInfoData();
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    string id = GetInstanceId(set, ref data);
                    if (id == null) continue;

                    var node = new Node();
                    node.Id = id;
                    node.DevInst = data.DevInst;
                    node.ClassName = First(GetRegistryStrings(set, ref data, Native.SPDRP_CLASS));
                    node.Ids = GetRegistryStrings(set, ref data, Native.SPDRP_HARDWAREID)
                        .Concat(GetRegistryStrings(set, ref data, Native.SPDRP_COMPATIBLEIDS)).ToArray();
                    node.FriendlyName = First(GetRegistryStrings(set, ref data, Native.SPDRP_FRIENDLYNAME));
                    node.Description = First(GetRegistryStrings(set, ref data, Native.SPDRP_DEVICEDESC));
                    node.BusDescription = GetPropertyString(set, ref data, Native.DEVPKEY_Device_BusReportedDeviceDesc);
                    Native.CM_Get_DevNode_Status(out node.Status, out node.Problem, data.DevInst, 0);
                    nodes[id] = node;
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return nodes;
        }

        static void SetIdentity(DeviceInfo info, string instanceId)
        {
            var m = UsbIdPattern.Match(instanceId);
            if (!m.Success) m = BluetoothIdPattern.Match(instanceId);
            if (m.Success)
            {
                string vid = m.Groups[1].Value.ToUpperInvariant();
                string pid = m.Groups[2].Value.ToUpperInvariant();
                info.Key = "VID_" + vid + "&PID_" + pid;
                info.DisplayId = vid + ":" + pid;
            }
            else
            {
                info.Key = instanceId.ToUpperInvariant();
                info.DisplayId = "";
            }
        }

        static void CollectDescendants(Node node, Dictionary<string, Node> nodes, List<Node> into)
        {
            foreach (var child in GetChildren(node, nodes))
            {
                into.Add(child);
                CollectDescendants(child, nodes, into);
            }
        }

        static List<Node> GetChildren(Node node, Dictionary<string, Node> nodes)
        {
            var list = new List<Node>();
            uint current;
            if (Native.CM_Get_Child(out current, node.DevInst, 0) != Native.CR_SUCCESS) return list;
            while (true)
            {
                Node child;
                string id = GetDeviceId(current);
                if (id != null && nodes.TryGetValue(id, out child)) list.Add(child);
                uint next;
                if (Native.CM_Get_Sibling(out next, current, 0) != Native.CR_SUCCESS) break;
                current = next;
            }
            return list;
        }

        static Node GetParent(Node node, Dictionary<string, Node> nodes)
        {
            uint parent;
            if (Native.CM_Get_Parent(out parent, node.DevInst, 0) != Native.CR_SUCCESS) return null;
            string id = GetDeviceId(parent);
            Node result;
            return id != null && nodes.TryGetValue(id, out result) ? result : null;
        }

        static string GetDeviceId(uint devInst)
        {
            var sb = new StringBuilder(512);
            return Native.CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == Native.CR_SUCCESS ? sb.ToString() : null;
        }

        static Native.SP_DEVINFO_DATA NewDevInfoData()
        {
            var data = new Native.SP_DEVINFO_DATA();
            data.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
            return data;
        }

        static string GetInstanceId(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            var sb = new StringBuilder(512);
            int required;
            return Native.SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out required) ? sb.ToString() : null;
        }

        static string[] GetRegistryStrings(IntPtr set, ref Native.SP_DEVINFO_DATA data, uint property)
        {
            uint type, required;
            var buffer = new byte[1024];
            if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out type, buffer, (uint)buffer.Length, out required))
            {
                if (Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER) return new string[0];
                buffer = new byte[required];
                if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out type, buffer, (uint)buffer.Length, out required))
                    return new string[0];
            }
            return SplitStrings(buffer, (int)required);
        }

        static string GetPropertyString(IntPtr set, ref Native.SP_DEVINFO_DATA data, Native.DEVPROPKEY key)
        {
            uint type, required;
            var buffer = new byte[1024];
            if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, (uint)buffer.Length, out required, 0))
            {
                if (Marshal.GetLastWin32Error() != Native.ERROR_INSUFFICIENT_BUFFER) return null;
                buffer = new byte[required];
                if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, (uint)buffer.Length, out required, 0))
                    return null;
            }
            return First(SplitStrings(buffer, (int)required));
        }

        static string[] SplitStrings(byte[] buffer, int length)
        {
            length = Math.Min(length, buffer.Length);
            return Encoding.Unicode.GetString(buffer, 0, length & ~1)
                .Split('\0')
                .Where(s => s.Length > 0)
                .ToArray();
        }

        static string First(string[] values)
        {
            return values.Length > 0 ? values[0] : null;
        }
    }

    // ------------------------------------------------------------------------------------
    // Programs: starting them without admin rights, and noticing when they have closed
    // ------------------------------------------------------------------------------------

    // This app runs as administrator, and anything it starts directly would be too. Instead we
    // ask the desktop (Explorer) to start the program, so it runs as a normal user process,
    // exactly as if it had been double-clicked. See "How can I launch an unelevated process
    // from my elevated process?" by Raymond Chen.
    static class ShellLauncher
    {
        [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
        class ShellWindowsClass { }

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            // Slots we never call; they only keep FindWindowSW at the right vtable position.
            void get_Count();
            void Item();
            void _NewEnum();
            void Register();
            void RegisterPending();
            void Revoke();
            void OnNavigate();
            void OnActivated();

            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object locationRoot, int windowClass, out int hwnd, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void InsertMenusSB();
            void SetMenuSB();
            void RemoveMenusSB();
            void SetStatusTextSB();
            void EnableModelessSB();
            void TranslateAcceleratorSB();
            void BrowseObject();
            void GetViewStateStream();
            void GetControlWindow();
            void SendControlMsg();
            IShellView QueryActiveShellView();
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellView
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void TranslateAccelerator();
            void EnableModeless();
            void UIActivate();
            void Refresh();
            void CreateViewWindow();
            void DestroyViewWindow();
            void GetCurrentInfo();
            void AddPropertySheetPages();
            void SaveViewState();
            void SelectItem();

            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetItemObject(uint item, ref Guid riid);
        }

        const int SWC_DESKTOP = 8;
        const int SWFO_NEEDDISPATCH = 1;
        const uint SVGIO_BACKGROUND = 0;
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        public static void Start(string file)
        {
            string directory = Path.GetDirectoryName(file) ?? "";
            try
            {
                StartFromDesktop(file, directory);
                return;
            }
            catch (Exception)
            {
                // No Explorer desktop (unusual shell setup). Let Explorer start it instead.
            }
            Process.Start("explorer.exe", "\"" + file + "\"");
        }

        static void StartFromDesktop(string file, string directory)
        {
            var windows = (IShellWindows)new ShellWindowsClass();
            object location = 0; // CSIDL_DESKTOP
            object locationRoot = null;
            int hwnd;
            object desktop = windows.FindWindowSW(ref location, ref locationRoot, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);

            Guid service = SID_STopLevelBrowser;
            Guid browserId = typeof(IShellBrowser).GUID;
            var browser = (IShellBrowser)((IComServiceProvider)desktop).QueryService(ref service, ref browserId);
            IShellView view = browser.QueryActiveShellView();

            Guid dispatchId = IID_IDispatch;
            object folderView = view.GetItemObject(SVGIO_BACKGROUND, ref dispatchId);
            object shell = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
            shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell,
                new object[] { file, "", directory, "open", 1 });
        }
    }

    // Decides when a launched program has closed. Launchers often start the real program from
    // their own folder and then exit, so by default we wait until nothing running from the
    // program's folder is left. For programs in shared folders (Windows, a drive root, Program
    // Files itself) that would never end, so there we only watch the program's own exe.
    class ProgramWatcher
    {
        readonly string exePath;
        readonly string folder;

        public ProgramWatcher(string exePath)
        {
            this.exePath = Path.GetFullPath(exePath);
            string dir = Path.GetDirectoryName(this.exePath).TrimEnd('\\');
            folder = IsSharedFolder(dir) ? null : dir + "\\";
        }

        // Returns the file name of a process that is still running, or null if none is.
        public string FindRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self || p.Id == 0) continue;
                    string path = GetProcessPath(p.Id);
                    if (path == null) continue;
                    bool match = folder != null
                        ? path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                        : path.Equals(exePath, StringComparison.OrdinalIgnoreCase);
                    if (match) return Path.GetFileName(path);
                }
            }
            return null;
        }

        static bool IsSharedFolder(string dir)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            if (dir.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
            if (dir.Equals((Path.GetPathRoot(dir) ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            var shared = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            return shared.Any(s => !string.IsNullOrEmpty(s) && dir.Equals(s.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }

        static string GetProcessPath(int pid)
        {
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------------------------

    public class ProgramEntry
    {
        public string Name = "";
        public string ExePath = "";
        public List<string> DeviceKeys = new List<string>();

        public static string NameFor(string exePath)
        {
            string name = null;
            try { name = FileVersionInfo.GetVersionInfo(exePath).ProductName; } catch (Exception) { }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exePath);
            return name.Trim();
        }
    }

    public class Settings
    {
        public List<ProgramEntry> Programs = new List<ProgramEntry>();
        public Dictionary<string, KnownDevice> KnownDevices = new Dictionary<string, KnownDevice>(StringComparer.OrdinalIgnoreCase);
        // Devices this app turned off for a running program. Kept on disk so they can be
        // restored if the app is closed unexpectedly.
        public List<string> PendingRestore = new List<string>();

        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeviceToggle");
        static readonly string FilePath = Path.Combine(Folder, "settings.ini");

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return settings;
                ProgramEntry program = null;
                string section = "";
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2);
                        program = null;
                        if (section.Equals("Program", StringComparison.OrdinalIgnoreCase))
                        {
                            program = new ProgramEntry();
                            settings.Programs.Add(program);
                        }
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (program != null)
                    {
                        if (key == "Name") program.Name = value;
                        else if (key == "Path") program.ExePath = value;
                        else if (key == "Devices") program.DeviceKeys = SplitList(value).Select(DeviceManager.NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    else if (section.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key == "PendingRestore") settings.PendingRestore = SplitList(value);
                    }
                    else if (section.Equals("KnownDevices", StringComparison.OrdinalIgnoreCase))
                    {
                        int bar = value.IndexOf('|');
                        DeviceKind kind;
                        if (bar > 0 && Enum.TryParse(value.Substring(0, bar), out kind))
                            settings.KnownDevices[key] = new KnownDevice { Kind = kind, Name = value.Substring(bar + 1) };
                    }
                }
                settings.Programs.RemoveAll(p => p.ExePath.Length == 0);
                foreach (var p in settings.Programs.Where(p => p.Name.Length == 0)) p.Name = ProgramEntry.NameFor(p.ExePath);
            }
            catch (Exception)
            {
                // A broken settings file should not stop the app; start fresh.
            }
            return settings;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; Device Toggle settings");
            sb.AppendLine("[Settings]");
            sb.AppendLine("PendingRestore=" + string.Join("|", PendingRestore));
            foreach (var p in Programs)
            {
                sb.AppendLine();
                sb.AppendLine("[Program]");
                sb.AppendLine("Name=" + p.Name);
                sb.AppendLine("Path=" + p.ExePath);
                sb.AppendLine("Devices=" + string.Join("|", p.DeviceKeys));
            }
            sb.AppendLine();
            sb.AppendLine("[KnownDevices]");
            foreach (var pair in KnownDevices.OrderBy(k => k.Key))
                sb.AppendLine(pair.Key + "=" + pair.Value.Kind + "|" + pair.Value.Name);

            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        static List<string> SplitList(string value)
        {
            return value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }
    }

    // ------------------------------------------------------------------------------------
    // Look and feel: colours, fonts and the custom-drawn controls
    // ------------------------------------------------------------------------------------

    static class Theme
    {
        public static readonly Color Background = Rgb(0x0D1117);
        public static readonly Color Chrome = Rgb(0x090C10);
        public static readonly Color Card = Rgb(0x161B22);
        public static readonly Color CardHover = Rgb(0x1C2330);
        public static readonly Color Track = Rgb(0x262C36);
        public static readonly Color Input = Rgb(0x0A0D12);
        public static readonly Color Border = Rgb(0x30363D);
        public static readonly Color Accent = Rgb(0x22D3EE);
        public static readonly Color AccentHover = Rgb(0x67E8F9);
        public static readonly Color Text = Rgb(0xE6EDF3);
        public static readonly Color Muted = Rgb(0x8B949E);
        public static readonly Color Faint = Rgb(0x484F58);
        public static readonly Color Danger = Rgb(0xF87171);
        public static readonly Color Warning = Rgb(0xFBBF24);

        // Set from the screen DPI at startup. Fonts are in points and scale by themselves.
        public static float Scale = 1f;

        public static readonly Font TitleFont = new Font("Segoe UI Semibold", 12.5f);
        public static readonly Font NameFont = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font SectionFont = new Font("Segoe UI Semibold", 8.25f);

        // Icon fonts that ship with Windows: Segoe Fluent Icons (11), Segoe MDL2 Assets (10).
        static readonly string IconFamily = PickIconFamily();
        static readonly Dictionary<float, Font> iconFonts = new Dictionary<float, Font>();

        public const string GlyphPower = "";
        public const string GlyphPlay = "";
        public const string GlyphAdd = "";
        public const string GlyphEdit = "";
        public const string GlyphDelete = "";
        public const string GlyphLink = "";
        public const string GlyphRefresh = "";
        public const string GlyphCheck = "";
        public const string GlyphFolder = "";
        public const string GlyphInfo = "";
        public const string GlyphWarning = "";
        public const string GlyphQuestion = "";

        public static string KindGlyph(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.GameController: return "";
                case DeviceKind.Keyboard: return "";
                case DeviceKind.Mouse: return "";
                case DeviceKind.KeyboardMouse: return "";
                case DeviceKind.Audio: return "";
                case DeviceKind.Camera: return "";
                case DeviceKind.Storage: return "";
                case DeviceKind.Bluetooth: return "";
                case DeviceKind.Network: return "";
                default: return "";
            }
        }

        public static int S(float value)
        {
            return (int)Math.Round(value * Scale);
        }

        public static Color Rgb(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Color Alpha(Color color, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Font Icons(float size)
        {
            Font font;
            if (!iconFonts.TryGetValue(size, out font))
            {
                font = new Font(IconFamily, size);
                iconFonts[size] = font;
            }
            return font;
        }

        static string PickIconFamily()
        {
            using (var fonts = new System.Drawing.Text.InstalledFontCollection())
            {
                return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            }
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color fill, Color border)
        {
            using (var path = Rounded(r, radius))
            {
                if (fill.A > 0)
                    using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (border.A > 0)
                    using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
        }

        // Text colours must be opaque: GDI text ignores alpha.
        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra = TextFormatFlags.Default)
        {
            TextRenderer.DrawText(g, text, font, bounds, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | extra);
        }

        public static void DrawGlyph(Graphics g, string glyph, float size, Rectangle bounds, Color color)
        {
            TextRenderer.DrawText(g, glyph, Icons(size), bounds, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(hwnd, 19, ref on, 4); // older Windows 10 builds
                int caption = ColorRef(Chrome);
                Native.DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // Windows 11 only
                int border = ColorRef(Border);
                Native.DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            }
            catch (Exception)
            {
                // Not supported on this Windows version; the normal title bar is fine.
            }
        }

        static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }

    // Base for the app's windows: dark background and title bar.
    class DarkForm : Form
    {
        public DarkForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }
    }

    // Owner-drawn controls share this setup.
    class PaintedControl : Control
    {
        public PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, false);
            BackColor = Theme.Background;
        }
    }

    class FlatButton : PaintedControl, IButtonControl
    {
        public enum Kind { Accent, Ghost, Icon }

        readonly Kind style;
        readonly string glyph;
        bool hover, pressed;

        // Colour of whatever is behind the button.
        public Color Surface = Theme.Background;
        public bool DangerHover;
        public DialogResult DialogResult { get; set; }

        public FlatButton(string text, string glyph, Kind style)
        {
            this.style = style;
            this.glyph = glyph;
            Text = text ?? "";
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            Height = Theme.S(style == Kind.Icon ? 32 : 34);
            FitWidth();
        }

        public void FitWidth()
        {
            if (style == Kind.Icon)
            {
                Width = Height;
                return;
            }
            int width = Theme.S(16) * 2 + TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (glyph != null) width += Theme.S(14) + Theme.S(8);
            Width = width;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitWidth();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitWidth();
            Invalidate();
        }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var form = FindForm();
            if (DialogResult != DialogResult.None && form != null) form.DialogResult = DialogResult;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            bool active = Enabled && hover;

            Color fill, border, fore;
            switch (style)
            {
                case Kind.Accent:
                    fill = !Enabled ? Theme.Faint : pressed ? Theme.Accent : active ? Theme.AccentHover : Theme.Accent;
                    border = Color.Transparent;
                    fore = Theme.Background;
                    break;
                case Kind.Ghost:
                    fill = !active ? Color.Transparent : Theme.Alpha(Theme.Text, pressed ? 40 : 20);
                    border = active ? Theme.Accent : Theme.Border;
                    fore = Enabled ? Theme.Text : Theme.Faint;
                    break;
                default:
                    fill = !active ? Color.Transparent : Theme.Alpha(Theme.Text, pressed ? 36 : 18);
                    border = Color.Transparent;
                    fore = !Enabled ? Theme.Faint : !active ? Theme.Muted : DangerHover ? Theme.Danger : Theme.Text;
                    break;
            }
            Theme.Fill(g, r, style == Kind.Icon ? Theme.S(8) : r.Height / 2, fill, border);

            if (style == Kind.Icon)
            {
                Theme.DrawGlyph(g, glyph, 11f, ClientRectangle, fore);
                return;
            }
            int textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int glyphWidth = glyph != null ? Theme.S(14) + Theme.S(8) : 0;
            int x = (Width - textWidth - glyphWidth) / 2;
            if (glyph != null) Theme.DrawGlyph(g, glyph, 9.5f, new Rectangle(x, 0, Theme.S(14), Height), fore);
            Theme.DrawText(g, Text, Font, new Rectangle(x + glyphWidth, 0, textWidth + Theme.S(4), Height), fore, TextFormatFlags.NoPadding);
        }
    }

    // A section title ("GAME CONTROLLERS") or, with hint = true, a line of muted help text.
    class Caption : PaintedControl
    {
        readonly bool hint;

        public Caption(string text, bool hint)
        {
            this.hint = hint;
            Text = hint ? text : text.ToUpperInvariant();
            Height = Theme.S(hint ? 44 : 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (hint)
            {
                TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(Theme.S(4), Theme.S(4), Width - Theme.S(8), Height - Theme.S(4)),
                    Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                return;
            }
            var size = TextRenderer.MeasureText(Text, Theme.SectionFont, Size.Empty, TextFormatFlags.NoPadding);
            var textRect = new Rectangle(Theme.S(2), Height - size.Height - Theme.S(6), size.Width + Theme.S(4), size.Height);
            Theme.DrawText(g, Text, Theme.SectionFont, textRect, Theme.Accent, TextFormatFlags.NoPadding);
            int lineY = textRect.Top + textRect.Height / 2;
            using (var brush = new LinearGradientBrush(new Rectangle(textRect.Right + Theme.S(8), lineY, Math.Max(1, Width - textRect.Right), 1),
                Theme.Border, Theme.Background, 0f))
            {
                g.FillRectangle(brush, textRect.Right + Theme.S(8), lineY, Width - textRect.Right - Theme.S(8), 1);
            }
        }
    }

    // One device: icon, name, details and an on/off switch (or a check box in Checkable mode).
    class DeviceCard : PaintedControl
    {
        public DeviceInfo Device { get; private set; }
        public bool Busy { get; private set; }
        public string Subtitle;
        public event EventHandler ToggleRequested;

        readonly bool checkable;
        bool isChecked, hover, hoverSwitch;
        float knob, knobTarget, animFrom;
        DateTime animStart;
        int pulse;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };

        public DeviceCard(DeviceInfo device, bool checkable)
        {
            this.checkable = checkable;
            Height = Theme.S(checkable ? 58 : 68);
            anim.Tick += delegate { Step(); };
            SetDevice(device);
            knob = knobTarget;
        }

        public bool Checked
        {
            get { return isChecked; }
            set { isChecked = value; Invalidate(); }
        }

        public void SetDevice(DeviceInfo device)
        {
            Device = device;
            if (!Busy) MoveKnob(device.Disabled ? 0f : 1f);
            Invalidate();
        }

        // Shows the new state straight away while Windows applies it.
        public void BeginChange(bool turnOn)
        {
            Busy = true;
            MoveKnob(turnOn ? 1f : 0f);
            anim.Start();
        }

        public void EndChange()
        {
            Busy = false;
            Invalidate();
        }

        void MoveKnob(float target)
        {
            if (target == knobTarget && (anim.Enabled || knob == target)) return;
            knobTarget = target;
            animFrom = knob;
            animStart = DateTime.Now;
            if (IsHandleCreated) anim.Start();
            else knob = target;
        }

        void Step()
        {
            double t = (DateTime.Now - animStart).TotalMilliseconds / 160.0;
            if (t >= 1) knob = knobTarget;
            else
            {
                float eased = 1f - (float)Math.Pow(1 - t, 3);
                knob = animFrom + (knobTarget - animFrom) * eased;
            }
            pulse++;
            if (t >= 1 && !Busy) anim.Stop();
            Invalidate();
        }

        Rectangle SwitchRect
        {
            get
            {
                int w = Theme.S(46), h = Theme.S(24);
                return new Rectangle(Width - Theme.S(20) - w, (Height - h) / 2, w, h);
            }
        }

        Rectangle CheckRect
        {
            get
            {
                int s = Theme.S(22);
                return new Rectangle(Width - Theme.S(20) - s, (Height - s) / 2, s, s);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float k = checkable ? (isChecked ? 1f : 0f) : knob;

            Theme.Fill(g, card, Theme.S(10), hover ? Theme.CardHover : Theme.Card,
                checkable && isChecked ? Theme.Alpha(Theme.Accent, 140) : Theme.Border);

            // Accent stripe on the left of devices that are on.
            if (k > 0.01f)
            {
                var stripe = new RectangleF(card.X + 1, card.Y + Theme.S(16), Theme.S(3), card.Height - Theme.S(32));
                Theme.Fill(g, stripe, Theme.S(2), Theme.Alpha(Theme.Accent, (int)(255 * k)), Color.Transparent);
            }

            int badge = Theme.S(40);
            var badgeRect = new Rectangle(Theme.S(16), (Height - badge) / 2, badge, badge);
            Color iconColor = Theme.Mix(Theme.Muted, Theme.Accent, k);
            Theme.Fill(g, badgeRect, Theme.S(10), Theme.Alpha(iconColor, 26), Color.Transparent);
            Theme.DrawGlyph(g, Theme.KindGlyph(Device.Kind), 15f, badgeRect, iconColor);

            int left = badgeRect.Right + Theme.S(14);
            int right = (checkable ? CheckRect.Left : SwitchRect.Left) - Theme.S(14);
            bool dim = !checkable && Device.Disabled && !Busy;
            Theme.DrawText(g, Device.Name, Theme.NameFont, new Rectangle(left, Height / 2 - Theme.S(21), right - left, Theme.S(22)),
                dim ? Theme.Muted : Theme.Text, TextFormatFlags.Bottom);
            Theme.DrawText(g, Details(), Theme.Small, new Rectangle(left, Height / 2 + Theme.S(2), right - left, Theme.S(18)),
                Device.HasProblem && !Device.Disabled ? Theme.Danger : Theme.Muted, TextFormatFlags.Top);

            if (checkable) DrawCheck(g);
            else DrawSwitch(g, k);
        }

        string Details()
        {
            if (Subtitle != null) return Subtitle;
            var parts = new List<string> { Device.KindText };
            if (Device.DisplayId.Length > 0) parts.Add(Device.DisplayId);
            if (!checkable)
                parts.Add(Busy ? (knobTarget > 0.5f ? "Turning on..." : "Turning off...") : Device.StatusText);
            return string.Join("   ·   ", parts);
        }

        void DrawSwitch(Graphics g, float k)
        {
            var r = SwitchRect;
            using (var track = Theme.Rounded(r, r.Height / 2f))
            {
                // Soft glow around a switch that is on.
                for (int i = 3; i >= 1; i--)
                {
                    using (var glow = new Pen(Theme.Alpha(Theme.Accent, (int)(28 * k)), Theme.S(2 * i)))
                        g.DrawPath(glow, track);
                }
                using (var brush = new SolidBrush(Theme.Mix(Theme.Track, Theme.Accent, k)))
                    g.FillPath(brush, track);
                if (k < 0.99f)
                    using (var pen = new Pen(Theme.Alpha(hoverSwitch ? Theme.Text : Theme.Faint, (int)(255 * (1 - k))), 1f))
                        g.DrawPath(pen, track);
            }

            int d = r.Height - Theme.S(8);
            float x = r.X + Theme.S(4) + (r.Width - Theme.S(8) - d) * k;
            int alpha = Busy ? 140 + (int)(100 * Math.Sin(pulse / 6.0)) : 255;
            Color knobColor = Theme.Mix(hoverSwitch ? Theme.Text : Theme.Muted, Theme.Background, k);
            using (var brush = new SolidBrush(Theme.Alpha(knobColor, alpha)))
                g.FillEllipse(brush, x, r.Y + Theme.S(4), d, d);
        }

        void DrawCheck(Graphics g)
        {
            var r = CheckRect;
            if (isChecked)
            {
                Theme.Fill(g, r, Theme.S(6), Theme.Accent, Color.Transparent);
                Theme.DrawGlyph(g, Theme.GlyphCheck, 10f, r, Theme.Background);
            }
            else
            {
                Theme.Fill(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(6), Color.Transparent, hover ? Theme.Text : Theme.Muted);
            }
        }

        bool OverSwitch(Point p)
        {
            var r = SwitchRect;
            r.Inflate(Theme.S(10), Theme.S(10));
            return r.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = !checkable && OverSwitch(e.Location);
            Cursor = checkable || over ? Cursors.Hand : Cursors.Default;
            if (!hover || over != hoverSwitch)
            {
                hover = true;
                hoverSwitch = over;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            hoverSwitch = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (checkable) Checked = !Checked;
            else if (OverSwitch(e.Location) && !Busy && ToggleRequested != null) ToggleRequested(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }
    }

    // One program: its icon, which devices it turns off, and buttons to launch, edit and so on.
    class ProgramCard : PaintedControl
    {
        public ProgramEntry Program { get; private set; }
        public readonly FlatButton LaunchButton, StopButton, EditButton, ShortcutButton, RemoveButton;

        string devicesText = "";
        string runningText;
        Bitmap icon;
        int pulse;
        readonly System.Windows.Forms.Timer pulseTimer = new System.Windows.Forms.Timer { Interval = 50 };

        public ProgramCard(ProgramEntry program)
        {
            Program = program;
            Height = Theme.S(76);
            try { using (var ico = Icon.ExtractAssociatedIcon(program.ExePath)) icon = ico.ToBitmap(); } catch (Exception) { }

            LaunchButton = new FlatButton("Launch", Theme.GlyphPlay, FlatButton.Kind.Accent) { Surface = Theme.Card };
            StopButton = new FlatButton("Turn devices back on", Theme.GlyphPower, FlatButton.Kind.Ghost) { Surface = Theme.Card, Visible = false };
            EditButton = new FlatButton(null, Theme.GlyphEdit, FlatButton.Kind.Icon) { Surface = Theme.Card };
            ShortcutButton = new FlatButton(null, Theme.GlyphLink, FlatButton.Kind.Icon) { Surface = Theme.Card };
            RemoveButton = new FlatButton(null, Theme.GlyphDelete, FlatButton.Kind.Icon) { Surface = Theme.Card, DangerHover = true };
            var tips = new ToolTip();
            tips.SetToolTip(EditButton, "Change devices");
            tips.SetToolTip(ShortcutButton, "Create a desktop shortcut");
            tips.SetToolTip(RemoveButton, "Remove from the list");
            Controls.AddRange(new Control[] { LaunchButton, StopButton, EditButton, ShortcutButton, RemoveButton });
            pulseTimer.Tick += delegate { pulse++; Invalidate(); };
        }

        public void Update(string devicesText, string runningText, bool canLaunch)
        {
            this.devicesText = devicesText;
            this.runningText = runningText;
            bool running = runningText != null;
            LaunchButton.Visible = !running;
            LaunchButton.Enabled = canLaunch;
            StopButton.Visible = running;
            EditButton.Enabled = !running;
            RemoveButton.Enabled = !running;
            if (running) pulseTimer.Start(); else pulseTimer.Stop();
            LayoutButtons();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutButtons();
        }

        int TextRight
        {
            get { return (StopButton.Visible ? StopButton.Left : LaunchButton.Left) - Theme.S(12); }
        }

        void LayoutButtons()
        {
            if (RemoveButton == null) return; // still being constructed
            int x = Width - Theme.S(14);
            foreach (var b in new[] { RemoveButton, ShortcutButton, EditButton })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= Theme.S(2);
            }
            x -= Theme.S(8);
            foreach (var b in new[] { LaunchButton, StopButton })
                b.Location = new Point(x - b.Width, (Height - b.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool running = runningText != null;
            Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10), Theme.Card,
                running ? Theme.Alpha(Theme.Accent, 160) : Theme.Border);

            int size = Theme.S(32);
            var iconRect = new Rectangle(Theme.S(20), (Height - size) / 2, size, size);
            if (icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, iconRect);
            }
            else
            {
                Theme.DrawGlyph(g, Theme.GlyphPlay, 16f, iconRect, Theme.Accent);
            }

            int left = iconRect.Right + Theme.S(16);
            int width = Math.Max(0, TextRight - left);
            Theme.DrawText(g, Program.Name, Theme.NameFont, new Rectangle(left, Height / 2 - Theme.S(21), width, Theme.S(22)), Theme.Text, TextFormatFlags.Bottom);
            var detailRect = new Rectangle(left, Height / 2 + Theme.S(2), width, Theme.S(18));
            if (running)
            {
                int dot = Theme.S(8);
                int alpha = 150 + (int)(105 * Math.Sin(pulse / 4.0));
                using (var brush = new SolidBrush(Theme.Alpha(Theme.Accent, alpha)))
                    g.FillEllipse(brush, left, detailRect.Top + (Theme.S(16) - dot) / 2, dot, dot);
                detailRect.X += dot + Theme.S(8);
                detailRect.Width -= dot + Theme.S(8);
                Theme.DrawText(g, runningText, Theme.Small, detailRect, Theme.Accent, TextFormatFlags.Top);
            }
            else
            {
                Theme.DrawText(g, "Turns off: " + devicesText, Theme.Small, detailRect, Theme.Muted, TextFormatFlags.Top);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pulseTimer.Dispose();
                if (icon != null) icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Scrolling column of captions, cards and buttons, all full width (buttons keep their size).
    class CardList : Panel
    {
        List<Control> items = new List<Control>();
        bool laying;

        public CardList()
        {
            AutoScroll = true;
            DoubleBuffered = true;
            BackColor = Theme.Background;
            Padding = new Padding(Theme.S(20), Theme.S(6), Theme.S(20), Theme.S(20));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowTheme(Handle, "DarkMode_Explorer", null); // dark scroll bar
        }

        // Shows exactly these controls, in this order. Controls left out are removed (not disposed).
        public void SetItems(List<Control> newItems)
        {
            SuspendLayout();
            foreach (var old in items.Where(c => !newItems.Contains(c)))
                Controls.Remove(old);
            foreach (var c in newItems.Where(c => !Controls.Contains(c)))
                Controls.Add(c);
            items = new List<Control>(newItems);
            ResumeLayout(false);
            LayoutItems();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutItems();
        }

        void LayoutItems()
        {
            if (laying) return;
            laying = true;
            try
            {
                int width = Math.Max(Theme.S(100), ClientSize.Width - Padding.Horizontal);
                int y = Padding.Top;
                int scroll = AutoScrollPosition.Y;
                foreach (var c in items)
                {
                    if (c is Caption && y > Padding.Top) y += Theme.S(10);
                    if (c is FlatButton) y += Theme.S(4);
                    c.SetBounds(Padding.Left, y + scroll, c is FlatButton ? c.Width : width, c.Height);
                    y += c.Height + (c is Caption ? Theme.S(2) : Theme.S(8));
                }
                AutoScrollMinSize = new Size(0, y + Padding.Bottom);
            }
            finally
            {
                laying = false;
            }
        }
    }

    class HeaderBar : PaintedControl
    {
        public readonly FlatButton RefreshButton;

        public HeaderBar(string title)
        {
            Text = title;
            Dock = DockStyle.Top;
            Height = Theme.S(64);
            BackColor = Theme.Chrome;
            RefreshButton = new FlatButton(null, Theme.GlyphRefresh, FlatButton.Kind.Icon) { Surface = Theme.Chrome };
            new ToolTip().SetToolTip(RefreshButton, "Refresh");
            Controls.Add(RefreshButton);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (RefreshButton == null) return; // still being constructed
            RefreshButton.Location = new Point(Width - Theme.S(18) - RefreshButton.Width, (Height - Theme.S(2) - RefreshButton.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int h = Height - Theme.S(2);
            var badge = new Rectangle(Theme.S(20), (h - Theme.S(34)) / 2, Theme.S(34), Theme.S(34));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, badge, Theme.S(9), Theme.Alpha(Theme.Accent, 30), Theme.Alpha(Theme.Accent, 90));
            Theme.DrawGlyph(g, Theme.GlyphPower, 14f, badge, Theme.Accent);
            Theme.DrawText(g, Text, Theme.TitleFont, new Rectangle(badge.Right + Theme.S(14), 0, Width / 2, h), Theme.Text);

            var line = new Rectangle(0, h, Width, Theme.S(2));
            using (var brush = new LinearGradientBrush(new Rectangle(0, h, Math.Max(1, Width), line.Height + 1), Theme.Accent, Theme.Alpha(Theme.Accent, 0), 0f))
                g.FillRectangle(brush, line);
        }
    }

    class StatusBar : PaintedControl
    {
        public readonly FlatButton StopButton;
        string status = "";

        public StatusBar()
        {
            Dock = DockStyle.Bottom;
            Height = Theme.S(42);
            BackColor = Theme.Chrome;
            StopButton = new FlatButton("Turn devices back on now", Theme.GlyphPower, FlatButton.Kind.Ghost) { Surface = Theme.Chrome, Visible = false };
            StopButton.Font = Theme.Small;
            StopButton.Height = Theme.S(28);
            Controls.Add(StopButton);
        }

        public string Status
        {
            get { return status; }
            set { status = value ?? ""; Invalidate(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (StopButton == null) return; // still being constructed
            StopButton.Location = new Point(Width - Theme.S(14) - StopButton.Width, (Height - StopButton.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, 0, Width, 0);
            int right = StopButton.Visible ? StopButton.Left - Theme.S(10) : Width - Theme.S(20);
            Theme.DrawText(g, status, Theme.Small, new Rectangle(Theme.S(20), 0, Math.Max(0, right - Theme.S(20)), Height), Theme.Muted);
        }
    }

    // A text box with a rounded dark frame.
    class InputBox : Panel
    {
        public readonly TextBox Box;

        public InputBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            Height = Theme.S(40);
            Padding = new Padding(Theme.S(12), Theme.S(10), Theme.S(12), Theme.S(6));
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body, Dock = DockStyle.Fill };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(8), Theme.Input, Box.Focused ? Theme.Accent : Theme.Border);
        }
    }

    class GlyphView : PaintedControl
    {
        readonly string glyph;
        readonly Color color;

        public GlyphView(string glyph, Color color)
        {
            this.glyph = glyph;
            this.color = color;
            Size = new Size(Theme.S(40), Theme.S(40));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(10), Theme.Alpha(color, 30), Color.Transparent);
            Theme.DrawGlyph(g, glyph, 15f, ClientRectangle, color);
        }
    }

    // Dark replacement for MessageBox.
    static class DarkDialog
    {
        public static DialogResult Show(IWin32Window owner, string text, MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.Information, bool defaultNo = false)
        {
            using (var form = new DarkForm())
            {
                form.Text = "Device Toggle";
                form.FormBorderStyle = FormBorderStyle.FixedSingle;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowIcon = false;
                form.ShowInTaskbar = owner == null;
                form.TopMost = owner == null;
                form.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;

                string glyph = Theme.GlyphInfo;
                Color color = Theme.Accent;
                if (icon == MessageBoxIcon.Warning) { glyph = Theme.GlyphWarning; color = Theme.Warning; }
                else if (icon == MessageBoxIcon.Error) { glyph = Theme.GlyphWarning; color = Theme.Danger; }
                else if (icon == MessageBoxIcon.Question) { glyph = Theme.GlyphQuestion; color = Theme.Accent; }

                int pad = Theme.S(24);
                var glyphView = new GlyphView(glyph, color) { Location = new Point(pad, pad) };
                var label = new Label
                {
                    Text = text,
                    ForeColor = Theme.Text,
                    BackColor = Theme.Background,
                    Font = Theme.Body,
                    UseMnemonic = false,
                    AutoSize = true,
                    MaximumSize = new Size(Theme.S(400), 0),
                };
                var labelSize = label.GetPreferredSize(new Size(Theme.S(400), 0));
                label.Location = new Point(glyphView.Right + Theme.S(16), pad + Math.Max(0, (glyphView.Height - labelSize.Height) / 2));

                var choices = new List<FlatButton>();
                if (buttons == MessageBoxButtons.YesNo)
                {
                    choices.Add(new FlatButton("Yes", null, FlatButton.Kind.Accent) { DialogResult = DialogResult.Yes, MinimumSize = new Size(Theme.S(88), 0) });
                    choices.Add(new FlatButton("No", null, FlatButton.Kind.Ghost) { DialogResult = DialogResult.No, MinimumSize = new Size(Theme.S(88), 0) });
                }
                else
                {
                    choices.Add(new FlatButton("OK", null, FlatButton.Kind.Accent) { DialogResult = DialogResult.OK, MinimumSize = new Size(Theme.S(88), 0) });
                }

                int contentBottom = Math.Max(glyphView.Bottom, label.Top + labelSize.Height);
                int width = Math.Max(Theme.S(380), label.Left + labelSize.Width + pad);
                int buttonTop = contentBottom + Theme.S(24);
                int x = width - pad;
                foreach (var b in Enumerable.Reverse(choices))
                {
                    x -= b.Width;
                    b.Location = new Point(x, buttonTop);
                    x -= Theme.S(10);
                }
                form.ClientSize = new Size(width, buttonTop + choices[0].Height + pad);

                form.Controls.Add(glyphView);
                form.Controls.Add(label);
                foreach (var b in choices) form.Controls.Add(b);
                var no = choices.Last();
                form.AcceptButton = defaultNo ? no : choices[0];
                form.CancelButton = no;
                return form.ShowDialog(owner);
            }
        }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.CardHover; } }
            public override Color MenuItemSelected { get { return Theme.CardHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHover; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Card; } }
        }

        public DarkMenuRenderer() : base(new Colors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }
    }

    // ------------------------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------------------------

    class Session
    {
        public ProgramEntry Program;
        public List<DeviceInfo> TurnedOff;
        public ProgramWatcher Watcher;
        public DateTime Started;
        public bool Seen;
        public string RunningNow;
    }

    class MainForm : DarkForm
    {
        const string AppName = "Device Toggle";
        // How long a program may take to show up after being started.
        static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);

        readonly Settings settings;
        readonly string startupLaunch;
        bool launchMode;
        bool exiting;
        List<DeviceInfo> devices = new List<DeviceInfo>();
        Session session;

        HeaderBar header;
        StatusBar footer;
        CardList list;
        readonly Dictionary<string, DeviceCard> deviceCards = new Dictionary<string, DeviceCard>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<ProgramEntry, ProgramCard> programCards = new Dictionary<ProgramEntry, ProgramCard>();
        readonly Dictionary<string, Caption> captions = new Dictionary<string, Caption>();
        Caption noDevicesHint, noProgramsHint;
        FlatButton addProgramButton;
        NotifyIcon tray;
        ToolStripMenuItem trayStopItem;
        System.Windows.Forms.Timer sessionTimer;
        System.Windows.Forms.Timer refreshTimer;

        public MainForm(string startupLaunch)
        {
            this.startupLaunch = startupLaunch;
            settings = Settings.Load();

            Text = AppName;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(S(660), S(780));
            MinimumSize = new Size(S(500), S(480));

            if (startupLaunch != null)
            {
                // Started from a shortcut: stay in the tray.
                Opacity = 0;
                ShowInTaskbar = false;
            }

            BuildLayout();
            BuildTray();

            sessionTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            sessionTimer.Tick += delegate { CheckSession(); };
            refreshTimer = new System.Windows.Forms.Timer { Interval = 700 };
            refreshTimer.Tick += delegate { refreshTimer.Stop(); RefreshDevices(); };
        }

        static int S(float value)
        {
            return Theme.S(value);
        }

        // ---- Layout -------------------------------------------------------------------

        void BuildLayout()
        {
            header = new HeaderBar(AppName.ToUpperInvariant());
            header.RefreshButton.Click += delegate { RefreshDevices(); };
            footer = new StatusBar();
            footer.StopButton.Click += delegate { EndSession(false); };
            list = new CardList { Dock = DockStyle.Fill };

            addProgramButton = new FlatButton("Add program", Theme.GlyphAdd, FlatButton.Kind.Ghost);
            addProgramButton.Click += delegate { AddProgram(); };
            noDevicesHint = new Caption("No USB or Bluetooth devices found.", true);
            noProgramsHint = new Caption("Add a program to start it with chosen devices turned off. They turn back on when it closes.", true);

            Controls.Add(header);
            Controls.Add(footer);
            Controls.Add(list);
            list.BringToFront();
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.Body };
            var open = new ToolStripMenuItem("Open " + AppName, null, delegate { ShowFromTray(); });
            open.Font = new Font(Theme.Body, FontStyle.Bold);
            trayStopItem = new ToolStripMenuItem("Turn devices back on now", null, delegate { EndSession(false); }) { Enabled = false };
            menu.Items.Add(open);
            menu.Items.Add(trayStopItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApp(); }));

            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = AppName, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        // Rebuilds the card list from the current devices, programs and session.
        void RebuildList()
        {
            var items = new List<Control>();
            var live = new HashSet<Control>();

            string lastGroup = null;
            foreach (var d in devices.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string group = DeviceInfo.GroupName(d.Kind);
                if (group != lastGroup)
                {
                    items.Add(GetCaption(group));
                    lastGroup = group;
                }
                DeviceCard card;
                if (deviceCards.TryGetValue(d.InstanceId, out card))
                {
                    card.SetDevice(d);
                }
                else
                {
                    card = new DeviceCard(d, false);
                    card.ToggleRequested += delegate(object sender, EventArgs e) { OnToggle((DeviceCard)sender); };
                    deviceCards[d.InstanceId] = card;
                }
                items.Add(card);
                live.Add(card);
            }
            if (devices.Count == 0)
            {
                items.Add(GetCaption("Devices"));
                items.Add(noDevicesHint);
            }

            items.Add(GetCaption("Launch with devices off"));
            foreach (var p in settings.Programs)
            {
                ProgramCard card;
                if (!programCards.TryGetValue(p, out card))
                {
                    card = new ProgramCard(p);
                    var program = p;
                    card.LaunchButton.Click += delegate { Launch(program); };
                    card.StopButton.Click += delegate { EndSession(false); };
                    card.EditButton.Click += delegate { EditProgram(program); };
                    card.ShortcutButton.Click += delegate { CreateShortcut(program); };
                    card.RemoveButton.Click += delegate { RemoveProgram(program); };
                    programCards[p] = card;
                }
                bool running = session != null && session.Program == p;
                string runningText = running
                    ? "Running   ·   devices back on when " + (session.RunningNow ?? Path.GetFileName(p.ExePath)) + " closes"
                    : null;
                card.Update(DescribeDevices(p.DeviceKeys), runningText, session == null);
                items.Add(card);
                live.Add(card);
            }
            if (settings.Programs.Count == 0) items.Add(noProgramsHint);
            items.Add(addProgramButton);

            list.SetItems(items);

            // Throw away cards for devices and programs that are gone.
            foreach (var pair in deviceCards.Where(p => !live.Contains(p.Value)).ToList())
            {
                deviceCards.Remove(pair.Key);
                pair.Value.Dispose();
            }
            foreach (var pair in programCards.Where(p => !live.Contains(p.Value)).ToList())
            {
                programCards.Remove(pair.Key);
                pair.Value.Dispose();
            }
        }

        Caption GetCaption(string text)
        {
            Caption caption;
            if (!captions.TryGetValue(text, out caption))
            {
                caption = new Caption(text, false);
                captions[text] = caption;
            }
            return caption;
        }

        // ---- Startup / shutdown --------------------------------------------------------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RefreshDevices();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            OfferRestore();

            if (startupLaunch == null) return;
            launchMode = true;
            Hide();
            Opacity = 1;
            ShowInTaskbar = true;

            var program = FindProgram(startupLaunch);
            if (program == null)
                DarkDialog.Show(null, "\"" + startupLaunch + "\" is not in " + AppName + "'s program list any more.", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Launch(program);
            if (session == null) ExitApp();
        }

        // Shortcuts pass the program's path (older ones its name).
        ProgramEntry FindProgram(string pathOrName)
        {
            return settings.Programs.FirstOrDefault(p => p.ExePath.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)) ??
                   settings.Programs.FirstOrDefault(p => p.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));
        }

        IWin32Window DialogOwner
        {
            get { return Visible ? this : null; }
        }

        // The app can be closed unexpectedly while it has devices turned off; those stay off,
        // even after a restart. Offer to turn them back on.
        void OfferRestore()
        {
            if (settings.PendingRestore.Count == 0) return;
            var answer = DarkDialog.Show(DialogOwner,
                AppName + " was closed while it had " + settings.PendingRestore.Count + " device(s) turned off for a program.\n\nTurn them back on now?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                var errors = new List<string>();
                foreach (string id in settings.PendingRestore)
                {
                    var device = devices.FirstOrDefault(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    string error = Apply(id, device != null ? device.Name : id, true);
                    if (error != null) errors.Add(error);
                }
                ShowErrors(errors);
            }
            settings.PendingRestore.Clear();
            SaveSettings();
            RefreshDevices();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (session != null && !exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // Keep watching the program from the tray.
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(5000, AppName, AppName + " keeps running in the tray until " + session.Program.Name + " closes.", ToolTipIcon.Info);
                return;
            }
            if (session != null) EndSession(true);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            Close();
        }

        void ShowFromTray()
        {
            launchMode = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DEVICECHANGE && refreshTimer != null)
            {
                // Devices were added, removed or changed. Refresh once things settle.
                refreshTimer.Stop();
                refreshTimer.Start();
            }
            else if (m.Msg == App.ActivateMessage && m.Msg != 0)
            {
                OnActivateRequest(m.WParam.ToInt32() == 1);
            }
            base.WndProc(ref m);
        }

        // Another copy of the app was started (for example from a desktop shortcut).
        void OnActivateRequest(bool hasLaunchRequest)
        {
            if (!hasLaunchRequest)
            {
                ShowFromTray();
                return;
            }
            string request = App.TakePendingLaunch();
            var program = request == null ? null : FindProgram(request);
            if (program == null)
            {
                ShowFromTray();
                return;
            }
            Launch(program);
        }

        // ---- Devices -------------------------------------------------------------------

        void RefreshDevices()
        {
            try
            {
                devices = DeviceManager.Enumerate(settings.KnownDevices);
            }
            catch (Exception ex)
            {
                SetStatus("Could not read the device list: " + ex.Message);
                return;
            }
            Remember(devices);
            RebuildList();
            if (session == null)
                SetStatus(devices.Count + " devices   ·   " + devices.Count(d => d.Disabled) + " turned off");
        }

        // Keep each working device's name and type, for when it is turned off.
        void Remember(List<DeviceInfo> found)
        {
            bool changed = false;
            foreach (var d in found.Where(d => !d.Disabled))
            {
                KnownDevice known;
                if (settings.KnownDevices.TryGetValue(d.Key, out known) && known.Kind == d.Kind && known.Name == d.Name) continue;
                settings.KnownDevices[d.Key] = new KnownDevice { Kind = d.Kind, Name = d.Name };
                changed = true;
            }
            if (changed) SaveSettings();
        }

        // The switch on a card was clicked. The change runs in the background so the switch can animate.
        void OnToggle(DeviceCard card)
        {
            var device = card.Device;
            bool enable = device.Disabled;
            if (!enable && !ConfirmRisky(new[] { device })) return;

            card.BeginChange(enable);
            Task.Factory.StartNew(() => Apply(device.InstanceId, device.Name, enable))
                .ContinueWith(t =>
                {
                    card.EndChange();
                    RefreshDevices();
                    if (t.Result != null) ShowErrors(new List<string> { t.Result });
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        bool ConfirmRisky(IEnumerable<DeviceInfo> targets)
        {
            var risky = targets.Where(d => d.IsRisky).ToList();
            if (risky.Count == 0) return true;
            var answer = DarkDialog.Show(DialogOwner,
                "You are about to turn off:\n\n" + string.Join("\n", risky.Select(d => "  " + d.Name + " (" + d.KindText.ToLowerInvariant() + ")")) +
                "\n\nIf this is the keyboard or mouse you are using right now, you will lose control of it until you turn it back on. Continue?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, true);
            return answer == DialogResult.Yes;
        }

        // Returns null on success, otherwise a message for the user.
        static string Apply(string instanceId, string name, bool enable)
        {
            try
            {
                if (!DeviceManager.SetEnabled(instanceId, enable))
                    return name + ": Windows needs a restart to finish turning it " + (enable ? "on." : "off.");
                return null;
            }
            catch (Exception ex)
            {
                return name + ": " + ex.Message;
            }
        }

        void ShowErrors(List<string> errors)
        {
            if (errors.Count == 0) return;
            DarkDialog.Show(DialogOwner, string.Join("\n\n", errors), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---- Programs ------------------------------------------------------------------

        string DescribeDevices(List<string> keys)
        {
            if (keys.Count == 0) return "(nothing)";
            return string.Join(", ", keys.Select(k =>
            {
                var d = devices.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (d != null) return d.Name;
                KnownDevice known;
                return settings.KnownDevices.TryGetValue(k, out known) ? known.Name + " (not connected)" : k + " (not connected)";
            }).Distinct());
        }

        void AddProgram()
        {
            using (var dialog = new ProgramDialog(null, devices, settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                settings.Programs.Add(dialog.Result);
                SaveSettings();
                RebuildList();
            }
        }

        void EditProgram(ProgramEntry program)
        {
            using (var dialog = new ProgramDialog(program, devices, settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int index = settings.Programs.IndexOf(program);
                if (index < 0) return;
                settings.Programs[index] = dialog.Result;
                SaveSettings();
                RebuildList();
            }
        }

        void RemoveProgram(ProgramEntry program)
        {
            if (DarkDialog.Show(this, "Remove " + program.Name + " from the list?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Programs.Remove(program);
            SaveSettings();
            RebuildList();
        }

        void CreateShortcut(ProgramEntry program)
        {
            string fileName = program.Name + " (devices off).lnk";
            foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "--launch \"" + program.ExePath + "\"" });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Start " + program.Name + " with devices turned off" });
                if (File.Exists(program.ExePath))
                    linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { program.ExePath + ",0" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                DarkDialog.Show(this,
                    "Created \"" + Path.GetFileNameWithoutExtension(fileName) + "\" on your desktop.\n\n" +
                    "It starts " + program.Name + " with its devices turned off and turns them back on when it closes. " +
                    "Keep DeviceToggle.exe where it is, or the shortcut will stop working.");
            }
            catch (Exception ex)
            {
                DarkDialog.Show(this, "Could not create the shortcut: " + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- Launch sessions -----------------------------------------------------------

        void Launch(ProgramEntry program)
        {
            if (session != null)
            {
                DarkDialog.Show(DialogOwner, session.Program.Name + " is already running with devices turned off. Wait until it closes, or click \"Turn devices back on now\".");
                return;
            }
            if (!File.Exists(program.ExePath))
            {
                DarkDialog.Show(DialogOwner, "Could not find " + program.ExePath + ".\n\nUse the edit button to pick the program again.", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RefreshDevices();
            var targets = devices
                .Where(d => !d.Disabled && program.DeviceKeys.Contains(d.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var turnedOff = new List<DeviceInfo>();
            var errors = new List<string>();
            UseWaitCursor = true;
            try
            {
                foreach (var d in targets)
                {
                    string error = Apply(d.InstanceId, d.Name, false);
                    if (error == null) turnedOff.Add(d);
                    else errors.Add(error);
                }
            }
            finally
            {
                UseWaitCursor = false;
            }
            settings.PendingRestore = turnedOff.Select(d => d.InstanceId).ToList();
            SaveSettings();

            session = new Session
            {
                Program = program,
                TurnedOff = turnedOff,
                Watcher = new ProgramWatcher(program.ExePath),
                Started = DateTime.Now,
            };
            try
            {
                ShellLauncher.Start(program.ExePath);
            }
            catch (Exception ex)
            {
                errors.Add("Could not start " + program.Name + ": " + ex.Message);
                EndSession(true);
                ShowErrors(errors);
                return;
            }

            sessionTimer.Start();
            UpdateSessionUi();
            RefreshDevices();
            ShowErrors(errors);

            tray.ShowBalloonTip(5000, program.Name,
                turnedOff.Count == 0
                    ? "Started. None of its devices needed turning off."
                    : "Started. " + string.Join(", ", turnedOff.Select(d => d.Name).Distinct()) + " turned off until it closes.",
                ToolTipIcon.Info);
        }

        void CheckSession()
        {
            if (session == null) return;
            string running = session.Watcher.FindRunning();
            if (running != null)
            {
                session.Seen = true;
                if (running != session.RunningNow)
                {
                    session.RunningNow = running;
                    UpdateSessionUi();
                }
                return;
            }
            if (session.Seen || DateTime.Now - session.Started > StartGrace)
                EndSession(false);
        }

        // Turns the session's devices back on. quiet: no notification (used when exiting).
        void EndSession(bool quiet)
        {
            if (session == null) return;
            var ended = session;
            session = null;
            sessionTimer.Stop();

            var errors = new List<string>();
            foreach (var d in ended.TurnedOff)
            {
                string error = Apply(d.InstanceId, d.Name, true);
                if (error != null) errors.Add(error);
            }
            settings.PendingRestore.Clear();
            SaveSettings();

            if (!exiting)
            {
                UpdateSessionUi();
                RefreshDevices();
            }
            if (!quiet)
            {
                if (ended.TurnedOff.Count > 0 || !ended.Seen)
                {
                    tray.ShowBalloonTip(5000, ended.Program.Name,
                        ended.Seen ? "Closed. Devices turned back on." : "It didn't seem to start, so the devices were turned back on.",
                        ToolTipIcon.Info);
                }
                ShowErrors(errors);
            }

            if (launchMode && !exiting && !Visible) ExitApp();
        }

        void UpdateSessionUi()
        {
            bool active = session != null;
            footer.StopButton.Visible = active;
            trayStopItem.Enabled = active;
            if (active)
            {
                string waitingFor = session.RunningNow ?? Path.GetFileName(session.Program.ExePath);
                SetStatus(session.Program.Name + " is running   ·   " + session.TurnedOff.Count + " device(s) off until " + waitingFor + " closes");
            }
            tray.Text = active ? Truncate(AppName + ": " + session.Program.Name + " running", 63) : AppName;
            RebuildList();
        }

        static string Truncate(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length);
        }

        void SetStatus(string text)
        {
            footer.Status = text;
        }

        void SaveSettings()
        {
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                SetStatus("Could not save settings: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Add / edit program dialog
    // ------------------------------------------------------------------------------------

    class ProgramDialog : DarkForm
    {
        public ProgramEntry Result;

        readonly ProgramEntry original;
        readonly Settings settings;
        readonly InputBox pathBox;
        readonly List<DeviceCard> cards = new List<DeviceCard>();

        public ProgramDialog(ProgramEntry existing, List<DeviceInfo> devices, Settings settings)
        {
            original = existing;
            this.settings = settings;

            Text = existing == null ? "Add program" : existing.Name;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.S(580), Theme.S(620));
            MinimumSize = new Size(Theme.S(440), Theme.S(420));
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Theme.Background,
                Padding = new Padding(Theme.S(20), Theme.S(4), Theme.S(20), Theme.S(18)),
            };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(new Caption("Program", false) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.S(8)) }, 0, 0);

            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0), BackColor = Theme.Background };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathBox = new InputBox { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var browse = new FlatButton("Browse", Theme.GlyphFolder, FlatButton.Kind.Ghost) { Margin = new Padding(Theme.S(10), Theme.S(3), 0, 0) };
            browse.Click += delegate { Browse(); };
            pathRow.Controls.Add(pathBox, 0, 0);
            pathRow.Controls.Add(browse, 1, 0);
            grid.Controls.Add(pathRow, 0, 1);

            grid.Controls.Add(new Caption("Turn off while it runs", false) { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(10), 0, Theme.S(4)) }, 0, 2);

            var deviceList = new CardList { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, Theme.S(4), Theme.S(4), Theme.S(4)) };
            grid.Controls.Add(deviceList, 0, 3);

            var ok = new FlatButton("OK", Theme.GlyphCheck, FlatButton.Kind.Accent) { MinimumSize = new Size(Theme.S(96), 0) };
            ok.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", null, FlatButton.Kind.Ghost) { DialogResult = DialogResult.Cancel, MinimumSize = new Size(Theme.S(96), 0) };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Margin = new Padding(0, Theme.S(14), 0, 0),
            };
            ok.Margin = new Padding(Theme.S(10), 0, 0, 0);
            cancel.Margin = new Padding(0);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            grid.Controls.Add(buttons, 0, 4);

            Controls.Add(grid);
            AcceptButton = ok;
            CancelButton = cancel;

            FillDevices(existing, devices);
            deviceList.SetItems(cards.Cast<Control>().ToList());
            if (existing != null) pathBox.Box.Text = existing.ExePath;
        }

        void FillDevices(ProgramEntry existing, List<DeviceInfo> devices)
        {
            var chosen = existing != null ? existing.DeviceKeys : new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!seen.Add(d.Key)) continue;
                cards.Add(new DeviceCard(d, true) { Checked = chosen.Contains(d.Key, StringComparer.OrdinalIgnoreCase) });
            }
            foreach (string key in chosen.Where(k => !seen.Contains(k)))
            {
                KnownDevice known;
                settings.KnownDevices.TryGetValue(key, out known);
                var stub = new DeviceInfo
                {
                    Key = key,
                    InstanceId = key,
                    Name = known != null ? known.Name : key,
                    Kind = known != null ? known.Kind : DeviceKind.Other,
                    DisplayId = "",
                };
                cards.Add(new DeviceCard(stub, true) { Checked = true, Subtitle = "Not connected right now" });
            }
        }

        void Browse()
        {
            using (var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a program" })
            {
                if (File.Exists(pathBox.Box.Text)) dialog.InitialDirectory = Path.GetDirectoryName(pathBox.Box.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Box.Text = dialog.FileName;
            }
        }

        void Accept()
        {
            string path = pathBox.Box.Text.Trim().Trim('"');
            if (!File.Exists(path))
            {
                Warn("Choose the program to start (an .exe file).");
                return;
            }
            if (settings.Programs.Any(p => p != original && p.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                Warn("This program is already in the list. Use its edit button instead.");
                return;
            }
            var keys = cards.Where(c => c.Checked).Select(c => c.Device.Key).ToList();
            if (keys.Count == 0)
            {
                Warn("Tick at least one device to turn off while the program runs.");
                return;
            }

            Result = new ProgramEntry
            {
                ExePath = path,
                Name = ProgramEntry.NameFor(path),
                DeviceKeys = keys,
            };
            DialogResult = DialogResult.OK;
        }

        void Warn(string message)
        {
            DarkDialog.Show(this, message, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------------------------

    static class App
    {
        // Lets a second copy (e.g. started from a desktop shortcut) hand its request to the running one.
        public static readonly int ActivateMessage = Native.RegisterWindowMessageW("DeviceToggle.Activate.5B1E2C77");
        static readonly string PendingLaunchFile = Path.Combine(Settings.Folder, "pending-launch.txt");

        [STAThread]
        static void Main(string[] args)
        {
            string launch = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    launch = args[++i];
            }

            bool firstInstance;
            using (var mutex = new Mutex(true, "Local\\DeviceToggle.5B1E2C77", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (launch != null)
                    {
                        Directory.CreateDirectory(Settings.Folder);
                        File.WriteAllText(PendingLaunchFile, launch, Encoding.UTF8);
                    }
                    Native.PostMessageW(Native.HWND_BROADCAST, ActivateMessage, new IntPtr(launch != null ? 1 : 0), IntPtr.Zero);
                    return;
                }

                Application.EnableVisualStyles();
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(launch));
                GC.KeepAlive(mutex);
            }
        }

        public static string TakePendingLaunch()
        {
            try
            {
                if (!File.Exists(PendingLaunchFile)) return null;
                string request = File.ReadAllText(PendingLaunchFile, Encoding.UTF8).Trim();
                File.Delete(PendingLaunchFile);
                return request;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
