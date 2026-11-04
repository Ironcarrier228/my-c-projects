using System.Runtime.InteropServices;

namespace UsbWatch.Core;

public static class CfgMgr32Native
{
    public const int CR_SUCCESS = 0;

    public const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    public const int CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    public const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr Context, int Action, IntPtr EventData, int EventDataSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct CM_NOTIFY_FILTER
    {
        public int cbSize;       // 0
        public int Flags;        // 4
        public int FilterType;   // 8
        public Guid Reserved;    // 12-27
        public int Pad1;         // 28-31 (native union align 8 для HANDLE)
        public Guid ClassGuid;   // 32-47 (union DeviceInterface)
    }   // total: 48 bytes

    [DllImport("CfgMgr32.dll", SetLastError = false)]
    public static extern int CM_Register_Notification(ref CM_NOTIFY_FILTER pFilter, IntPtr Context,
        CM_NOTIFY_CALLBACK pCallback, out IntPtr pNotifyContext);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Unregister_Notification(IntPtr NotifyContext);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid InterfaceClassGuid, string? DevicePath,
        [Out] char[]? Buffer, ref int BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out IntPtr DevInst, string DeviceId, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Get_Parent(out IntPtr Parent, IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Get_Child(out IntPtr Child, IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Get_Sibling(out IntPtr Sibling, IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(IntPtr DevInst, [Out] char[] Buffer, int BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(IntPtr DevInst, [Out] char[]? Buffer, ref int BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint Status, out uint Problem, IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Disable_DevNode(IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    public static extern int CM_Enable_DevNode(IntPtr DevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_ListW(string pszFilter, [Out] char[]? Buffer, ref int BufferLen, uint ulFlags);

    public const uint CM_GETIDLIST_FILTER_NONE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid FmtId;
        public uint Pid;
        public DEVPROPKEY(Guid f, uint p) { FmtId = f; Pid = p; }
    }

    public static readonly DEVPROPKEY DEVPKEY_Device_ClassGuid = new(new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), 1);
    public static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 3);

    public const uint CM_GET_DEVNODE_PROPNAMES = 0;
    public const uint DEVPROP_TYPE_STRING = 0x12;
    public const uint DEVPROP_TYPE_STRING_LIST = 0x12;
    public const uint DEVPROP_TYPE_GUID = 0x0B;
    public const uint CM_DISABLE_PERSISTENT = 1;

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(IntPtr DevInst, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, [Out] byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, int dwShareMode,
        IntPtr lpSecurityAttributes, int dwCreationDisposition, int dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("Kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("Kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint GENERIC_READ = 0x80000000;
    public const int FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const int OPEN_EXISTING = 3;
    public const uint IOCTL_STORAGE_SET_READ_ONLY = 0x2D1400;

    public static string GetDeviceId(IntPtr devInst)
    {
        var len = 0;
        CM_Get_Device_IDW(devInst, null, ref len, 0);
        var buf = new char[len];
        CM_Get_Device_IDW(devInst, buf, len, 0);
        return new string(buf).TrimEnd('\0');
    }

    public static string[] GetDeviceIdList(string filter)
    {
        var len = 0;
        CM_Get_Device_ID_ListW(filter, null, ref len, CM_GETIDLIST_FILTER_NONE);
        var buf = new char[len];
        CM_Get_Device_ID_ListW(filter, buf, ref len, CM_GETIDLIST_FILTER_NONE);
        return new string(buf).TrimEnd('\0').Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string? GetStringDevProp(IntPtr devInst, DEVPROPKEY key)
    {
        uint type = 0, size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, CM_GET_DEVNODE_PROPNAMES);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, CM_GET_DEVNODE_PROPNAMES) != CR_SUCCESS)
            return null;
        return type == DEVPROP_TYPE_STRING
            ? Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0))
            : null;
    }

    public static string[] GetStringListDevProp(IntPtr devInst, DEVPROPKEY key)
    {
        uint type = 0, size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, CM_GET_DEVNODE_PROPNAMES);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, CM_GET_DEVNODE_PROPNAMES) != CR_SUCCESS)
            return Array.Empty<string>();
        return type == DEVPROP_TYPE_STRING_LIST
            ? (Marshal.PtrToStringUni(Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0)) ?? "")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }

    public static string GetClassGuidString(IntPtr devInst)
    {
        uint type = 0, size = 0;
        var key = DEVPKEY_Device_ClassGuid;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, CM_GET_DEVNODE_PROPNAMES);
        if (size < 16) return "";
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, CM_GET_DEVNODE_PROPNAMES) != CR_SUCCESS)
            return "";
        return new Guid(buf.AsSpan(0, 16)).ToString("B");
    }
}
