using UsbWatch.Core;

namespace UsbWatch.Tests;

public class BadUsbDetectorTests
{
    /// Управляемые часы: детектор видит время, которое мы двигаем сами.
    private sealed class ControlledClock
    {
        private long _ms;
        public long NowMs => _ms;
        public void Advance(long ms) => _ms += ms;
        public Func<DateTime> Clock => () => new DateTime(2026, 1, 1).AddMilliseconds(_ms);
    }

    private const string Fp = "usb2:sha256:badusb";

    [Fact]
    public void Роботизированный_ввод_обнаруживается()
    {
        var clock = new ControlledClock();
        var d = new BadUsbDetector(clock.Clock);
        d.MarkAttached(Fp);

        BadUsbVerdict v = new(false, "");
        clock.Advance(100);   // подключение -> ввод через 100 мс (в окне свежести)

        // 40 нажатий строго каждые 10 мс, без джиттера
        for (var i = 0; i < 40 && !v.Suspect; i++)
        {
            clock.Advance(10);
            v = d.OnKeyStroke(Fp, 0x41, winDown: false);
        }

        Assert.True(v.Suspect, v.Reason);
        Assert.Contains("роботизированный", v.Reason);
    }

    [Fact]
    public void Человеческий_ввод_не_флагается()
    {
        var clock = new ControlledClock();
        var d = new BadUsbDetector(clock.Clock);
        d.MarkAttached(Fp);
        var rng = new Random(42);

        BadUsbVerdict v = new(false, "");
        clock.Advance(100);

        // 40 нажатий с человеческими интервалами 80..200 мс
        for (var i = 0; i < 40 && !v.Suspect; i++)
        {
            clock.Advance(80 + rng.Next(120));
            v = d.OnKeyStroke(Fp, 0x41, winDown: false);
        }

        Assert.False(v.Suspect, v.Reason);
    }

    [Fact]
    public void Win_комбинация_в_первые_секунды_флагается()
    {
        var clock = new ControlledClock();
        var d = new BadUsbDetector(clock.Clock);
        d.MarkAttached(Fp);

        clock.Advance(500);
        var v = d.OnKeyStroke(Fp, 0x5B, winDown: true);   // LWIN

        Assert.True(v.Suspect, v.Reason);
    }

    [Fact]
    public void После_окна_свежести_робот_не_флагается()
    {
        var clock = new ControlledClock();
        var d = new BadUsbDetector(clock.Clock);
        d.MarkAttached(Fp);

        clock.Advance((long)TimeSpan.FromMinutes(5).TotalMilliseconds);

        // роботизированный ввод подозрителен в любое время — ожидаем True
        BadUsbVerdict v = new(false, "");
        for (var i = 0; i < 40 && !v.Suspect; i++)
        {
            clock.Advance(10);
            v = d.OnKeyStroke(Fp, 0x41, winDown: false);
        }
        Assert.True(v.Suspect, v.Reason);
    }
}
