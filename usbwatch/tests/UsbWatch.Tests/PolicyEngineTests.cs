using UsbWatch.Core;

namespace UsbWatch.Tests;

public class PolicyEngineTests
{
    private static PolicyEngine Engine(UsbPolicy p, SessionPhase ph = SessionPhase.Active)
        => new(() => p, () => ph, () => new DateTime(2026, 1, 1));

    private static UsbDeviceInfo Dev(params string[] ifs) => new()
    {
        DeviceInstanceId = "USB\\VID_1&PID_2\\S",
        Vid = "1", Pid = "2", Serial = "S",
        ClassGuid = "{x}",
        InterfaceClasses = ifs,
        IsComposite = ifs.Length > 1,
        HubPath = new[] { "R" },
        ParentHubDeviceId = "R",
    };

    [Fact]
    public void Составная_флешка_с_HID_жестко_запрещается()
    {
        var p = new UsbPolicy { DefaultAction = "allow" };   // даже при allow-everything
        var r = Engine(p).Evaluate(Dev(PolicyEngine.DiskClass, PolicyEngine.HidClass));
        Assert.Equal(Decision.Deny, r.Decision);
        Assert.True(r.IsComposite);
    }

    [Fact]
    public void Геймерская_мышь_не_срабатывает_ложно()   // HID, не composite, active
    {
        var p = new UsbPolicy { DefaultAction = "quarantine" };
        var r = Engine(p).Evaluate(Dev(PolicyEngine.HidClass));
        Assert.Equal(Decision.Allow, r.Decision);
    }

    [Fact]
    public void Гарнитура_не_срабатывает_ложно()          // составная аудио+HID(кнопки), без диска
    {
        var p = new UsbPolicy { DefaultAction = "quarantine" };
        var audio = "{4d36e96c-e325-11ce-bfc1-08002be10318}"; // Media
        var r = Engine(p).Evaluate(Dev(audio, PolicyEngine.HidClass));
        // составная, но без DiskClass — не флешка с HID; правило composite storage+HID не бьёт
        Assert.NotEqual(Decision.Deny, r.Decision);
    }

    [Fact]
    public void NoUser_фаза_строже_даже_для_hid()
    {
        var p = new UsbPolicy { DefaultAction = "quarantine" };
        var r = Engine(p, SessionPhase.NoUser).Evaluate(Dev(PolicyEngine.HidClass));
        Assert.NotEqual(Decision.Allow, r.Decision);
    }

    [Fact]
    public void Temp_grant_истекает()
    {
        var p = new UsbPolicy();
        p.Temporary.Add(new TempGrant
        {
            Fingerprint = "usb2:sha256:x",
            GrantedAtUtc = new DateTime(2025, 1, 1),
            TtlMinutes = 60,
        });
        var r = Engine(p).Evaluate(Dev(PolicyEngine.DiskClass));
        Assert.Equal(Decision.Quarantine, r.Decision);
    }

    [Fact]
    public void Allow_правило_по_отпечатку_перебивает_default()
    {
        var p = new UsbPolicy { DefaultAction = "quarantine" };
        var fp = UsbIdentity.Fingerprint(Dev(PolicyEngine.DiskClass));
        p.Rules.Add(new PolicyRule { Kind = "allow", Match = "fingerprint", Value = fp });
        var r = Engine(p).Evaluate(Dev(PolicyEngine.DiskClass));
        Assert.Equal(Decision.Allow, r.Decision);
    }

    [Fact]
    public void Allow_правила_по_fingerprint_сильнее_чем_vidpid()
    {
        var p = new UsbPolicy();
        p.Rules.Add(new PolicyRule { Kind = "deny", Match = "vidpid", Value = "1:2" });
        p.Rules.Add(new PolicyRule { Kind = "allow", Match = "fingerprint", Value = UsbIdentity.Fingerprint(Dev()) });
        var r = Engine(p).Evaluate(Dev());
        Assert.Equal(Decision.Allow, r.Decision);
    }
}
