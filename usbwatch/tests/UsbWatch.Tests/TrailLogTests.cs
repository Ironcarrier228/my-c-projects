using System.Text;
using UsbWatch.Core;

namespace UsbWatch.Tests;

public class TrailLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "usbwatch-tests-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "t.jsonl");
    private string AnchorPath => Path.Combine(_dir, "a.txt");

    public TrailLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Запись/перезапись файла с FileShare.ReadWrite + retry
    /// (Defender кратко держит свежие файлы открытым).</summary>
    private static void WithFile(string path, Func<string[], string[]> mutate)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                string[] lines;
                using (var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var rd = new StreamReader(r, Encoding.UTF8))
                {
                    lines = ReadAll(rd);
                }
                lines = mutate(lines);
                using (var w = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                using (var wr = new StreamWriter(w, Encoding.UTF8))
                {
                    wr.BaseStream.SetLength(0);
                    foreach (var l in lines) wr.WriteLine(l);
                }
                return;
            }
            catch (IOException) when (attempt < 15) { Thread.Sleep(200); }
        }
        static string[] ReadAll(StreamReader r) { var l = new List<string>(); while (r.ReadLine() is { } s) l.Add(s); return l.ToArray(); }
    }

    [Fact]
    public void Записи_получают_последовательные_номера_и_цепочку()
    {
        var log = new TrailLog(LogPath, AnchorPath);
        var e1 = log.Append("svc", "arrival", "D1", "fp", "allow", "ok");
        var e2 = log.Append("svc", "deny", "D2", "fp", "deny", "bad");

        Assert.Equal(1, e1.Seq);
        Assert.Equal(2, e2.Seq);
        Assert.Equal(e1.Hash, e2.PrevHash);
    }

    [Fact]
    public void Подмена_записи_обнаруживается()
    {
        var log = new TrailLog(LogPath, AnchorPath);
        log.Append("svc", "arrival", "D1", "fp", "allow", "ok");
        log.Append("svc", "deny", "D2", "fp", "deny", "bad");

        WithFile(LogPath, lines =>
        {
            lines[1] = lines[1].Replace("\"deny\"", "\"allow\"");
            return lines;
        });

        var (ok, err) = TrailLog.Verify(LogPath, AnchorPath);
        Assert.False(ok);
        Assert.Contains("mismatch", err);
    }

    [Fact]
    public void Удаление_записи_обнаруживается()
    {
        var log = new TrailLog(LogPath, AnchorPath);
        log.Append("svc", "arrival", "D1", "fp", "allow", "a");
        log.Append("svc", "arrival", "D2", "fp", "allow", "b");
        log.Append("svc", "deny", "D3", "fp", "deny", "c");

        WithFile(LogPath, lines => lines.Where((_, i) => i != 1).ToArray());
        
        var (ok, _) = TrailLog.Verify(LogPath, AnchorPath);
        Assert.False(ok);
    }

    [Fact]
    public void Честный_журнал_проходит_проверку()
    {
        var log = new TrailLog(LogPath, AnchorPath);
        for (var i = 0; i < 10; i++)
            log.Append("svc", "arrival", $"D{i}", "fp", "allow", "ok");

        var (ok, err) = TrailLog.Verify(LogPath, AnchorPath);
        Assert.True(ok, err);
    }

    [Fact]
    public void Журнал_продолжается_после_пересоздания_объекта()
    {
        new TrailLog(LogPath, AnchorPath).Append("svc", "a", "D", "fp", "allow", "1");
        var log2 = new TrailLog(LogPath, AnchorPath);   // «перезапуск службы»
        var e = log2.Append("svc", "b", "D", "fp", "allow", "2");
        Assert.Equal(2, e.Seq);
        Assert.True(TrailLog.Verify(LogPath, AnchorPath).ok);
    }
}
