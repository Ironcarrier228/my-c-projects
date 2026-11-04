using System.IO.Pipes;

var cmd = args.Length > 0 ? args[0] : "help";

using var pipe = new NamedPipeClientStream(".", "usbwatch$ctl",
    PipeDirection.InOut, PipeOptions.None);
try { pipe.Connect(3000); }
catch { Console.WriteLine("ERR: служба UsbWatch не запущена"); return 2; }

using var writer = new StreamWriter(pipe) { AutoFlush = true };
using var reader = new StreamReader(pipe);
await writer.WriteLineAsync(cmd + (args.Length > 1 ? " " + string.Join(' ', args.Skip(1)) : ""));
var reply = await reader.ReadLineAsync();
Console.WriteLine(reply);
return reply != null && reply.StartsWith("ok") ? 0 : 1;
