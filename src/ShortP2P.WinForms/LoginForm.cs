using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShortP2P.Auth;
using ShortP2P.Client.ProfileBackup;
using ShortP2P.Client.Services;

namespace ShortP2P.WinForms;

public sealed class LoginForm : Form
{
    private readonly AuthService _auth;
    private readonly Button _btnCancel = new() { Text = "Exit", DialogResult = DialogResult.Cancel };
    private readonly Button _btnLogin = new() { Text = "Login", DialogResult = DialogResult.None };
    private readonly Button _btnRegister = new() { Text = "Register" };
    private readonly Button _btnImport = new() { Text = "Импорт профиля…" };
    private readonly ILogger<LoginForm> _logger;
    private readonly TextBox _nick = new() { PlaceholderText = "Nickname" };
    private readonly TextBox _pass = new() { PlaceholderText = "Password", UseSystemPasswordChar = true };
    private readonly IServiceProvider _services;
    private readonly ILogger<UserAction> _userLogs;

    public LoginForm(AuthService auth, IServiceProvider services, ILogger<LoginForm> logger,
        ILogger<UserAction> userActions)
    {
        _auth = auth;
        _services = services;
        _logger = logger;
        _userLogs = userActions;
        Text = "ShortP2P — Login";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AcceptButton = _btnLogin;
        CancelButton = _btnCancel;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_nick, 0, 0);
        layout.Controls.Add(_pass, 0, 1);
        var buttons = new FlowLayoutPanel
            { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(_btnLogin);
        buttons.Controls.Add(_btnRegister);
        buttons.Controls.Add(_btnImport);
        buttons.Controls.Add(_btnCancel);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        _nick.Width = 320;
        _pass.Width = 320;

        var preferred = new Size(
            Math.Max(layout.PreferredSize.Width, _nick.Width) + Padding.Horizontal,
            layout.PreferredSize.Height + Padding.Vertical);
        AutoSize = false;
        ClientSize = new Size(
            (int)Math.Round(preferred.Width * 1.25),
            (int)Math.Round(preferred.Height * 1.25));
        _nick.Dock = DockStyle.Fill;
        _pass.Dock = DockStyle.Fill;

        _btnLogin.Click += async (_, _) => await OnLoginAsync().ConfigureAwait(true);
        _btnRegister.Click += OnRegisterClicked;
        _btnImport.Click += async (_, _) => await OnImportAsync().ConfigureAwait(true);
        Load += async (_, _) => await TryRestoreAsync().ConfigureAwait(true);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && DialogResult == DialogResult.Cancel)
                _userLogs.LogInformation("Login: exit");
        };
    }

    private async Task TryRestoreAsync()
    {
        if (await _auth.TryRestoreSessionAsync().ConfigureAwait(true) && _auth.CurrentUser != null)
        {
            await ApplyIdentityMigrationIfNeededAsync().ConfigureAwait(true);
            _userLogs.LogInformation("Login: session restored for {Nickname}", _auth.CurrentUser.Nickname);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private async Task OnLoginAsync()
    {
        var nick = _nick.Text.Trim();
        var pass = _pass.Text ?? "";
        var (ok, err) = await _auth.LoginAsync(nick, pass).ConfigureAwait(true);
        if (!ok)
        {
            _userLogs.LogInformation("Login: failed for nickname {Nickname}", nick);
            _logger.LogWarning("Login failed for {Nickname}: {Reason}", nick, err);
            MessageBox.Show(this, err ?? "Login failed.", "Login", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await ApplyIdentityMigrationIfNeededAsync().ConfigureAwait(true);
        _userLogs.LogInformation("Login: success for nickname {Nickname}", nick);
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task ApplyIdentityMigrationIfNeededAsync()
    {
        try
        {
            var chats = _services.GetRequiredService<ChatRepository>();
            var migration = await IdentityMigrationApplier.ApplyPendingAsync(_auth, chats, _logger)
                .ConfigureAwait(true);
            if (migration != null)
                MessageBox.Show(this, migration, "Identity upgraded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity migration after login");
        }
    }

    private void OnRegisterClicked(object? sender, EventArgs e)
    {
        _userLogs.LogInformation("Login: open register");
        using var reg = _services.GetRequiredService<RegisterForm>();
        if (reg.ShowDialog(this) != DialogResult.OK)
            return;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task OnImportAsync()
    {
        _userLogs.LogInformation("Login: import profile from .tlp");
        var backup = _services.GetRequiredService<ProfileBackupService>();
        if (!await ProfileFileShare.ImportProfileAsync(this, _auth, backup, _logger).ConfigureAwait(true))
            return;

        // DialogResult.OK lets Program.cs open MainChatsForm, which starts UserP2pRuntime
        // (LAN + MessengerServerSyncService) for the restored user — same as a password login.
        DialogResult = DialogResult.OK;
        Close();
    }
}
