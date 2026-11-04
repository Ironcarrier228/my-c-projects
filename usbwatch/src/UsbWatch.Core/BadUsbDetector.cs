namespace UsbWatch.Core;

public record KeyStroke(string Fingerprint, long TimestampMs, uint Vk, bool WinDown);
public record BadUsbVerdict(bool Suspect, string Reason);

/// <summary>
/// Эвристики BadUSB. Ввод приходит от трей-агента (LL-хук в сессии пользователя).
/// Учёт ложных срабатываний: устройства из allow-политики не проверяются на «маркеры»
/// (проверяет вызывающий код, он знает политику), макро-клавиатуры дают джиттер,
/// KVM переключения создают паузы (не короткие стабильные интервалы).
/// </summary>
public class BadUsbDetector
{
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _attachWindow = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, List<long>> _strokes = new();
    private readonly Dictionary<string, List<uint>> _lastVks = new();
    private readonly Dictionary<string, long> _attachedAt = new();
    private readonly Dictionary<string, long> _lastStroke = new();
    private readonly Dictionary<string, List<long>> _gaps = new();
    private readonly HashSet<string> _flagged = new();

    public BadUsbDetector(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public void MarkAttached(string fingerprint) => _attachedAt[fingerprint] = Now();

    private long Now() => _clock().Ticks / TimeSpan.TicksPerMillisecond;

    public BadUsbVerdict OnKeyStroke(string fingerprint, uint vk, bool winDown)
    {
        if (_flagged.Contains(fingerprint)) return new(false, "");

        var now = Now();
        if (!_attachedAt.TryGetValue(fingerprint, out var attach))
            _attachedAt[fingerprint] = attach = now;
        var sinceAttachMs = now - attach;

        // интервал между нажатиями (чистые интервалы, без смешивания с абсолютным временем)
        var last = _lastStroke.TryGetValue(fingerprint, out var t) ? t : now;
        if (now > last)
        {
            var gaps = _gaps.TryGetValue(fingerprint, out var gl) ? gl : _gaps[fingerprint] = new List<long>();
            gaps.Add(now - last);
            if (gaps.Count > 24) gaps.RemoveRange(0, gaps.Count - 24);
        }
        _lastStroke[fingerprint] = now;

        var seq = _lastVks.TryGetValue(fingerprint, out var l) ? l : _lastVks[fingerprint] = new List<uint>();
        seq.Add(vk);
        if (seq.Count > 32) seq.RemoveAt(0);


        // 1) Win-комбинация в первые секунды после подключения
        if (winDown && sinceAttachMs < 5000)
            return Flag(fingerprint, $"Win-комбинация через {sinceAttachMs} мс после подключения");

        // 2) нечеловеческая скорость: >=19 промежутков из 20 <12 мс с джиттером <1.5 мс
        if (_gaps.TryGetValue(fingerprint, out var intervals) && intervals.Count >= 20)
        {
            var real = intervals.TakeLast(20).Where(x => x is > 0 and < 12).ToList();
            if (real.Count >= 19)
            {
                var avg = real.Average();
                var jitter = real.Select(x => Math.Abs(x - avg)).Average();
                if (jitter < 1.5)
                    return Flag(fingerprint, $"роботизированный ввод: avg={avg:F1}ms jitter={jitter:F2}");
            }
        }

        // 3) маркеры cmd/powershell в первых символах (после Win-нажатия)
        var text = string.Concat(seq.Select(v => v is >= 0x41 and <= 0x5A ? (char)v : '.'));
        if (sinceAttachMs < 10_000 &&
            (text.Contains("C.M.D") || text.Contains("P.O.W.E.R") || text.Contains(".M.D.S")))
            return Flag(fingerprint, $"маркер cmd/powershell: {text}");

        return new(false, "");
    }

    private BadUsbVerdict Flag(string fp, string reason)
    {
        _flagged.Add(fp);
        return new(true, reason);
    }
}
