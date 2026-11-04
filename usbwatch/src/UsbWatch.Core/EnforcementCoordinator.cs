namespace UsbWatch.Core;

public record QuarantineEvent(
    string Fingerprint,
    UsbDeviceInfo Device,
    string Reason,
    string DecisionName);

public record EnforcementOutcome(Decision Decision, string Reason, bool Blocked, string? BlockedChildId);

/// <summary>Склейка: отпечаток → решение → заморозка → журнал.</summary>
public class EnforcementCoordinator
{
    private readonly PolicyEngine _engine;
    private readonly BadUsbDetector _detector;
    private readonly TrailLog _log;
    private readonly Dictionary<string, string> _lastFp = new();
    private readonly Dictionary<string, UsbDeviceInfo> _known = new();   // fp -> device
    private Func<string, UsbDeviceInfo?>? _lookup;

    /// <summary>Событие карантина: устройство заморожено, ждёт решения пользователя.</summary>
    public event Action<QuarantineEvent>? OnQuarantined;
    /// <summary>Пользователь доверил устройство (служба сохраняет allow-правило).</summary>
    public event Action<UsbDeviceInfo>? OnTrusted;
    public event Action<string, BadUsbVerdict>? OnBadUsb;

    public EnforcementCoordinator(PolicyEngine engine, BadUsbDetector detector, TrailLog log)
    {
        _engine = engine;
        _detector = detector;
        _log = log;
    }

    public void SetLookup(Func<string, UsbDeviceInfo?> lookup) => _lookup = lookup;
    public UsbDeviceInfo? GetDevice(string fp) => _known.TryGetValue(fp, out var d) ? d : null;

    public DecisionResult HandleArrival(UsbDeviceInfo dev)
    {
        var fp = UsbIdentity.Fingerprint(dev);
        _detector.MarkAttached(fp);
        _known[fp] = dev;

        // Повторная перенумерация / смена дескрипторов: отпечаток того же пути изменился
        if (_lastFp.TryGetValue(dev.DeviceInstanceId, out var prevFp) && prevFp != fp)
        {
            var reason = "смена отпечатка (повторная перенумерация/дескрипторы)";
            FreezeAndNotify(dev, fp, reason, "deny");
            return new(Decision.Deny, reason, true, null);
        }
        _lastFp[dev.DeviceInstanceId] = fp;

        var decision = _engine.Evaluate(dev);
        switch (decision.Decision)
        {
        case Decision.Allow:
            _log.Append("svc", "arrival", dev.DeviceInstanceId, fp, "allow", decision.Reason);
            break;

        case Decision.ReadOnly:
            _log.Append("svc", "arrival", dev.DeviceInstanceId, fp, "readonly", decision.Reason);
            break;

        case Decision.Quarantine:
        case Decision.Deny:
            // МГНОВЕННАЯ заморозка: весь USB-узел отключается до решения пользователя
            DeviceBlock.Freeze(dev.DeviceInstanceId);
            _log.Append("svc", decision.Decision == Decision.Deny ? "deny" : "freeze",
                dev.DeviceInstanceId, fp, decision.Reason, "frozen");
            OnQuarantined?.Invoke(new QuarantineEvent(fp, dev, decision.Reason,
                decision.Decision == Decision.Deny ? "deny" : "quarantine"));
            break;
        }
        return decision;
    }

    /// <summary>BadUSB-детектор (ввод) — заморозка всего устройства.</summary>
    public void HandleBadUsb(string fingerprint, BadUsbVerdict verdict)
    {
        var dev = _lookup?.Invoke(fingerprint) ?? GetDevice(fingerprint);
        var id = dev?.DeviceInstanceId ?? "";
        _log.Append("detector", "badusb", id, fingerprint, "deny", verdict.Reason);
        OnBadUsb?.Invoke(fingerprint, verdict);
        if (dev != null)
        {
            DeviceBlock.Freeze(dev.DeviceInstanceId);
            OnQuarantined?.Invoke(new QuarantineEvent(fingerprint, dev, verdict.Reason, "badusb"));
        }
    }

    private void FreezeAndNotify(UsbDeviceInfo dev, string fp, string reason, string decisionName)
    {
        DeviceBlock.Freeze(dev.DeviceInstanceId);
        _log.Append("svc", decisionName, dev.DeviceInstanceId, fp, decisionName, reason);
        OnQuarantined?.Invoke(new QuarantineEvent(fp, dev, reason, decisionName));
    }

    /// <summary>Доверить: разморозка + событие для сохранения allow-правила службой.</summary>
    public bool Trust(string fp)
    {
        if (!_known.TryGetValue(fp, out var dev)) return false;
        DeviceBlock.Unfreeze(dev.DeviceInstanceId);
        _log.Append("ui", "trust", dev.DeviceInstanceId, fp, "allow", "trusted by user");
        OnTrusted?.Invoke(dev);
        return true;
    }

    /// <summary>Заблокировать навсегда: HardwareID в DenyDeviceIDs (переживает перезагрузку).</summary>
    public bool BlockForever(string fp)
    {
        if (!_known.TryGetValue(fp, out var dev)) return false;
        var hwId = DeviceBlock.HardwareIdOf(dev.DeviceInstanceId);
        DeviceBlock.Unfreeze(dev.DeviceInstanceId);  // сначала разморозка узла...
        DeviceBlock.BlockForever(hwId);              // ...чтобы политика запретила заново
        DeviceBlock.Freeze(dev.DeviceInstanceId);    // и снова заморозка (недоверенное = заморожено)
        _log.Append("ui", "block-forever", dev.DeviceInstanceId, fp, "deny", "blocked: " + hwId);
        return true;
    }


    public bool UnfreezeByFingerprint(string fp)
    {
        if (!_known.TryGetValue(fp, out var dev)) return false;
        return DeviceBlock.Unfreeze(dev.DeviceInstanceId);
    }
}
