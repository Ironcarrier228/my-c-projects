using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using UsbWatch.Core;

namespace UsbWatch.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private IntPtr _notify;
    private CfgMgr32Native.CM_NOTIFY_CALLBACK? _cb;
    private PolicyEngine _engine = null!;
    private BadUsbDetector _detector = null!;
    private EnforcementCoordinator _coord = null!;
    private TrailLog _trail = null!;
    private volatile SessionPhase _phase = SessionPhase.Active;
    private UsbPolicy _policy = new();
    private readonly HashSet<string> _frozen = new();       // fp в карантине (заморожены)
    private readonly object _sync = new();

    private string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UsbWatch");
    private string PolicyPath => Path.Combine(DataDir, "policy.json");
    private string TrailPath => Path.Combine(DataDir, "trail.jsonl");
    private string AnchorPath => Path.Combine(DataDir, "anchor.txt");

    public Worker(ILogger<Worker> log) { _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        _trail = new TrailLog(TrailPath, AnchorPath);
        _policy = PolicyStore.Load(PolicyPath);
        _engine = new PolicyEngine(() => _policy, () => _phase);
        _detector = new BadUsbDetector();
        _coord = new EnforcementCoordinator(_engine, _detector, _trail);
        _coord.SetLookup(_coord.GetDevice);
        _coord.OnQuarantined += ev =>
        {
            lock (_sync) _frozen.Add(ev.Fingerprint);
            PipeHub.Broadcast("QUARANTINE " + JsonSerializer.Serialize(ev));
        };
        _coord.OnTrusted += dev =>
        {
            var fp = UsbIdentity.Fingerprint(dev);
            lock (_sync) _frozen.Remove(fp);
            if (!_policy.Rules.Any(r => r.Match == "fingerprint" && r.Value == fp))
            {
                _policy.Rules.Add(new PolicyRule
                { Kind = "allow", Match = "fingerprint", Value = fp, Note = "trusted via UI" });
                PolicyStore.Save(PolicyPath, _policy);
            }
        };

        RegisterPnp();
        _ = Task.Run(() => PipeLoop(ct), ct);

        _log.LogInformation("UsbWatch service started");
        await Task.Delay(Timeout.Infinite, ct);
    }

    private void RegisterPnp()
    {
        var guids = new[]
        {
            PolicyEngine.HidClass, PolicyEngine.KeyboardClass, PolicyEngine.MouseClass,
            "{4d36e967-e325-11ce-bfc1-08002be10318}",                 // DiskDrive
            "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}",                 // Volume
        };
        _cb ??= PnpCallback;
        foreach (var g in guids)
        {
            var f = new CfgMgr32Native.CM_NOTIFY_FILTER
            {
                cbSize = Marshal.SizeOf<CfgMgr32Native.CM_NOTIFY_FILTER>() + 16,
                FilterType = CfgMgr32Native.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                ClassGuid = new Guid(g)
            };
            if (CfgMgr32Native.CM_Register_Notification(ref f, IntPtr.Zero, _cb, out _notify)
                != CfgMgr32Native.CR_SUCCESS)
                _log.LogError("CM_Register_Notification failed: {g}", g);
        }
        foreach (var id in CfgMgr32Native.GetDeviceIdList("USB\\*"))
            ThreadPool.QueueUserWorkItem(_ => HandleUsbNode(id));
    }

    private int PnpCallback(IntPtr h, IntPtr ctx, int action, IntPtr data, int size)
    {
        try
        {
            if (action != CfgMgr32Native.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) return 0;
            var symlink = Marshal.PtrToStringUni(data + 8 + 16) ?? "";
            ThreadPool.QueueUserWorkItem(_ => HandleInterfaceArrival(symlink));
        }
        catch (Exception ex) { _log.LogError(ex, "PnP callback"); }
        return 0;
    }

    private void HandleInterfaceArrival(string symlink)
    {
        var guidPart = symlink.Split('#').LastOrDefault() ?? "";
        var g = new Guid(guidPart);
        int len = 0;
        CfgMgr32Native.CM_Get_Device_Interface_ListW(ref g, symlink, null, ref len, 0);
        var buf = new char[len];
        CfgMgr32Native.CM_Get_Device_Interface_ListW(ref g, symlink, buf, ref len, 0);
        var devPath = new string(buf).TrimEnd('\0');
        if (CfgMgr32Native.CM_Locate_DevNodeW(out var node, devPath, 0) != CfgMgr32Native.CR_SUCCESS) return;

        var dev = UsbIdentity.BuildFromInterfaceNode(node);
        if (dev == null) return;
        var outcome = _coord.HandleArrival(dev);
        _log.LogInformation("{id}: {dec} ({reason})", dev.DeviceInstanceId, outcome.Decision, outcome.Reason);
    }

    private void HandleUsbNode(string usbId)
    {
        if (CfgMgr32Native.CM_Locate_DevNodeW(out var node, usbId, 0) != CfgMgr32Native.CR_SUCCESS) return;
        var dev = UsbIdentity.BuildFromInterfaceNode(node);
        if (dev == null) return;
        var outcome = _coord.HandleArrival(dev);
        _log.LogInformation("{id}: {dec}", dev.DeviceInstanceId, outcome.Decision);
    }

    // --- Named pipe с ACL ---
    private async Task PipeLoop(CancellationToken ct)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.Read | PipeAccessRights.Write, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            var server = NamedPipeServerStreamAcl.Create(
                "usbwatch$ctl", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, ps);
            await server.WaitForConnectionAsync(ct);
            _ = Task.Run(async () =>
            {
                using (server)
                {
                    var writer = new StreamWriter(server) { AutoFlush = true };
                    PipeHub.Register(writer);
                    try
                    {
                        using var reader = new StreamReader(server);
                        while (!ct.IsCancellationRequested && server.IsConnected)
                        {
                            var line = await reader.ReadLineAsync(ct);
                            if (line == null) break;
                            await writer.WriteLineAsync(HandleCommand(line));
                        }
                    }
                    catch { /* клиент отвалился */ }
                    finally { PipeHub.Unregister(writer); }
                }
            }, ct);
        }
    }

    private string HandleCommand(string line)
    {
        var p = line.Split(' ', 3);
        try
        {
            switch (p[0])
            {
            case "ping": return "pong";

            case "trust" when p.Length > 1:
                return _coord.Trust(p[1]) ? "ok trusted" : "ERR unknown fingerprint";

            case "block" when p.Length > 1:
                return _coord.BlockForever(p[1]) ? "ok blocked forever" : "ERR unknown fingerprint";

            case "allow-once" when p.Length > 1:
            {
                var fp = p[1];
                if (_coord.GetDevice(fp) == null) return "ERR unknown fingerprint";
                var ttl = p.Length > 2 && int.TryParse(p[2], out var m) ? m : 10;
                lock (_sync) _frozen.Remove(fp);
                _policy.Temporary.RemoveAll(t => t.Fingerprint == fp);
                _policy.Temporary.Add(new TempGrant
                { Fingerprint = fp, GrantedAtUtc = DateTime.UtcNow, TtlMinutes = ttl });
                PolicyStore.Save(PolicyPath, _policy);
                _coord.UnfreezeByFingerprint(fp);
                return $"ok allowed for {ttl}m";
            }

            case "inspect" when p.Length > 1:
            {
                var dev = _coord.GetDevice(p[1]);
                return dev == null ? "ERR unknown fingerprint"
                    : JsonSerializer.Serialize(new
                    {
                        dev.DeviceInstanceId, dev.Vid, dev.Pid, dev.Serial,
                        dev.ClassGuid, dev.InterfaceClasses, dev.HubPath,
                        dev.IsComposite, fingerprint = p[1],
                    });
            }

            case "frozen-list":
                lock (_sync) return JsonSerializer.Serialize(_frozen);

            case "policy.export" when p.Length > 1:
                PolicyStore.Save(p[1], _policy);
                return "ok exported";

            case "policy.import" when p.Length > 2:
                _policy = PolicyStore.Load(p[2]);
                PolicyStore.Save(PolicyPath, _policy);
                return "ok imported";

            case "log.verify":
            {
                var (ok, err) = TrailLog.Verify(TrailPath, AnchorPath);
                return ok ? "ok: " + err : "TAMPERED: " + err;
            }

            default: return "ERR unknown command";
            }
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }
}
