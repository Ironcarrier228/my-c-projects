using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsbWatch.Core;

public record TrailEntry(
    long Seq, string Utc, string Actor, string Event, string DeviceInstanceId,
    string Fingerprint, string Decision, string Details, string PrevHash, string Hash);

public sealed class TrailLog
{
    private readonly string _path;
    private readonly string _anchorPath;
    private long _seq;
    private string _prevHash = "GENESIS";
    private readonly object _sync = new();

    public TrailLog(string path, string anchorPath)
    {
        _path = path;
        _anchorPath = anchorPath;
        Init();
    }

    private static void AppendLine(string path, string line)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var w = new StreamWriter(fs, Encoding.UTF8);
        w.WriteLine(line);
    }

    private static void WriteAll(string path, string content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var w = new StreamWriter(fs, Encoding.UTF8);
        w.Write(content);
    }

    private static string[] ReadAllLinesSafe(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                  using var r = new StreamReader(fs, Encoding.UTF8); return ReadAll(r); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(50); }
        }
        static string[] ReadAll(StreamReader r) { var l = new List<string>(); while (r.ReadLine() is { } s) l.Add(s); return l.ToArray(); }
    }

    private static string? ReadAllSafe(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                  using var r = new StreamReader(fs, Encoding.UTF8); return r.ReadToEnd(); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(50); }
        }
    }

    private void Init()
    {
        if (File.Exists(_path))
        {
            var last = ReadAllLinesSafe(_path).LastOrDefault();
            if (last != null)
            {
                var e = JsonSerializer.Deserialize<TrailEntry>(last)!;
                _seq = e.Seq;
                _prevHash = e.Hash;
            }
        }
        if (File.Exists(_anchorPath))
        {
            var anchored = File.ReadAllText(_anchorPath).Trim();
            if (anchored.Length > 0 && _prevHash != anchored && _seq == 0)
                throw new InvalidOperationException("Trail tampering detected (anchor mismatch)");
        }
    }

    public TrailEntry Append(string actor, string @event, string deviceId, string fp, string decision, string details)
    {
        lock (_sync)
        {
            var utc = DateTime.UtcNow.ToString("O");
            var payload = $"{actor}|{@event}|{deviceId}|{fp}|{decision}|{details}|{utc}";
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(_prevHash + "|" + (_seq + 1) + "|" + payload))).ToLowerInvariant();
            var entry = new TrailEntry(_seq + 1, utc, actor, @event, deviceId, fp, decision, details, _prevHash, hash);
            AppendLine(_path, JsonSerializer.Serialize(entry));
            WriteAll(_anchorPath, hash);
            _seq++;
            _prevHash = hash;
            return entry;
        }
    }

    public static (bool ok, string error) Verify(string path, string anchorPath)
    {
        var prev = "GENESIS";
        long expectSeq = 0;
        foreach (var line in ReadAllLinesSafe(path))
        {
            var e = JsonSerializer.Deserialize<TrailEntry>(line)!;
            if (e.Seq != ++expectSeq) return (false, $"seq gap at {e.Seq}");
            var payload = $"{e.Actor}|{e.Event}|{e.DeviceInstanceId}|{e.Fingerprint}|{e.Decision}|{e.Details}|{e.Utc}";
            var h = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(prev + "|" + e.Seq + "|" + payload))).ToLowerInvariant();
            if (h != e.Hash) return (false, $"hash mismatch at seq {e.Seq}");
            if (e.PrevHash != prev) return (false, $"prev-hash mismatch at seq {e.Seq}");
            prev = e.Hash;
        }
        var anchor = (ReadAllSafe(anchorPath) ?? "").Trim();
        if (prev != anchor) return (false, "anchor mismatch");
        return (true, "ok");
    }
}
