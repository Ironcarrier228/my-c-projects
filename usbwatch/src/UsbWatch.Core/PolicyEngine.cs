namespace UsbWatch.Core;

public enum Decision { Allow, Quarantine, Deny, ReadOnly }

public record DecisionResult(Decision Decision, string Reason, bool IsComposite, string? HidChildId);

public class PolicyEngine
{
    private readonly Func<UsbPolicy> _policy;
    private readonly Func<DateTime> _clock;
    private readonly Func<SessionPhase> _phase;

    public PolicyEngine(Func<UsbPolicy> policy, Func<SessionPhase> phase, Func<DateTime>? clock = null)
    {
        _policy = policy;
        _phase = phase;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public const string DiskClass = "{4d36e967-e325-11ce-bfc1-08002be10318}";   // DiskDrive
    public const string HidClass = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";    // HIDClass
    public const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    public const string MouseClass = "{4d36e96f-e325-11ce-bfc1-08002be10318}";

    public DecisionResult Evaluate(UsbDeviceInfo dev)
    {
        var p = _policy();
        var fp = UsbIdentity.Fingerprint(dev);
        var phase = _phase();

        // 1) временные разрешения (TTL), только в активной сессии
        var temp = p.Temporary.FirstOrDefault(t => t.Fingerprint == fp && !t.IsExpired(_clock()));
        if (temp != null && phase == SessionPhase.Active)
            return new(Decision.Allow, $"temp grant ({temp.TtlMinutes}m)", dev.IsComposite, null);

        // 2) явные правила: fingerprint > vidpid > class
        foreach (var r in p.Rules.Where(r => Matches(r, dev, fp)).OrderBy(Rank))
        {
            if (!r.Phases.Contains(PhaseName(phase))) continue;
            var dec = r.Kind switch
            {
                "allow" => Decision.Allow,
                "deny" => Decision.Deny,
                "readOnly" => Decision.ReadOnly,
                _ => Decision.Quarantine
            };
            return new(dec, $"{r.Kind}:{r.Match} ({r.Note})", dev.IsComposite, null);
        }

        // 3) fail-open: HID-only некомпозитная клавиатура/мышь в активной сессии
        var isInputIf = new Func<string, bool>(c =>
            KeyboardClass.Equals(c, StringComparison.OrdinalIgnoreCase) ||
            MouseClass.Equals(c, StringComparison.OrdinalIgnoreCase) ||
            HidClass.Equals(c, StringComparison.OrdinalIgnoreCase));
        var inputOnly = dev.InterfaceClasses.Any(isInputIf)
                   && !dev.InterfaceClasses.Any(c => DiskClass.Equals(c, StringComparison.OrdinalIgnoreCase));
        if (phase == SessionPhase.Active && inputOnly && !dev.IsComposite)
            return new(Decision.Allow, "hid-only default", dev.IsComposite, null);

        // 4) BadUSB-паттерн: составное с накопителем + HID
        if (dev.IsComposite &&
            dev.InterfaceClasses.Any(c => DiskClass.Equals(c, StringComparison.OrdinalIgnoreCase)) &&
            dev.InterfaceClasses.Any(c => HidClass.Equals(c, StringComparison.OrdinalIgnoreCase)))
            return new(Decision.Deny, "composite storage+HID", true, null);

        // 5) default
        return p.DefaultAction switch
        {
            "allow" => new(Decision.Allow, "default allow", dev.IsComposite, null),
            _ => new(Decision.Quarantine, "default quarantine", dev.IsComposite, null)
        };
    }

    public static bool Matches(PolicyRule r, UsbDeviceInfo d, string fp) => r.Match switch
    {
        "fingerprint" => r.Value == fp,
        "vidpid" => r.Value == $"{d.Vid}:{d.Pid}",
        "class" => d.InterfaceClasses.Any(c => c.Equals(r.Value, StringComparison.OrdinalIgnoreCase))
                || d.ClassGuid.Equals(r.Value, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static int Rank(PolicyRule r) => r.Match switch
    { "fingerprint" => 0, "vidpid" => 1, "class" => 2, _ => 3 };

    private static string PhaseName(SessionPhase p) => p switch
    { SessionPhase.Active => "active", SessionPhase.Locked => "locked", _ => "nouser" };
}
