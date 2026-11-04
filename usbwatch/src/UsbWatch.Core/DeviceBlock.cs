using Microsoft.Win32;

namespace UsbWatch.Core;

/// <summary>
/// Заморозка и персистентная блокировка USB-устройств.
/// Freeze: отключение всего USB-узла (все функции останавливаются сразу).
/// BlockForever: HardwareID в DeviceInstall Restrictions — PnP не даст
/// загрузить драйвер для этого устройства даже после перезагрузки.
/// Ограничение: блокирует всю модель VID/PID (серийник не входит в HardwareID).
/// </summary>
public static class DeviceBlock
{
    private const string RestrictionsKey =
        @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";
    private const string DenySubkey = RestrictionsKey + @"\DenyDeviceIDs";

    public static bool Freeze(string usbDeviceInstanceId) =>
        CfgMgr32Native.CM_Locate_DevNodeW(out var dev, usbDeviceInstanceId, 0)
            == CfgMgr32Native.CR_SUCCESS
        && CfgMgr32Native.CM_Disable_DevNode(dev, 0) == CfgMgr32Native.CR_SUCCESS;

    public static bool Unfreeze(string usbDeviceInstanceId) =>
        CfgMgr32Native.CM_Locate_DevNodeW(out var dev, usbDeviceInstanceId, 0)
            == CfgMgr32Native.CR_SUCCESS
        && CfgMgr32Native.CM_Enable_DevNode(dev, 0) == CfgMgr32Native.CR_SUCCESS;

    /// <summary>HardwareID устройства: USB\VID_x&amp;PID_x (без серийника).</summary>
    public static string HardwareIdOf(string deviceInstanceId)
    {
        var parts = deviceInstanceId.Split('\\');
        return parts.Length >= 2 ? parts[0] + "\\" + parts[1] : deviceInstanceId;
    }

    public static string HardwareIdFromProps(IntPtr devInst)
    {
        var ids = CfgMgr32Native.GetStringListDevProp(devInst, CfgMgr32Native.DEVPKEY_Device_HardwareIds);
        return ids.Length > 0 ? ids[^1] : "";
    }

    /// <summary>
    /// Блокировка навсегда: HardwareID в DenyDeviceIDs (SYSTEM-права нужны —
    /// служба под LocalSystem их имеет). Включает политику + ретроактивность.
    /// </summary>
    public static bool BlockForever(string usbHardwareId)
    {
        using var deny = Registry.LocalMachine.CreateSubKey(DenySubkey);
        if (deny == null) return false;
        var idx = 1;
        while (deny.GetValue(idx.ToString()) != null) idx++;
        deny.SetValue(idx.ToString(), usbHardwareId);

        using var root = Registry.LocalMachine.CreateSubKey(RestrictionsKey);
        root.SetValue("DenyDeviceIDs", 1, RegistryValueKind.DWord);
        root.SetValue("DenyDeviceIDs_Retroactive", 1, RegistryValueKind.DWord);
        return true;
    }

    public static bool UnblockForever(string usbHardwareId)
    {
        using var deny = Registry.LocalMachine.OpenSubKey(DenySubkey, writable: true);
        if (deny == null) return false;
        foreach (var n in deny.GetValueNames())
            if (string.Equals(deny.GetValue(n) as string, usbHardwareId, StringComparison.OrdinalIgnoreCase))
                deny.DeleteValue(n);
        return true;
    }
}
