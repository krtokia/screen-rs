using System.Drawing;
using System.Net.Sockets;
using System.Windows.Forms;

namespace Monitor.Sender;

/// <summary>
/// The sender's only configuration UI. Shown at install time and from the tray menu — both are
/// human actions. It must never appear on the unattended auto-start path (§9): a dialog nobody
/// can click would block the reboot recovery forever.
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = 65535 };
    private readonly TextBox _deviceId = new() { Dock = DockStyle.Fill, MaxLength = 16 };
    private readonly Label _testResult = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray };
    private readonly Button _test = new() { Text = "연결 테스트", AutoSize = true };

    public SenderSettings Value { get; private set; }

    public SettingsDialog(SenderSettings current)
    {
        Value = current;

        Text = "Screen Sender 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 190);

        _host.Text = current.ReceiverHost;
        _port.Value = current.ReceiverPort;
        _deviceId.Text = current.DeviceId;

        var save = new Button { Text = "저장", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += OnSave;
        _test.Click += OnTest;
        AcceptButton = save;
        CancelButton = cancel;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 5,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        grid.Controls.Add(MakeLabel("수신측 IP"), 0, 0);
        grid.Controls.Add(_host, 1, 0);
        grid.Controls.Add(MakeLabel("포트"), 0, 1);
        grid.Controls.Add(_port, 1, 1);
        grid.Controls.Add(MakeLabel("장비 ID"), 0, 2);
        grid.Controls.Add(_deviceId, 1, 2);
        grid.Controls.Add(_test, 0, 3);
        grid.Controls.Add(_testResult, 1, 3);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        grid.Controls.Add(buttons, 1, 4);

        Controls.Add(grid);
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private bool TryRead(out SenderSettings settings, out string error)
    {
        settings = new SenderSettings(_host.Text.Trim(), (int)_port.Value, _deviceId.Text.Trim());

        if (settings.ReceiverHost.Length == 0)
        {
            error = "수신측 IP를 입력하세요.";
            return false;
        }
        if (!SenderSettings.IsValidDeviceId(settings.DeviceId))
        {
            error = "장비 ID는 1~16바이트(UTF-8)여야 합니다.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Proves the receiver's port is reachable before the operator leaves the site — a mistyped IP
    /// is otherwise only discovered back at the receiver, and fixing it means another visit.
    /// </summary>
    private void OnTest(object? sender, EventArgs e)
    {
        if (!TryRead(out var s, out var error))
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = error;
            return;
        }

        _test.Enabled = false;
        _testResult.ForeColor = Color.Gray;
        _testResult.Text = "연결 중...";
        _testResult.Refresh();

        try
        {
            using var client = new TcpClient();
            if (client.ConnectAsync(s.ReceiverHost, s.ReceiverPort).Wait(TimeSpan.FromSeconds(3)))
            {
                _testResult.ForeColor = Color.Green;
                _testResult.Text = "연결 성공";
            }
            else
            {
                _testResult.ForeColor = Color.Firebrick;
                _testResult.Text = "응답 없음 (3초). IP와 수신측 실행 여부를 확인하세요.";
            }
        }
        catch (Exception ex)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = $"실패: {(ex.InnerException ?? ex).Message}";
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!TryRead(out var s, out var error))
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = error;
            return;
        }

        Value = s;
        DialogResult = DialogResult.OK;
    }
}
