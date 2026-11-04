using System.IO.Pipes;

namespace UsbWatch.Service;

/// <summary>Хаб подключённых pipe-клиентов: broadcast событий + маршрутизация команд.</summary>
public static class PipeHub
{
    private static readonly List<StreamWriter> _clients = new();
    private static readonly object _sync = new();

    public static void Register(StreamWriter w)
    {
        lock (_sync) _clients.Add(w);
    }

    public static void Unregister(StreamWriter w)
    {
        lock (_sync) _clients.Remove(w);
    }

    public static void Broadcast(string line)
    {
        lock (_sync)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                try { _clients[i].WriteLine(line); _clients[i].Flush(); }
                catch { _clients.RemoveAt(i); }
            }
        }
    }
}
