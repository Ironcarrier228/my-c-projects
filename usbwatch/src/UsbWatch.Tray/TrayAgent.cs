using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UsbWatch.Core;

namespace UsbWatch.Tray;

internal static class TrayAgent
{
    private static NotifyIcon _icon = null!;
    private static NamedPipeClientStream? _pipe;
    private static StreamWriter? _pipeWriter;
    private static readonly BadUsbDetector _detector = new();
    private static readonly HashSet<string> _frozenFps = new();
    private static Form? _syncForm;
    private static IntPtr _hook = IntPtr.Zero;
    private static string? _lastAttachedFp;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        _syncForm = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
        _ = _syncForm.Handle;   // force handle

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Visible = true,
            Text = "USB-вахтёр",
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Карантин", null, (_, _) => ShowFrozenList());
        menu.Items.Add("Выход", null, (_, _) => Application.Exit());
        _icon.ContextMenuStrip = menu;

        ConnectPipe();
        _ = Task.Run(PipeReaderLoop);
        StartKeyboardHook();

        Application.Run();
    }

    private static void ConnectPipe()
    {
        while (true)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", "usbwatch$ctl", PipeDirection.InOut);
                _pipe.Connect(5000);
                _pipeWriter = new StreamWriter(_pipe) { AutoFlush = true };
                Send("ping");
                return;
            }
            catch { Thread.Sleep(3000); }
        }
    }

    private static void Send(string msg)
    {
        try
        {
            if (_pipeWriter == null || !_pipe!.IsConnected) { ConnectPipe(); }
            _pipeWriter?.Write(msg + "\n");
            _pipeWriter?.Flush();
        }
        catch { }
    }

    private static async Task PipeReaderLoop()
    {
        while (true)
        {
            try
            {
                using var reader = new StreamReader(_pipe!);
                while (_pipe!.IsConnected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    HandleServiceEvent(line);
                }
            }
            catch { }
            Thread.Sleep(3000);
            ConnectPipe();
        }
    }

    private static void HandleServiceEvent(string line)
    {
        if (line.StartsWith("ATTACHED "))
        {
            _lastAttachedFp = line["ATTACHED ".Length..];
            if (_hook == IntPtr.Zero) StartKeyboardHook();
        }
        else if (line.StartsWith("QUARANTINE "))
        {
            var json = line["QUARANTINE ".Length..];
            var ev = JsonSerializer.Deserialize<QuarantineEvent>(json);
            if (ev == null) return;
            _frozenFps.Add(ev.Fingerprint);
            ShowOnUi(() => new FreezeDialog(ev, Send).Show());
        }
        else if (line.StartsWith("BADUSB "))
        {
            var reason = line["BADUSB ".Length..];
            ShowOnUi(() =>
            {
                _icon.BalloonTipTitle = "USB-вахтёр: BadUSB";
                _icon.BalloonTipText = reason;
                _icon.ShowBalloonTip(4000);
            });
        }
    }

    private static void ShowOnUi(Action a)
    {
        try { _syncForm!.BeginInvoke(a); }
        catch { a(); }
    }

    private static void ShowFrozenList()
    {
        var items = _frozenFps.Count == 0 ? "карантин пуст" : string.Join("\n", _frozenFps);
        MessageBox.Show(items, "USB-вахтёр: карантин", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // --- BadUSB: LL-хук клавиатуры ---

    private static void StartKeyboardHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, HookProc, GetModuleHandleW(null), 0);
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt64() is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var fp = _lastAttachedFp ?? "";
            var v = _detector.OnKeyStroke(fp, (uint)kb.vkCode, IsWinDown());
            if (v.Suspect) Send("BADUSB " + v.Reason);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsWinDown() =>
        (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc cb, IntPtr hMod, uint tid);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}
