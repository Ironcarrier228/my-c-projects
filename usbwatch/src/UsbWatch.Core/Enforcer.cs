using System.Runtime.InteropServices;

namespace UsbWatch.Core;

public class Enforcer
{
    /// <summary>Отключить devnode (для составного устройства — узел конкретного интерфейса).</summary>
    public static bool DisableNode(string deviceId, bool persistent)
    {
        if (CfgMgr32Native.CM_Locate_DevNodeW(out var dev, deviceId, 0) != CfgMgr32Native.CR_SUCCESS) return false;
        return CfgMgr32Native.CM_Disable_DevNode(dev, persistent ? CfgMgr32Native.CM_DISABLE_PERSISTENT : 0)
               == CfgMgr32Native.CR_SUCCESS;
    }

    public static bool EnableNode(string deviceId)
    {
        if (CfgMgr32Native.CM_Locate_DevNodeW(out var dev, deviceId, 0) != CfgMgr32Native.CR_SUCCESS) return false;
        return CfgMgr32Native.CM_Enable_DevNode(dev, 0) == CfgMgr32Native.CR_SUCCESS;
    }

    /// <summary>Найти дочерний devnode по классу (например, HID-функцию составного устройства).</summary>
    public static string? FindChildByClass(string parentInstanceId, string classGuid)
    {
        if (CfgMgr32Native.CM_Locate_DevNodeW(out var parent, parentInstanceId, 0) != CfgMgr32Native.CR_SUCCESS)
            return null;
        if (CfgMgr32Native.CM_Get_Child(out var child, parent, 0) != CfgMgr32Native.CR_SUCCESS) return null;
        var node = child;
        while (true)
        {
            if (CfgMgr32Native.GetClassGuidString(node).Equals(classGuid, StringComparison.OrdinalIgnoreCase))
                return CfgMgr32Native.GetDeviceId(node);
            if (CfgMgr32Native.CM_Get_Sibling(out var sib, node, 0) != CfgMgr32Native.CR_SUCCESS) return null;
            node = sib;
        }
    }

    /// <summary>
    /// Read-only для накопителя: IOCTL_STORAGE_SET_READ_ONLY.
    /// Честно: работает только если прошивка устройства поддерживает software write-protect.
    /// </summary>
    public static bool TrySetStorageReadOnly(string diskInterfacePath, bool readOnly)
    {
        var h = CfgMgr32Native.CreateFileW(diskInterfacePath, CfgMgr32Native.GENERIC_READ,
            CfgMgr32Native.FILE_SHARE_READ | CfgMgr32Native.FILE_SHARE_WRITE, IntPtr.Zero,
            CfgMgr32Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == IntPtr.Zero || h == (IntPtr)(-1)) return false;
        try
        {
            var bytes = BitConverter.GetBytes(readOnly ? 1 : 0);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return CfgMgr32Native.DeviceIoControl(h, CfgMgr32Native.IOCTL_STORAGE_SET_READ_ONLY,
                    pinned.AddrOfPinnedObject(), bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            finally { pinned.Free(); }
        }
        finally { CfgMgr32Native.CloseHandle(h); }
    }
}
