using UsbWatch.Core;

namespace UsbWatch.Tests;

public class FingerprintTests
{
    private static UsbDeviceInfo Dev(string[] ifs, string serial = "ABC123", string hub = "ACPI\\PNP0A03\\0") => new()
    {
        DeviceInstanceId = $"USB\\VID_0781&PID_5583\\{serial}",
        Vid = "0781",
        Pid = "5583",
        Serial = serial,
        ClassGuid = "{36fc9e60-c465-11cf-8056-444553540000}",
        InterfaceClasses = ifs,
        IsComposite = ifs.Length > 1,
        HubPath = new[] { hub, "USB\\VID_1A40&PID_0101\\5", "USB\\VID_0781&PID_5583\\" + serial },
        ParentHubDeviceId = hub,
    };

    [Fact]
    public void Порядок_интерфейсов_не_меняет_отпечаток()
    {
        var a = UsbIdentity.Fingerprint(Dev(new[] { "{a}", "{b}" }));
        var b = UsbIdentity.Fingerprint(Dev(new[] { "{b}", "{a}" }));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Подмена_серийника_меняет_отпечаток()
    {
        var a = UsbIdentity.Fingerprint(Dev(Array.Empty<string>()));
        var b = UsbIdentity.Fingerprint(Dev(Array.Empty<string>(), serial: "FAKE"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Смена_хаба_меняет_отпечаток()
    {
        var a = UsbIdentity.Fingerprint(Dev(Array.Empty<string>()));
        var b = UsbIdentity.Fingerprint(Dev(Array.Empty<string>(), hub: "ACPI\\PNP0A03\\9"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Каноническая_строка_содержит_все_поля()
    {
        var c = UsbIdentity.Canonical(Dev(new[] { "{a}" }));
        Assert.Contains("vid=0781", c);
        Assert.Contains("pid=5583", c);
        Assert.Contains("ser=ABC123", c);
        Assert.Contains("if={a}", c);
    }
}
