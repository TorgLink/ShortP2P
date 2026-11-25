using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client.Qr;
using ShortP2P.Crypto;

namespace ShortP2P.WinForms;

public sealed class MyQrForm : Form
{
    private readonly AuthService _auth;
    private readonly ILogger<MyQrForm> _log;
    private readonly ILogger<UserAction> _userActions;
    private readonly PictureBox? _picture;
    private readonly Label? _status;
    private string _qrPayloadJson = string.Empty;
    private byte[]? _qrPng;
    private bool _loadStarted;

    public MyQrForm(AuthService auth, ILogger<MyQrForm> log, ILogger<UserAction> userActions)
    {
        _auth = auth;
        _log = log;
        _userActions = userActions;

        Text = "My QR code";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 400;
        Height = 460;
        Padding = new Padding(12);

        var u = auth.CurrentUser;
        if (u == null)
        {
            log.LogWarning("My QR opened but user is not logged in");
            Controls.Add(new Label { Text = "Not logged in.", AutoSize = true, Dock = DockStyle.Fill });
            return;
        }

        var hint = new Label
        {
            Text =
                "Show this code to a peer so they can add you. All detected IPv4 addresses on this PC are included (best first); the peer can edit the list after scanning if needed.",
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Dock = DockStyle.Top
        };

        _status = new Label
        {
            Text = "Loading…",
            AutoSize = true,
            Dock = DockStyle.Top
        };

        _picture = new PictureBox
        {
            Size = new Size(280, 280),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle
        };
        var btnShare = new Button { Text = "Поделиться", AutoSize = true };
        btnShare.Click += (_, _) => OnShareClicked();

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        panel.Controls.Add(hint);
        panel.Controls.Add(_status);
        panel.Controls.Add(_picture);
        panel.Controls.Add(btnShare);
        Controls.Add(panel);

        Shown += (_, _) => _ = LoadQrAsync(u);
    }

    private async Task LoadQrAsync(UserEntity u)
    {
        if (_loadStarted)
            return;
        _loadStarted = true;

        try
        {
            var pub = IdentityKeySerializer.SerializePublic(_auth.GetCurrentPublicKey());
            // InviteHostsBuilder → GetAllUnicastIpv6Ordered / public IP — blocking; off UI thread.
            var (payloadJson, png) = await Task.Run(() =>
            {
                var payload = PeerQrService.BuildPayload(u, pub);
                return (PeerQrCodec.Serialize(payload), PeerQrService.EncodeQrPng(payload));
            }).ConfigureAwait(true);

            if (_picture == null || _status == null)
                return;

            _qrPayloadJson = payloadJson;
            _qrPng = png;
            _picture.Image?.Dispose();
            _picture.Image = new Bitmap(new MemoryStream(png));
            _status.Text = string.Empty;

            _userActions.LogInformation("My QR: opened (user {Nickname}, network id {NetworkId})",
                u.Nickname, u.NetworkIdShort);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "My QR load failed");
            if (_status != null)
                _status.Text = ex.Message;
        }
    }

    private void OnShareClicked()
    {
        if (_qrPng == null || _qrPng.Length == 0)
        {
            MessageBox.Show(this, "QR-код пока не готов.", "QR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("В мессенджеры", null, (_, _) => ShareToMessengers());
        menu.Items.Add("Почта", null, (_, _) => ShareToEmail());
        menu.Items.Add("Bluetooth", null, (_, _) => ShareToBluetooth());
        menu.Show(Cursor.Position);
    }

    private string EnsureQrTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shortp2p-my-qr-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
        File.WriteAllBytes(path, _qrPng!);
        return path;
    }

    private void ShareToMessengers()
    {
        try
        {
            var path = EnsureQrTempFile();
            Clipboard.SetText(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            MessageBox.Show(this,
                "PNG QR-кода сохранен во временный файл, путь скопирован в буфер обмена. Прикрепите файл в нужном мессенджере.",
                "Поделиться", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось подготовить QR для мессенджера: {ex.Message}", "Поделиться",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShareToEmail()
    {
        try
        {
            var subject = Uri.EscapeDataString("ShortP2P: мой QR-код");
            var body = Uri.EscapeDataString(
                "Во вложение добавьте PNG QR-кода из проводника.\r\n\r\nТакже можно импортировать по JSON:\r\n" +
                _qrPayloadJson);
            Process.Start(new ProcessStartInfo($"mailto:?subject={subject}&body={body}") { UseShellExecute = true });
            ShareToMessengers();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось открыть почту: {ex.Message}", "Поделиться",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShareToBluetooth()
    {
        try
        {
            _ = EnsureQrTempFile();
            Process.Start(new ProcessStartInfo("fsquirt.exe") { UseShellExecute = true });
            MessageBox.Show(this,
                "Открыт мастер передачи Bluetooth. Выберите 'Send files' и укажите сохраненный PNG QR-кода.",
                "Bluetooth", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось открыть передачу Bluetooth: {ex.Message}", "Bluetooth",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _picture?.Image?.Dispose();
        base.Dispose(disposing);
    }
}
