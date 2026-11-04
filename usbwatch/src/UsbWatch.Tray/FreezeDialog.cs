using System.Text;
using UsbWatch.Core;

namespace UsbWatch.Tray;

/// <summary>
/// Окно карантин-заморозки: устройство отключено (заморожено),
/// пользователь выбирает: Проверить / Доверить / Заблокировать навсегда.
/// </summary>
public class FreezeDialog : Form
{
    private readonly QuarantineEvent _ev;
    private readonly Action<string> _send;
    private readonly TextBox _inspect;
    private readonly Button _checkBtn;
    private bool _inspected;

    public FreezeDialog(QuarantineEvent ev, Action<string> send)
    {
        _ev = ev;
        _send = send;

        Text = "USB-вахтёр: устройство заморожено";
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Size = new Size(580, 380);

        var title = new Label
        {
            Text = "Новое USB-устройство заморожено",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Location = new Point(16, 12),
            AutoSize = true,
        };

        var info = new Label
        {
            Text = Summary(ev),
            Font = new Font("Consolas", 9),
            Location = new Point(16, 46),
            Size = new Size(540, 90),
        };

        var warn = new Label
        {
            Text = "Все его функции отключены до вашего решения.\n" +
                   "Ввод от устройства заблокирован.",
            ForeColor = Color.DarkRed,
            Location = new Point(16, 140),
            AutoSize = true,
        };

        _inspect = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9),
            Location = new Point(16, 170), Size = new Size(540, 120),
            Visible = false, BackColor = Color.FromArgb(245, 245, 245),
        };

        _checkBtn = new Button
        {
            Text = "Проверить",
            Location = new Point(16, 300),
            Size = new Size(120, 34),
        };
        _checkBtn.Click += (_, _) =>
        {
            if (!_inspected)
            {
                _inspect.Text = BuildInspector();
                _inspect.Visible = true;
                _checkBtn.Text = "Скрыть детали";
                _inspected = true;
                Height = 420;
            }
            else
            {
                _inspect.Visible = false;
                _checkBtn.Text = "Проверить";
                _inspected = false;
                Height = 380;
            }
        };

        var trustBtn = new Button
        {
            Text = "Доверить",
            Location = new Point(170, 300),
            Size = new Size(120, 34),
        };
        trustBtn.Click += (_, _) => { _send("trust " + ev.Fingerprint); Close(); };

        var blockBtn = new Button
        {
            Text = "Заблокировать навсегда",
            Location = new Point(320, 300),
            Size = new Size(190, 34),
        };
        blockBtn.Click += (_, _) =>
        {
            if (MessageBox.Show("Устройство будет заблокировано навсегда\n(запись в DeviceInstall Restrictions).",
                "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _send("block " + ev.Fingerprint);
                Close();
            }
        };

        Controls.AddRange([title, info, warn, _inspect, _checkBtn, trustBtn, blockBtn]);
    }

    private static string Summary(QuarantineEvent ev) =>
        $"  ID:     {ev.Device.DeviceInstanceId}\n" +
        $"  VID/PID: {ev.Device.Vid}:{ev.Device.Pid}  серийник: {ev.Device.Serial}\n" +
        $"  Причина: {ev.Reason}  [{ev.DecisionName}]";

    private string BuildInspector()
    {
        var d = _ev.Device;
        var sb = new StringBuilder();
        sb.AppendLine("=== ИЗОЛИРОВАННЫЙ ОСМОТР (устройство заморожено) ===");
        sb.AppendLine("Отпечаток: " + _ev.Fingerprint);
        sb.AppendLine("DeviceId:  " + d.DeviceInstanceId);
        sb.AppendLine("VID/PID:   " + d.Vid + ":" + d.Pid);
        sb.AppendLine("Серийник:  " + (string.IsNullOrEmpty(d.Serial) ? "(нет)" : d.Serial));
        sb.AppendLine("Класс:     " + d.ClassGuid);
        sb.AppendLine("Составное: " + (d.IsComposite ? "да" : "нет"));
        sb.AppendLine("Интерфейсы (" + d.InterfaceClasses.Length + "):");
        foreach (var c in d.InterfaceClasses) sb.AppendLine("  - " + c);
        sb.AppendLine("Хаб-путь:  " + string.Join(" -> ", d.HubPath));
        return sb.ToString();
    }
}
