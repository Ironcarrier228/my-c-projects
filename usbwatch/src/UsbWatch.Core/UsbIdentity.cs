using System.Security.Cryptography;
using System.Text;

namespace UsbWatch.Core;

public record UsbDeviceInfo
{
    public required string DeviceInstanceId;
    public required string Vid;
    public required string Pid;
    public required string Serial;
    public required string ClassGuid;
    public required string[] InterfaceClasses;
    public required string[] HubPath;
    public required bool IsComposite;
    public required string ParentHubDeviceId;
}

public static class UsbIdentity
{
    /// <summary>Каноническая строка: порядок интерфейсов нормализован сортировкой,
    /// VID/PID в нижний регистр (их подделывают точной копией), серийник — как есть.</summary>
    public static string Canonical(UsbDeviceInfo d) => string.Join("|",
        "usb2",
        "vid=" + d.Vid.ToLowerInvariant(),
        "pid=" + d.Pid.ToLowerInvariant(),
        "ser=" + (string.IsNullOrEmpty(d.Serial) ? "NOSER" : d.Serial),
        "cls=" + d.ClassGuid.ToLowerInvariant(),
        "comp=" + (d.IsComposite ? 1 : 0),
        "if=" + string.Join(",", d.InterfaceClasses
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToLowerInvariant())),
        "hub=" + string.Join("/", d.HubPath.Select(x => x.ToLowerInvariant())));

    public static string Fingerprint(UsbDeviceInfo d)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(d)));
        return "usb2:sha256:" + Convert.ToHexString(h).ToLowerInvariant();
    }

    public static UsbDeviceInfo? BuildFromInterfaceNode(IntPtr interfaceDevInst)
    {
        var node = interfaceDevInst;
        var id = CfgMgr32Native.GetDeviceId(node);
        var guard = 0;
        while (!id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && guard++ < 10)
        {
            if (CfgMgr32Native.CM_Get_Parent(out var parent, node, 0) != CfgMgr32Native.CR_SUCCESS) return null;
            node = parent;
            id = CfgMgr32Native.GetDeviceId(node);
        }
        if (!id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = id.Split('\\');
        var ids = parts.Length > 1 ? parts[1].Split('&') : Array.Empty<string>();
        var vid = ids.FirstOrDefault(x => x.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))?[4..] ?? "0000";
        var pid = ids.FirstOrDefault(x => x.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))?[4..] ?? "0000";
        var serial = parts.Length > 2 ? parts[2] : "";

        var ifClasses = new List<string>();
        var isComposite = false;
        if (CfgMgr32Native.CM_Get_Child(out var child, node, 0) == CfgMgr32Native.CR_SUCCESS)
        {
            isComposite = true;
            CollectChildClasses(child, ifClasses);
        }

        var hub = new List<string>();
        var n = node;
        while (CfgMgr32Native.CM_Get_Parent(out var p, n, 0) == CfgMgr32Native.CR_SUCCESS)
        {
            hub.Insert(0, CfgMgr32Native.GetDeviceId(p));
            n = p;
        }

        return new UsbDeviceInfo
        {
            DeviceInstanceId = id,
            Vid = vid,
            Pid = pid,
            Serial = serial,
            ClassGuid = CfgMgr32Native.GetClassGuidString(node),
            InterfaceClasses = ifClasses.ToArray(),
            HubPath = hub.ToArray(),
            IsComposite = isComposite,
            ParentHubDeviceId = hub.Count > 0 ? hub[^1] : id,
        };
    }

    private static void CollectChildClasses(IntPtr child, List<string> acc)
    {
        acc.Add(CfgMgr32Native.GetClassGuidString(child));
        var sib = child;
        while (CfgMgr32Native.CM_Get_Sibling(out var next, sib, 0) == CfgMgr32Native.CR_SUCCESS)
        {
            acc.Add(CfgMgr32Native.GetClassGuidString(next));
            sib = next;
        }
    }
}
