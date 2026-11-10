using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.Vorbis;
using NAudio.Wave;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.Data;
using ShortP2P.Client.Services;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;
using ShortP2P.Discovery;
using ShortP2P.Transport.Bluetooth.Windows;
using Timer = System.Windows.Forms.Timer;

namespace ShortP2P.WinForms;

public sealed class ChatForm : Form
{
    private const string FileCaptionNewline = "\r\n";
    private const string FileDownloadHintPrefix = "Двойной щелчок — ";
    private const string FileDownloadHintAction = "Скачать";
    private const int MessagesPageSize = 15;

    private const int SB_VERT = 1;
    private const uint SIF_ALL = 0x17;
    private static readonly Color FileDownloadActionColor = Color.FromArgb(0, 102, 204);
    private readonly AppSettingsStore _appSettings;

    private readonly Button _attachCamera = new()
    {
        Text = "📹",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI Emoji", 12, FontStyle.Regular, GraphicsUnit.Point)
    };

    private readonly Button _attachDocument = new()
    {
        Text = "📄",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI Emoji", 12, FontStyle.Regular, GraphicsUnit.Point)
    };

    private readonly Button _attachImage = new()
    {
        Text = "🖼",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI Emoji", 12, FontStyle.Regular, GraphicsUnit.Point)
    };

    private readonly Button _attachVideo = new()
    {
        Text = "🎬",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI Emoji", 12, FontStyle.Regular, GraphicsUnit.Point)
    };

    private readonly Button _attachVoice = new()
    {
        Text = "🎤",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI Emoji", 12, FontStyle.Regular, GraphicsUnit.Point)
    };

    private readonly AuthService _auth;
    private readonly ToolTip _buttonTooltips = new() { ShowAlways = true };

    private readonly ChatEntity _chat;
    private readonly Button _clearChat = new() { Text = "Удалить", AutoSize = true };

    private readonly Label _deliveryIssueLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        Visible = false,
        ForeColor = Color.DarkRed,
        Padding = new Padding(8, 2, 8, 4)
    };

    private readonly RichTextBox _input = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsTab = false,
        BorderStyle = BorderStyle.FixedSingle,
        MaxLength = ChatP2PSession.MaxMessageChars
    };

    private readonly List<ChatMessageEntity> _loadedRows = [];
    private readonly ILogger<ChatForm> _logger;
    private readonly ChatMediaOptions _media;

    private readonly ListBox _messages = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        ScrollAlwaysVisible = true,
        Padding = new Padding(8, 5, 8, 4),
        DrawMode = DrawMode.OwnerDrawVariable
    };

    private readonly UserP2pRuntime _p2PRuntime;
    private readonly MessengerServerManager _messengerServers;

    private readonly Label _peerInfoLabel = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(0, 0, 0, 2)
    };

    private readonly Label _safetyNumberLabel = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI Emoji", 12f),
        TextAlign = ContentAlignment.TopRight,
        Padding = new Padding(4, 0, 4, 0)
    };

    private readonly Button _emergencyUntrust = new()
    {
        Text = "🚨",
        AutoSize = true,
        Font = new Font("Segoe UI Emoji", 12f),
        FlatStyle = FlatStyle.Flat
    };

    private readonly Button _deliveryPath = new()
    {
        Text = "Путь: авто",
        AutoSize = true
    };

    private readonly ChatRepository _repo;

    private readonly Button _send = new()
    {
        Text = "➤",
        Dock = DockStyle.Right,
        Width = 36,
        Height = 36,
        Font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Point)
    };

    private readonly GroupBox _techGroup = new()
    {
        Text = "TECH",
        Dock = DockStyle.Bottom,
        Height = 52,
        Padding = new Padding(8, 4, 8, 4),
        TabStop = false
    };

    private readonly Button _techHandshake = new() { Text = "Send handshake", AutoSize = true };
    private readonly Button _techInvite = new() { Text = "Send Invite", AutoSize = true };
    private readonly Button _techPing = new() { Text = "Send ping", AutoSize = true };
    private readonly UserEntity _user;
    private readonly ILogger<UserAction> _userActions;

    private readonly object _voiceCapLock = new();
    private bool _hasMoreRows = true;
    private bool _isLoadingRows;
    private ChatP2PSession? _p2PSession;
    private bool _pairingPromptShown;
    private volatile bool _voiceDiscardNextStop;
    private MemoryStream? _voicePlaybackMem;

    private IWavePlayer? _voicePlaybackOut;
    private RawSourceWaveStream? _voicePlaybackRaw;
    private VorbisWaveReader? _voicePlaybackReader;
    private DateTime _voiceRecordStartUtc;
    private Timer? _voiceRecordTimer;
    private WaveInEvent? _voiceWaveIn;
    private MemoryStream? _voiceWaveMs;
    private WaveFileWriter? _voiceWaveWriter;

    public ChatForm(ChatEntity chat, UserEntity user, AuthService auth, ChatRepository repo, UserP2pRuntime p2PRuntime,
        ILogger<ChatForm> logger, ILogger<UserAction> userActions, ChatMediaOptions media, AppSettingsStore appSettings,
        MessengerServerManager messengerServers)
    {
        _chat = chat;
        _user = user;
        _auth = auth;
        _repo = repo;
        _p2PRuntime = p2PRuntime;
        _media = media;
        _appSettings = appSettings;
        _logger = logger;
        _userActions = userActions;
        _messengerServers = messengerServers;
        Text = chat.PeerNickname;
        StartPosition = FormStartPosition.CenterParent;
        Width = 700;
        Height = 715;
        MaximizeBox = false;
        _emergencyUntrust.FlatAppearance.BorderSize = 0;
        _buttonTooltips.SetToolTip(_emergencyUntrust,
            "Срочно пометить текущий messenger-сервер как недоверенный и переключиться.");
        _buttonTooltips.SetToolTip(_deliveryPath,
            "Переключить доставку этого чата: сервер или mesh (UDP/BLE). Ручной выбор держится, пока не переключите обратно.");

        _peerInfoLabel.Text = PeerInfoText("Статус: офлайн");
        RefreshSafetyLabel();

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 2),
            ColumnCount = 5,
            RowCount = 1
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_peerInfoLabel, 0, 0);
        top.Controls.Add(_safetyNumberLabel, 1, 0);
        top.Controls.Add(_deliveryPath, 2, 0);
        top.Controls.Add(_emergencyUntrust, 3, 0);
        top.Controls.Add(_clearChat, 4, 0);
        _deliveryPath.Click += (_, _) => ShowDeliveryPathMenu();
        _emergencyUntrust.Click += async (_, _) => await OnEmergencyUntrustAsync().ConfigureAwait(true);
        _repo.PeerPublicKeyChanged += OnPeerPublicKeyChanged;
        _messengerServers.FailoverCompleted += OnMessengerServerFailover;

        var deliveryIssuePanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 26
        };
        deliveryIssuePanel.Controls.Add(_deliveryIssueLabel);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 76, ColumnCount = 7 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_input, 0, 0);
        bottom.Controls.Add(_attachVoice, 1, 0);
        bottom.Controls.Add(_attachImage, 2, 0);
        bottom.Controls.Add(_attachVideo, 3, 0);
        bottom.Controls.Add(_attachCamera, 4, 0);
        bottom.Controls.Add(_attachDocument, 5, 0);
        bottom.Controls.Add(_send, 6, 0);

        RefreshVoiceRecordTooltip();
        _buttonTooltips.SetToolTip(_attachImage, "Отправить изображение");
        _buttonTooltips.SetToolTip(_attachVideo,
            $"Отправить видео OGV (обычный: 320x240, экономия: 160x120, до {VideoAttachHelper.MaxDurationSeconds} сек)");
        _buttonTooltips.SetToolTip(_attachCamera, "Записать видеосообщение с камеры");
        _buttonTooltips.SetToolTip(_attachDocument, "Отправить документ");
        _buttonTooltips.SetToolTip(_send, "Отправить сообщение");
        _buttonTooltips.SetToolTip(_clearChat,
            "Удалить все сообщения с этого ПК и отменить недоставленные отправки.");
        _buttonTooltips.SetToolTip(_techHandshake, "Временно: сброс крипто-сессии и повторный RSA handshake");
        _buttonTooltips.SetToolTip(_techInvite, "Временно: chat invite (frame 0x30) на адреса пира");
        _buttonTooltips.SetToolTip(_techPing, "Временно: presence ping на порт discovery (17501)");

        var techFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 2, 0, 0)
        };
        techFlow.Controls.Add(_techHandshake);
        techFlow.Controls.Add(_techInvite);
        techFlow.Controls.Add(_techPing);
        _techGroup.Controls.Add(techFlow);

        // Порядок: Fill сначала, затем Top/Bottom — иначе между шапкой и вводом остаётся пустая полоса.
        // Последний Dock Bottom примыкает к низу окна (панель ввода); TECH добавляется раньше — выше неё.
        Controls.Add(_messages);
        Controls.Add(top);
        Controls.Add(_techGroup);
        Controls.Add(deliveryIssuePanel);
        Controls.Add(bottom);

        _clearChat.Click += async (_, _) => await OnClearChatAsync().ConfigureAwait(true);
        _attachVoice.Click += (_, _) => OnAttachVoice();
        _attachImage.Click += async (_, _) => await OnAttachImageAsync().ConfigureAwait(true);
        _attachVideo.Click += async (_, _) => await OnAttachVideoAsync().ConfigureAwait(true);
        _attachCamera.Click += async (_, _) => await OnAttachCameraAsync().ConfigureAwait(true);
        _attachDocument.Click += async (_, _) => await OnAttachDocumentAsync().ConfigureAwait(true);
        _send.Click += async (_, _) => await OnSendAsync().ConfigureAwait(true);
        _input.KeyDown += OnInputKeyDown;
        _techHandshake.Click += async (_, _) => await OnTechHandshakeAsync().ConfigureAwait(true);
        _techInvite.Click += async (_, _) => await OnTechInviteAsync().ConfigureAwait(true);
        _techPing.Click += async (_, _) => await OnTechPingAsync().ConfigureAwait(true);
        _messages.DrawItem += OnMessagesDrawItem;
        _messages.MeasureItem += OnMessagesMeasureItem;
        _messages.MouseWheel += OnMessagesMouseWheel;
        _messages.KeyUp += OnMessagesKeyUp;
        _messages.MouseClick += OnMessagesMouseClick;
        _messages.DoubleClick += OnMessageDoubleClick;
        Shown += async (_, _) => await OnShownAsync().ConfigureAwait(true);
    }

    private async Task OnShownAsync()
    {
        _userActions.LogInformation("Chat {Peer}: window opened (chat id {ChatId})", _chat.PeerNickname, _chat.Id);
        var uiSync = SynchronizationContext.Current;
        var fresh = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(true) ?? _chat;
        _p2PSession = _p2PRuntime.GetSession(fresh, _user, _auth, _repo, uiSync);
        _p2PSession.MessagesChanged += OnP2pMessagesChanged;
        _p2PSession.DeliveryPathChanged += OnDeliveryPathChanged;
        _p2PSession.ActiveChannelChanged += OnActiveChannelChanged;
        if (!_p2PRuntime.IsChatSessionStarted(_chat.Id))
            try
            {
                await _p2PSession.StartAsync().ConfigureAwait(true);
                _p2PRuntime.MarkChatSessionStarted(_chat.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start UDP for chat {ChatId}", _chat.Id);
                if (HandleBluetoothUnavailable(ex))
                    return;
                MessageBox.Show(this, $"Could not start UDP: {ex.Message}", "P2P", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

        RefreshDeliveryPathButton();
        RefreshSafetyLabel();
        await ReloadMessagesAsync().ConfigureAwait(true);
        RefreshPeerPresenceLabel();
    }

    private void RefreshDeliveryPathButton()
    {
        _deliveryPath.Text = _p2PSession == null
            ? ChatP2PSession.DeliveryPathLabel(ChatDeliveryPath.Auto)
            : ChatP2PSession.DeliveryPathLabel(_p2PSession.DeliveryPath);
    }

    private void ShowDeliveryPathMenu()
    {
        if (_p2PSession == null)
            return;

        var menu = new ContextMenuStrip();
        ChatDeliveryPath? chosen = null;
        menu.Items.Add("Сервер", null, (_, _) => chosen = ChatDeliveryPath.Server);
        menu.Items.Add("Mesh (UDP/BLE)", null, (_, _) => chosen = ChatDeliveryPath.Mesh);
        menu.Closed += (_, _) =>
        {
            // Dispose after Closed finishes — disposing inside Closed races ToolStripDropDown.CreateHandle.
            var path = chosen;
            void Finish()
            {
                menu.Dispose();
                if (path != null)
                    _ = ApplyDeliveryPathAsync(path.Value);
            }

            if (IsHandleCreated)
                BeginInvoke(Finish);
            else
                Finish();
        };
        menu.Show(_deliveryPath, new Point(0, _deliveryPath.Height));
    }

    private async Task ApplyDeliveryPathAsync(ChatDeliveryPath path)
    {
        if (_p2PSession == null)
            return;

        _userActions.LogInformation("Chat {Peer}: switch delivery path to {Path}", _chat.PeerNickname, path);
        try
        {
            await _p2PSession.SwitchDeliveryPathAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delivery path switch failed for chat {ChatId}", _chat.Id);
            MessageBox.Show(this, ex.Message, "Путь доставки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshDeliveryPathButton();
        RefreshSafetyLabel();
    }

    private void OnDeliveryPathChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                RefreshDeliveryPathButton();
                RefreshSafetyLabel();
            });
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private void OnActiveChannelChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(RefreshSafetyLabel);
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _voiceDiscardNextStop = true;
        CleanupVoiceRecordingHardware();
        StopVoicePlaybackInternal();

        if (_p2PSession != null)
        {
            _p2PSession.MessagesChanged -= OnP2pMessagesChanged;
            _p2PSession.DeliveryPathChanged -= OnDeliveryPathChanged;
            _p2PSession.ActiveChannelChanged -= OnActiveChannelChanged;
            _p2PSession = null;
        }

        foreach (var item in _messages.Items)
            if (item is ChatLine line)
                line.Dispose();

        _messages.Items.Clear();
        base.OnFormClosed(e);
    }

    private void OnP2pMessagesChanged(object? sender, EventArgs e)
    {
        BeginInvoke(() => _ = ReloadMessagesAsync());
    }

    private async Task ReloadMessagesAsync()
    {
        _loadedRows.Clear();
        _hasMoreRows = true;
        await LoadNextMessagesPageAsync().ConfigureAwait(true);
    }

    private async Task LoadNextMessagesPageAsync()
    {
        if (_isLoadingRows || !_hasMoreRows)
            return;
        _isLoadingRows = true;
        try
        {
            var page = await _repo.ListMessagesPageDescAsync(_chat.Id, _loadedRows.Count, MessagesPageSize)
                .ConfigureAwait(true);
            _hasMoreRows = page.Count == MessagesPageSize;
            _loadedRows.AddRange(page);

            var rows = _loadedRows;
            if (!IsHandleCreated || IsDisposed)
                return;
            _messages.BeginUpdate();
            foreach (var existing in _messages.Items)
                if (existing is ChatLine oldLine)
                    oldLine.Dispose();

            _messages.Items.Clear();
            foreach (var m in rows) _messages.Items.Add(BuildChatLine(m));
            _messages.EndUpdate();
        }
        catch (ObjectDisposedException)
        {
            // expected while closing
        }
        finally
        {
            _isLoadingRows = false;
        }
    }

    private ChatLine BuildChatLine(ChatMessageEntity m)
    {
        var whoPrefix = m.Outgoing ? "[Я:]" : $"[{_chat.PeerNickname}:]";
        var color = m.Outgoing ? Color.DodgerBlue : Color.FromArgb(0x00, 0x99, 0x99);
        var sentLocal = new DateTimeOffset(m.SentUtcTicks, TimeSpan.Zero).ToLocalTime();
        var ts = sentLocal.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var ds = (MessageDeliveryStatus)m.DeliveryStatus;
        if (m.Outgoing && ds == MessageDeliveryStatus.NotApplicable)
            ds = MessageDeliveryStatus.Delivered;

        if (m.PayloadKind == (int)ChatPayloadKind.File && m.ImageBlob is { Length: > 0 } voiceBlob &&
            string.Equals(m.MimeType, VoiceRecordHelper.VoiceMessageMime, StringComparison.OrdinalIgnoreCase))
        {
            var kb = (voiceBlob.Length + 1023) / 1024;
            var caption = $"{whoPrefix} [{ts}] · голосовое · Ogg Opus · mono · {kb} КБ";
            return new ChatLine(m.Id, caption, color, m.Outgoing, ds, ChatLineKind.Voice, voiceBlob,
                VoiceRecordHelper.VoiceFileName);
        }

        if (m.PayloadKind == (int)ChatPayloadKind.File && m.ImageBlob is { Length: > 0 } fileBlob)
        {
            var kb = (fileBlob.Length + 1023) / 1024;
            var name = string.IsNullOrEmpty(m.Text) ? "file" : m.Text;
            if (m.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var caption = $"{whoPrefix} [{ts}] · видео · {name} · {kb} КБ";
                return new ChatLine(m.Id, caption, color, m.Outgoing, ds, ChatLineKind.Video, fileBlob, name);
            }

            var captionWithHint =
                $"{whoPrefix} [{ts}] · документ · {name} · {kb} КБ{FileCaptionNewline}{FileDownloadHintPrefix}{FileDownloadHintAction}";
            return new ChatLine(m.Id, captionWithHint, color, m.Outgoing, ds, ChatLineKind.File, fileBlob, name);
        }

        if (m.PayloadKind == (int)ChatPayloadKind.Image && m.ImageBlob is { Length: > 0 } blob)
        {
            var kb = (blob.Length + 1023) / 1024;
            var mimeShort = string.IsNullOrEmpty(m.MimeType) ? "image" : m.MimeType.Replace("image/", "");
            var caption = $"{whoPrefix} [{ts}] · {mimeShort} · {kb} КБ";
            return new ChatLine(m.Id, caption, color, m.Outgoing, ds, ChatLineKind.Image, blob, null);
        }

        if (m.PayloadKind == (int)ChatPayloadKind.TransferOffer)
        {
            var kb = (m.TransferSizeBytes + 1023) / 1024;
            var state = (ChatTransferState)m.TransferState;
            var stateText = state switch
            {
                ChatTransferState.Transferring => "загрузка...",
                ChatTransferState.Received => "получено",
                ChatTransferState.Failed => "ошибка",
                _ => "нажмите для скачивания"
            };
            var name = string.IsNullOrWhiteSpace(m.TransferFileName) ? m.Text : m.TransferFileName;
            var caption =
                $"{whoPrefix} [{ts}] · {m.TransferPayloadKind} · {name} · {kb} КБ{FileCaptionNewline}{stateText}";
            return new ChatLine(m.Id, caption, color, m.Outgoing, ds, ChatLineKind.TransferOffer, null, name);
        }

        var full = $"{whoPrefix} [{ts}] {m.Text}";
        return new ChatLine(m.Id, full, color, m.Outgoing, ds, ChatLineKind.Text, null, null);
    }

    private async void OnMessagesMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta >= 0)
            return;
        if (!IsScrolledToBottom(_messages))
            return;
        await LoadNextMessagesPageAsync().ConfigureAwait(true);
    }

    private async void OnMessagesKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.PageDown or Keys.Down or Keys.End))
            return;
        if (!IsScrolledToBottom(_messages))
            return;
        await LoadNextMessagesPageAsync().ConfigureAwait(true);
    }

    private async Task OnAttachVideoAsync()
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();

        using var dlg = new OpenFileDialog
        {
            Title = BuildOgvDialogTitle(_appSettings.Current.TrafficQuality),
            Filter = "Видео OGV|*.ogv|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (_p2PSession.IsMeshtasticPathActive)
            {
                MessageBox.Show(this,
                    "Передача видео по пути Meshtastic пока запрещена.",
                    "Видео", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!VideoAttachHelper.TryGetMimeFromExtension(dlg.FileName, out _))
            {
                MessageBox.Show(this, "Допустим только .ogv.", "Видео", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var prepared = await VideoAttachHelper
                .TryLoadAndValidateOgvAsync(dlg.FileName,
                    _media.GetMaxVideoBytes(_appSettings.Current.TrafficQuality),
                    _appSettings.Current.TrafficQuality)
                .ConfigureAwait(true);
            if (!prepared.Success || prepared.Bytes == null || prepared.OutputFileName == null ||
                prepared.OutputMime == null)
            {
                MessageBox.Show(this, prepared.Error ?? "Видео не прошло валидацию.", "Видео", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _media.ValidateDocumentMime(prepared.OutputMime);
            _media.ValidateVideoSize(prepared.Bytes.Length, _appSettings.Current.TrafficQuality);
            await _p2PSession.SendFileAsync(prepared.OutputFileName, prepared.Bytes, prepared.OutputMime)
                .ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: sent ogv video ({Bytes} bytes, {Mime})",
                _chat.PeerNickname, prepared.Bytes.Length, prepared.OutputMime);
            ClearDeliveryIssue();
        }
        catch (OutboundMessageQueuedException ex)
        {
            _logger.LogInformation(ex, "Video queued until peer is on LAN (chat {ChatId})", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: video queued for LAN delivery", _chat.PeerNickname);
            ShowDeliveryIssue(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send video failed in chat {ChatId}", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: send video failed ({Message})", _chat.PeerNickname, ex.Message);
            if (HandleBluetoothUnavailable(ex))
                return;
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private static string BuildOgvDialogTitle(TrafficQualityMode mode)
    {
        var (w, h) = mode.GetVideoResolution();
        return $"Видео OGV ({w}x{h}, до {VideoAttachHelper.MaxDurationSeconds} секунд)";
    }

    private async Task OnAttachCameraAsync()
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();
        if (_p2PSession.IsMeshtasticPathActive)
        {
            MessageBox.Show(this,
                "Передача видео по пути Meshtastic пока запрещена.",
                "Видео", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var win = new CameraRecordForm(_appSettings.Current.TrafficQuality,
            _appSettings.Current.VideoInputDeviceId);
        if (await win.ShowDialogAsync(this) != DialogResult.OK || win.Result == null)
            return;

        try
        {
            _media.ValidateDocumentMime(win.Result.MimeType);
            _media.ValidateVideoSize(win.Result.Bytes.Length, _appSettings.Current.TrafficQuality);
            await _p2PSession.SendFileAsync(win.Result.FileName, win.Result.Bytes, win.Result.MimeType)
                .ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: sent camera video ({Bytes} bytes, {Mime})",
                _chat.PeerNickname, win.Result.Bytes.Length, win.Result.MimeType);
            ClearDeliveryIssue();
        }
        catch (OutboundMessageQueuedException ex)
        {
            _logger.LogInformation(ex, "Camera video queued until peer is on LAN (chat {ChatId})", _chat.Id);
            ShowDeliveryIssue(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send camera video failed in chat {ChatId}", _chat.Id);
            if (HandleBluetoothUnavailable(ex))
                return;
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async Task OnAttachImageAsync()
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();

        using var dlg = new OpenFileDialog
        {
            Title = "Изображение (JPEG, PNG, GIF)",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.gif|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (!ImageAttachHelper.TryGetMimeFromExtension(dlg.FileName, out var mime))
            {
                MessageBox.Show(this, "Допустимы только .jpg, .jpeg, .png, .gif.", "Файл", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(dlg.FileName).ConfigureAwait(true);
            if (bytes.Length < 12)
            {
                MessageBox.Show(this, "Файл слишком маленький.", "Файл", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (!ImageAttachHelper.SniffMatchesMime(bytes.AsSpan(0, Math.Min(12, bytes.Length)), mime))
            {
                MessageBox.Show(this, "Содержимое не совпадает с расширением файла.", "Файл", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var maxImageBytes = _media.GetMaxImageBytes(_appSettings.Current.TrafficQuality);
            if (bytes.Length > maxImageBytes)
            {
                var want = MessageBox.Show(this,
                    $"Файл {ChatMediaOptions.FormatByteLimit(bytes.Length)} больше лимита {ChatMediaOptions.FormatByteLimit(maxImageBytes)} (настраивается в chat-media.json). Сжать изображение?",
                    "Размер",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (want != DialogResult.Yes)
                    return;
                if (!ImageAttachmentCompressor.TryCompressToMaxBytes(bytes, maxImageBytes, out var compressed,
                        out var err))
                {
                    MessageBox.Show(this, err ?? "Не удалось уложиться в лимит.", "Сжатие", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                bytes = compressed;
                mime = ImageAttachmentCompressor.SuggestMimeAfterCompression();
            }

            _media.ValidateMime(mime);
            await _p2PSession.SendImageAsync(bytes, mime).ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: sent image ({Bytes} bytes, {Mime})", _chat.PeerNickname,
                bytes.Length, mime);
            ClearDeliveryIssue();
        }
        catch (OutboundMessageQueuedException ex)
        {
            _logger.LogInformation(ex, "Image queued until peer is on LAN (chat {ChatId})", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: image queued for LAN delivery", _chat.PeerNickname);
            ShowDeliveryIssue(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send image failed in chat {ChatId}", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: send image failed ({Message})", _chat.PeerNickname, ex.Message);
            if (HandleBluetoothUnavailable(ex))
                return;
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async Task OnAttachDocumentAsync()
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();

        using var dlg = new OpenFileDialog
        {
            Title = "Документ Word / LibreOffice (до " +
                    ChatMediaOptions.FormatByteLimit(
                        _media.GetMaxDocumentBytes(_appSettings.Current.TrafficQuality)) + ")",
            Filter =
                "Документы|*.doc;*.docx;*.rtf;*.pdf;*.odt;*.ods;*.odp;*.odg;*.xlsx;*.xls;*.pptx;*.ppt|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (!DocumentAttachHelper.TryGetMimeFromExtension(dlg.FileName, out var mime))
            {
                MessageBox.Show(this,
                    "Допустимы только .doc, .docx, .rtf, .pdf, .odt, .ods, .odp, .odg, .xlsx, .xls, .pptx, .ppt.",
                    "Файл", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(dlg.FileName).ConfigureAwait(true);
            if (bytes.Length == 0)
            {
                MessageBox.Show(this, "Файл пустой.", "Файл", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var headLen = Math.Min(4096, bytes.Length);
            if (!DocumentAttachHelper.SniffMatchesMime(bytes.AsSpan(0, headLen), mime))
            {
                MessageBox.Show(this,
                    "Содержимое не совпадает с типом файла (ожидается корректный Office/LibreOffice).",
                    "Файл", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var maxDocumentBytes = _media.GetMaxDocumentBytes(_appSettings.Current.TrafficQuality);
            if (bytes.Length > maxDocumentBytes)
            {
                MessageBox.Show(this,
                    $"Файл больше {ChatMediaOptions.FormatByteLimit(maxDocumentBytes)} (лимит в chat-media.json: maxDocumentBytes).",
                    "Размер",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _media.ValidateDocumentMime(mime);
            await _p2PSession.SendFileAsync(dlg.FileName, bytes, mime).ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: sent document ({Bytes} bytes, {Mime})", _chat.PeerNickname,
                bytes.Length, mime);
            ClearDeliveryIssue();
        }
        catch (OutboundMessageQueuedException ex)
        {
            _logger.LogInformation(ex, "Document queued until peer is on LAN (chat {ChatId})", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: document queued for LAN delivery", _chat.PeerNickname);
            ShowDeliveryIssue(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send document failed in chat {ChatId}", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: send document failed ({Message})", _chat.PeerNickname,
                ex.Message);
            if (HandleBluetoothUnavailable(ex))
                return;
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
            return;

        // Ctrl+Enter inserts a newline; bare Enter sends the message.
        if (e.Control)
        {
            e.SuppressKeyPress = true;
            _input.SelectedText = "\n";
            return;
        }

        if (e.Alt || e.Shift)
            return;

        e.SuppressKeyPress = true;
        await OnSendAsync().ConfigureAwait(true);
    }

    private async Task OnSendAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _p2PSession == null)
            return;
        ClearDeliveryIssue();
        try
        {
            await _p2PSession.SendTextAsync(text).ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: sent message ({Length} chars)", _chat.PeerNickname, text.Length);
            _input.Clear();
            ClearDeliveryIssue();
        }
        catch (OutboundMessageQueuedException ex)
        {
            _logger.LogInformation(ex, "Message queued until peer is on LAN (chat {ChatId})", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: message queued for LAN delivery", _chat.PeerNickname);
            ShowDeliveryIssue(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send message failed in chat {ChatId}", _chat.Id);
            _userActions.LogInformation("Chat {Peer}: send message failed ({Message})", _chat.PeerNickname, ex.Message);
            if (HandleBluetoothUnavailable(ex))
                return;
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async Task OnTechHandshakeAsync()
    {
        if (_p2PSession == null)
            return;
        try
        {
            await _p2PSession.TechSendHandshakeAsync().ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: TECH send handshake", _chat.PeerNickname);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TECH handshake failed chat {ChatId}", _chat.Id);
            MessageBox.Show(this, ex.Message, "TECH: handshake", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OnTechInviteAsync()
    {
        if (_p2PSession == null)
            return;
        try
        {
            await _p2PSession.TechSendInviteAsync().ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: TECH send invite", _chat.PeerNickname);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TECH invite failed chat {ChatId}", _chat.Id);
            MessageBox.Show(this, ex.Message, "TECH: invite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OnTechPingAsync()
    {
        if (_p2PSession == null)
            return;
        try
        {
            await _p2PSession.TechSendPresencePingAsync().ConfigureAwait(true);
            _userActions.LogInformation("Chat {Peer}: TECH send ping", _chat.PeerNickname);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TECH ping failed chat {ChatId}", _chat.Id);
            MessageBox.Show(this, ex.Message, "TECH: ping", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private int MaxVoiceRecordSeconds =>
        _media.GetMaxVoiceSeconds(_appSettings.Current.TrafficQuality);

    private void RefreshVoiceRecordTooltip()
    {
        var voiceBytes = ChatMediaOptions.FormatByteLimit(
            _media.GetMaxVoiceBytes(_appSettings.Current.TrafficQuality));
        _buttonTooltips.SetToolTip(_attachVoice,
            $"Голосовое (Ogg Opus, моно, до {voiceBytes}, до {MaxVoiceRecordSeconds} сек): нажмите для начала записи, ещё раз — остановить и отправить. Битрейт зависит от режима экономии трафика.");
    }

    private void OnAttachVoice()
    {
        if (_p2PSession == null)
            return;
        RefreshVoiceRecordTooltip();

        if (_voiceWaveIn != null)
        {
            _voiceDiscardNextStop = false;
            try
            {
                _voiceWaveIn.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stop voice recording");
                CleanupVoiceRecordingHardware();
            }

            return;
        }

        try
        {
            _voiceDiscardNextStop = false;
            _voiceWaveMs = new MemoryStream();
            var wf = new WaveFormat(16000, 16, 1);
            _voiceWaveWriter = new WaveFileWriter(_voiceWaveMs, wf);
            var selectedDevice = _appSettings.Current.VoiceInputDeviceNumber;
            if (selectedDevice.HasValue)
                if (selectedDevice.Value >= WaveIn.DeviceCount)
                {
                    MessageBox.Show(this,
                        "Выбранный источник звука недоступен. Откройте Настройки -> Звук и выберите доступное устройство.",
                        "Микрофон",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

            _voiceWaveIn = new WaveInEvent
            {
                WaveFormat = wf,
                BufferMilliseconds = 100
            };
            if (selectedDevice.HasValue)
                _voiceWaveIn.DeviceNumber = selectedDevice.Value;
            _voiceWaveIn.DataAvailable += VoiceWaveInOnDataAvailable;
            _voiceWaveIn.RecordingStopped += VoiceWaveInOnRecordingStopped;
            _voiceRecordStartUtc = DateTime.UtcNow;
            _voiceWaveIn.StartRecording();
            _attachVoice.BackColor = Color.MistyRose;
            _voiceRecordTimer?.Dispose();
            _voiceRecordTimer = new Timer { Interval = 400 };
            _voiceRecordTimer.Tick += (_, _) =>
            {
                if (_voiceWaveIn == null)
                    return;
                if ((DateTime.UtcNow - _voiceRecordStartUtc).TotalSeconds >= MaxVoiceRecordSeconds)
                {
                    _voiceDiscardNextStop = false;
                    try
                    {
                        _voiceWaveIn.StopRecording();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            };
            _voiceRecordTimer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice record start failed");
            CleanupVoiceRecordingHardware();
            _attachVoice.BackColor = SystemColors.Control;
            MessageBox.Show(this, ex.Message, "Микрофон", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void VoiceWaveInOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;
        lock (_voiceCapLock)
        {
            try
            {
                _voiceWaveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                // disposed
            }
        }
    }

    private void VoiceWaveInOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        byte[] wav;
        lock (_voiceCapLock)
        {
            try
            {
                _voiceWaveWriter?.Dispose();
            }
            catch
            {
                // ignore
            }

            _voiceWaveWriter = null;
            try
            {
                wav = _voiceWaveMs?.ToArray() ?? [];
            }
            catch
            {
                wav = [];
            }

            try
            {
                _voiceWaveMs?.Dispose();
            }
            catch
            {
                // ignore
            }

            _voiceWaveMs = null;
        }

        try
        {
            _voiceWaveIn?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voiceWaveIn = null;

        var discard = _voiceDiscardNextStop;
        _voiceDiscardNextStop = false;

        if (!IsHandleCreated)
            return;

        BeginInvoke(async () =>
        {
            _attachVoice.BackColor = SystemColors.Control;
            try
            {
                _voiceRecordTimer?.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                _voiceRecordTimer?.Dispose();
            }
            catch
            {
                // ignore
            }

            _voiceRecordTimer = null;

            if (e.Exception != null)
            {
                _logger.LogWarning(e.Exception, "Voice recording stopped with error");
                MessageBox.Show(this, e.Exception.Message, "Запись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (discard || wav.Length < 200)
                return;

            UseWaitCursor = true;
            try
            {
                var (ok, ogg, err) = await VoiceRecordHelper
                    .EncodeWavPcmToOggOpusAsync(wav, _appSettings.Current.TrafficQuality)
                    .ConfigureAwait(true);
                if (!ok || ogg == null)
                {
                    MessageBox.Show(this, err ?? "Кодирование в Ogg не удалось.", "Голосовое", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                _media.ValidateDocumentMime(VoiceRecordHelper.VoiceMessageMime);
                var maxVoiceBytes = _media.GetMaxVoiceBytes(_appSettings.Current.TrafficQuality);
                if (ogg.Length > maxVoiceBytes)
                {
                    MessageBox.Show(this,
                        $"Файл больше {ChatMediaOptions.FormatByteLimit(maxVoiceBytes)}.",
                        "Голосовое", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _media.ValidateVoiceSize(ogg.Length, _appSettings.Current.TrafficQuality);
                await _p2PSession!
                    .SendFileAsync(VoiceRecordHelper.VoiceFileName, ogg, VoiceRecordHelper.VoiceMessageMime)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Send voice failed");
                if (HandleBluetoothUnavailable(ex))
                    return;
                ShowDeliveryIssue(ex.Message);
            }
            finally
            {
                UseWaitCursor = false;
                await ReloadMessagesAsync().ConfigureAwait(true);
            }
        });
    }

    private void CleanupVoiceRecordingHardware()
    {
        try
        {
            _voiceRecordTimer?.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            _voiceRecordTimer?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voiceRecordTimer = null;
        lock (_voiceCapLock)
        {
            try
            {
                _voiceWaveWriter?.Dispose();
            }
            catch
            {
                // ignore
            }

            _voiceWaveWriter = null;
            try
            {
                _voiceWaveMs?.Dispose();
            }
            catch
            {
                // ignore
            }

            _voiceWaveMs = null;
        }

        try
        {
            _voiceWaveIn?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voiceWaveIn = null;
    }

    private void StopVoicePlaybackInternal()
    {
        try
        {
            _voicePlaybackOut?.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            _voicePlaybackOut?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voicePlaybackOut = null;
        try
        {
            _voicePlaybackRaw?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voicePlaybackRaw = null;
        try
        {
            _voicePlaybackReader?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voicePlaybackReader = null;
        try
        {
            _voicePlaybackMem?.Dispose();
        }
        catch
        {
            // ignore
        }

        _voicePlaybackMem = null;
    }

    private void PlayVoiceMessage(byte[] ogg)
    {
        StopVoicePlaybackInternal();
        try
        {
            try
            {
                var (pcm, sr) = VoiceRecordHelper.DecodeOpusOggToPcm16(ogg);
                var pcmMs = new MemoryStream(pcm, false);
                _voicePlaybackRaw = new RawSourceWaveStream(pcmMs, new WaveFormat(sr, 16, 1));
                _voicePlaybackOut = new WaveOutEvent();
                _voicePlaybackOut.Init(_voicePlaybackRaw);
            }
            catch
            {
                _voicePlaybackRaw?.Dispose();
                _voicePlaybackRaw = null;
                _voicePlaybackMem = new MemoryStream(ogg, false);
                _voicePlaybackReader = new VorbisWaveReader(_voicePlaybackMem);
                _voicePlaybackOut = new WaveOutEvent();
                _voicePlaybackOut.Init(_voicePlaybackReader);
            }

            _voicePlaybackOut.PlaybackStopped += OnVoicePlaybackStopped;
            _voicePlaybackOut.Play();
        }
        catch (Exception ex)
        {
            StopVoicePlaybackInternal();
            _logger.LogWarning(ex, "Voice playback start failed");
            MessageBox.Show(this, ex.Message, "Воспроизведение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OnVoicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => OnVoicePlaybackStopped(sender, e));
            }
            catch
            {
                StopVoicePlaybackInternal();
            }

            return;
        }

        StopVoicePlaybackInternal();
        if (e.Exception != null && IsHandleCreated && !IsDisposed)
            MessageBox.Show(this, e.Exception.Message, "Воспроизведение", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
    }

    private void OnMessagesMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        var idx = _messages.IndexFromPoint(e.Location);
        if (idx < 0 || idx >= _messages.Items.Count)
            return;
        if (_messages.Items[idx] is not ChatLine line)
            return;
        if (line.IsRetryable)
        {
            _ = RetryFailedMessageAsync(line.MessageId);
            return;
        }

        if (line.Kind == ChatLineKind.TransferOffer)
        {
            _ = DownloadTransferOfferAsync(line.MessageId);
            return;
        }

        if (line.Kind is not (ChatLineKind.Voice or ChatLineKind.Video))
            return;
        if (line.PayloadBytes is not { Length: > 0 })
            return;
        if (line.Kind == ChatLineKind.Voice)
        {
            if (line.PlayButtonBounds == Rectangle.Empty || !line.PlayButtonBounds.Contains(e.Location))
                return;
            _userActions.LogInformation("Chat {Peer}: play voice message", _chat.PeerNickname);
            PlayVoiceMessage(line.PayloadBytes);
            return;
        }

        if (line.PlayButtonBounds != Rectangle.Empty && line.PlayButtonBounds.Contains(e.Location))
        {
            _userActions.LogInformation("Chat {Peer}: play video message", _chat.PeerNickname);
            PlayVideoMessage(line.PayloadBytes, line.FileSuggestedName);
            return;
        }

        if (line.SaveButtonBounds == Rectangle.Empty || !line.SaveButtonBounds.Contains(e.Location))
            return;
        using var sfd = new SaveFileDialog
        {
            Title = "Сохранить видео",
            FileName = string.IsNullOrEmpty(line.FileSuggestedName) ? "video.webm" : line.FileSuggestedName,
            Filter = "Все файлы|*.*",
            OverwritePrompt = true
        };
        if (sfd.ShowDialog(this) != DialogResult.OK)
            return;
        _userActions.LogInformation("Chat {Peer}: save video to {Path}", _chat.PeerNickname, sfd.FileName);
        File.WriteAllBytes(sfd.FileName, line.PayloadBytes);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _voiceDiscardNextStop = true;
        try
        {
            _voiceWaveIn?.StopRecording();
        }
        catch
        {
            // ignore
        }

        StopVoicePlaybackInternal();
        _repo.PeerPublicKeyChanged -= OnPeerPublicKeyChanged;
        _messengerServers.FailoverCompleted -= OnMessengerServerFailover;
        base.OnFormClosing(e);
    }

    private void RefreshSafetyLabel()
    {
        try
        {
            _safetyNumberLabel.Text = PeerSafetyDisplay.FormatPanel(
                PeerSafetyDisplay.FormatFingerprints(
                    _user.Nickname,
                    _auth.GetCurrentPublicKey(),
                    _chat.PeerNickname,
                    _chat.PeerRsaPublicJson),
                _chat,
                _p2PSession);
        }
        catch
        {
            _safetyNumberLabel.Text = "";
        }
    }

    private void OnPeerPublicKeyChanged(object? sender, PeerPublicKeyChangedEventArgs e)
    {
        if (e.ChatId != _chat.Id || !IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(async () =>
            {
                var fresh = await _repo.GetChatAsync(_chat.Id).ConfigureAwait(true);
                if (fresh != null)
                {
                    _chat.PeerRsaPublicJson = fresh.PeerRsaPublicJson;
                    _chat.PeerKeySourceKind = fresh.PeerKeySourceKind;
                    _chat.PeerKeySourceDetail = fresh.PeerKeySourceDetail;
                    _chat.PeerNickname = fresh.PeerNickname;
                }

                RefreshSafetyLabel();
                MessageBox.Show(
                    this,
                    PeerSafetyDisplay.FormatKeyChangeWarning(e),
                    PeerSafetyDisplay.KeyChangeTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            });
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private void OnMessengerServerFailover(object? sender, MessengerServerFailoverEventArgs e)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (e.SwitchedToMesh)
                {
                    MessageBox.Show(
                        this,
                        PeerSafetyDisplay.MeshWarning,
                        "Messenger-сервер",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private async Task OnEmergencyUntrustAsync()
    {
        var hint = PeerKeySource.IsServer(_chat.PeerKeySourceKind) ? _chat.PeerKeySourceDetail : null;
        try
        {
            await _messengerServers.MarkUntrustedWithFailoverAsync(null, hint).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emergency untrust failed");
            MessageBox.Show(this, ex.Message, "🚨", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshPeerPresenceLabel()
    {
        _peerInfoLabel.Text = PeerInfoText("Статус: офлайн");
        _peerInfoLabel.ForeColor = SystemColors.GrayText;
    }

    private void ShowDeliveryIssue(string message)
    {
        _deliveryIssueLabel.Text = string.IsNullOrWhiteSpace(message)
            ? "Проблема с доставкой текущего сообщения."
            : message.Trim();
        _deliveryIssueLabel.Visible = true;
    }

    private void ClearDeliveryIssue()
    {
        _deliveryIssueLabel.Text = string.Empty;
        _deliveryIssueLabel.Visible = false;
    }

    private string PeerInfoText(string statusLine)
    {
        return $"Id: {_chat.PeerNetworkIdShort}\r\n{statusLine}";
    }

    private bool HandleBluetoothUnavailable(Exception ex)
    {
        // if (!WindowsBluetoothTransport.IsUnavailableError(ex))
        //     return false;

        _userActions.LogInformation("Chat {Peer}: bluetooth unavailable ({Message})", _chat.PeerNickname, ex.Message);
        if (!_p2PRuntime.Settings.SuggestBluetoothPairing || _pairingPromptShown)
            return true;

        _pairingPromptShown = true;
        var answer = MessageBox.Show(this,
            "Bluetooth-пир недоступен или не сопряжён. Открыть системные настройки Bluetooth для сопряжения?",
            "Bluetooth",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes)
            return true;

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            _userActions.LogInformation("Chat {Peer}: opened system bluetooth settings", _chat.PeerNickname);
        }
        catch (Exception openEx)
        {
            _logger.LogWarning(openEx, "Could not open Bluetooth settings");
        }

        return true;
    }

    private void OnMessageDoubleClick(object? sender, EventArgs e)
    {
        if (_messages.SelectedItem is not ChatLine line)
            return;
        if (line.IsRetryable)
        {
            _ = RetryFailedMessageAsync(line.MessageId);
            return;
        }

        if (line.Kind == ChatLineKind.Voice && line.PayloadBytes is { Length: > 0 })
        {
            _userActions.LogInformation("Chat {Peer}: play voice message (double-click)", _chat.PeerNickname);
            PlayVoiceMessage(line.PayloadBytes);
            return;
        }

        if (line.Kind == ChatLineKind.File && line.PayloadBytes is { Length: > 0 })
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Сохранить файл",
                FileName = string.IsNullOrEmpty(line.FileSuggestedName) ? "document" : line.FileSuggestedName,
                Filter = "Все файлы|*.*",
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                _userActions.LogInformation("Chat {Peer}: save document to {Path}", _chat.PeerNickname, sfd.FileName);
                File.WriteAllBytes(sfd.FileName, line.PayloadBytes);
            }

            return;
        }

        if (line.Kind == ChatLineKind.TransferOffer)
        {
            _ = DownloadTransferOfferAsync(line.MessageId);
            return;
        }

        if (line.Kind == ChatLineKind.Video && line.PayloadBytes is { Length: > 0 })
        {
            _userActions.LogInformation("Chat {Peer}: play video message (double-click)", _chat.PeerNickname);
            PlayVideoMessage(line.PayloadBytes, line.FileSuggestedName);
            return;
        }

        if (line.Kind == ChatLineKind.Image && line.PayloadBytes is { Length: > 0 })
        {
            _userActions.LogInformation("Chat {Peer}: open image viewer", _chat.PeerNickname);
            using var v = new ImageViewForm("Изображение", line.PayloadBytes);
            v.ShowDialog(this);
            return;
        }

        if (line.DisplayText.Length <= 64)
            return;
        _userActions.LogInformation("Chat {Peer}: open long message viewer", _chat.PeerNickname);
        using var dlg = new MessageViewForm("Сообщение", line.DisplayText);
        dlg.ShowDialog(this);
    }

    private async Task OnClearChatAsync()
    {
        if (_p2PSession == null)
            return;

        var confirm = MessageBox.Show(this,
            "Все сообщения будут удалены с этого ПК. Недоставленные отправки будут отменены.",
            "Удалить переписку",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
            return;

        ClearDeliveryIssue();
        try
        {
            var ok = await _p2PSession.ClearMessagesAsync().ConfigureAwait(true);
            if (!ok)
            {
                MessageBox.Show(this, "Не удалось удалить переписку.", "Ошибка", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _userActions.LogInformation("Chat {Peer}: history cleared (chat id {ChatId})", _chat.PeerNickname,
                _chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clear chat failed for chat {ChatId}", _chat.Id);
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async Task RetryFailedMessageAsync(int messageId)
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();
        try
        {
            await _p2PSession.RetryFailedMessageAsync(messageId).ConfigureAwait(true);
            ClearDeliveryIssue();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retry failed message failed in chat {ChatId}", _chat.Id);
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private async Task DownloadTransferOfferAsync(int messageId)
    {
        if (_p2PSession == null)
            return;
        ClearDeliveryIssue();
        try
        {
            await _p2PSession.RequestBinaryDownloadAsync(messageId).ConfigureAwait(true);
            ClearDeliveryIssue();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer download failed in chat {ChatId}", _chat.Id);
            ShowDeliveryIssue(ex.Message);
        }
        finally
        {
            await ReloadMessagesAsync().ConfigureAwait(true);
        }
    }

    private void OnMessagesDrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _messages.Items.Count)
            return;

        var line = _messages.Items[e.Index] as ChatLine;
        var text = line?.DisplayText ?? _messages.Items[e.Index]?.ToString() ?? "";
        var color = line?.Color ?? ForeColor;
        const int statusCol = 22;
        var reserveRight = line is { Outgoing: true } ? statusCol : 0;
        var font = e.Font ?? _messages.Font;
        var textWidth = Math.Max(10, e.Bounds.Width - 8 - reserveRight);
        var captionMeasured = TextRenderer.MeasureText(text, font, new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var captionH = captionMeasured.Height;
        Rectangle textBounds;

        if (line?.Kind == ChatLineKind.File &&
            text.IndexOf(FileCaptionNewline, StringComparison.Ordinal) is var nlIdx && nlIdx >= 0)
        {
            var head = text[..nlIdx];
            var headMeasured = TextRenderer.MeasureText(head, font, new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, textWidth,
                Math.Max(font.Height, headMeasured.Height));
            TextRenderer.DrawText(e.Graphics, head, font, textBounds, color,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            var line2Y = textBounds.Bottom + 2;
            var prefixW = TextRenderer.MeasureText(FileDownloadHintPrefix, font).Width;
            var prefixBounds = new Rectangle(e.Bounds.X + 4, line2Y, prefixW + 2, font.Height + 2);
            TextRenderer.DrawText(e.Graphics, FileDownloadHintPrefix, font, prefixBounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);

            var actionBounds = new Rectangle(e.Bounds.X + 4 + prefixW, line2Y,
                Math.Max(10, textWidth - prefixW), font.Height + 2);
            TextRenderer.DrawText(e.Graphics, FileDownloadHintAction, font, actionBounds, FileDownloadActionColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }
        else if (line?.Kind is ChatLineKind.Voice or ChatLineKind.Video)
        {
            textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, textWidth, Math.Max(font.Height, captionH));
            TextRenderer.DrawText(e.Graphics, text, font, textBounds, color,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            var playY = textBounds.Bottom + 4;
            var playRect = new Rectangle(e.Bounds.X + 4, playY, 52, 22);
            line.PlayButtonBounds = playRect;
            using (var br = new SolidBrush(Color.WhiteSmoke))
            {
                e.Graphics.FillRectangle(br, playRect);
            }

            using var pen = new Pen(Color.SteelBlue, 1);
            e.Graphics.DrawRectangle(pen, playRect);
            TextRenderer.DrawText(e.Graphics, "Play", font, playRect, Color.SteelBlue,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (line.Kind == ChatLineKind.Video)
            {
                var saveRect = new Rectangle(playRect.Right + 8, playY, 52, 22);
                line.SaveButtonBounds = saveRect;
                using var saveBrush = new SolidBrush(Color.WhiteSmoke);
                e.Graphics.FillRectangle(saveBrush, saveRect);
                e.Graphics.DrawRectangle(pen, saveRect);
                TextRenderer.DrawText(e.Graphics, "Save", font, saveRect, Color.SteelBlue,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                line.SaveButtonBounds = Rectangle.Empty;
            }
        }
        else
        {
            textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, textWidth, Math.Max(font.Height, captionH));
            TextRenderer.DrawText(e.Graphics, text, font, textBounds, color,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        if (line is { Kind: ChatLineKind.Image, Thumbnail: { } thumb })
        {
            var imgY = textBounds.Bottom + 4;
            var imgRect = new Rectangle(e.Bounds.X + 4, imgY, thumb.Width, thumb.Height);
            e.Graphics.DrawImage(thumb, imgRect);
        }

        if (line is { Outgoing: true })
        {
            var (glyph, gColor) = OutgoingDeliveryDraw(line.DeliveryStatus);
            var gb = new Rectangle(e.Bounds.Right - statusCol - 2, e.Bounds.Y + 2, statusCol,
                Math.Max(font.Height, e.Bounds.Height - 4));
            TextRenderer.DrawText(e.Graphics, glyph, font, gb, gColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        e.DrawFocusRectangle();
    }

    private void OnMessagesMeasureItem(object? sender, MeasureItemEventArgs e)
    {
        // Vertical padding for Segoe UI 12pt (OwnerDrawVariable).
        const int rowPad = 10;
        if (e.Index < 0 || e.Index >= _messages.Items.Count)
        {
            e.ItemHeight = Math.Max(Font.Height + rowPad, 26);
            return;
        }

        var line = _messages.Items[e.Index] as ChatLine;
        var text = line?.DisplayText ?? _messages.Items[e.Index]?.ToString() ?? "";
        const int statusCol = 22;
        var reserveRight = line is { Outgoing: true } ? statusCol : 0;
        var width = Math.Max(120,
            _messages.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12 - reserveRight);
        var measured = TextRenderer.MeasureText(text, _messages.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var h = measured.Height + rowPad;
        if (line is { Kind: ChatLineKind.Image, Thumbnail: { } thumb })
            h += 4 + thumb.Height + 4;
        if (line?.Kind is ChatLineKind.Voice or ChatLineKind.Video)
            h += 4 + 22 + 4;
        e.ItemHeight = Math.Max(_messages.Font.Height + rowPad, h);
    }

    private void PlayVideoMessage(byte[] ogvBytes, string? fileName)
    {
        using var v = new VideoPlayerForm(ogvBytes, fileName ?? "video.ogv");
        v.ShowDialog(this);
    }

    private static bool IsScrolledToBottom(ListBox listBox)
    {
        if (!listBox.IsHandleCreated || listBox.Items.Count == 0)
            return false;

        var si = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = SIF_ALL
        };
        if (!GetScrollInfo(listBox.Handle, SB_VERT, ref si))
            return false;

        return si.nPos + (int)si.nPage >= si.nMax;
    }

    private static (string Glyph, Color Color) OutgoingDeliveryDraw(MessageDeliveryStatus status)
    {
        return status switch
        {
            MessageDeliveryStatus.Pending => (OutgoingDeliveryIndicators.Pending, Color.DarkGoldenrod),
            MessageDeliveryStatus.Sent => (OutgoingDeliveryIndicators.Sent, Color.ForestGreen),
            MessageDeliveryStatus.Delivered => (OutgoingDeliveryIndicators.Delivered, Color.ForestGreen),
            MessageDeliveryStatus.Failed => (OutgoingDeliveryIndicators.Failed, Color.Red),
            _ => (OutgoingDeliveryIndicators.Delivered, Color.ForestGreen)
        };
    }

    private static Bitmap? TryCreateThumbnail(byte[] bytes, int maxEdge)
    {
        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var src = Image.FromStream(ms, false, false);
            var w = src.Width;
            var h = src.Height;
            if (w <= 0 || h <= 0)
                return null;
            var scale = Math.Min(1.0, Math.Min((double)maxEdge / w, (double)maxEdge / h));
            var tw = Math.Max(1, (int)Math.Round(w * scale));
            var th = Math.Max(1, (int)Math.Round(h * scale));
            var bmp = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, tw, th);
            }

            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpScrollInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    private enum ChatLineKind
    {
        Text,
        Image,
        File,
        Voice,
        Video,
        TransferOffer
    }

    private sealed class ChatLine : IDisposable
    {
        private const int ThumbMaxEdge = 240;
        private bool _disposed;

        public ChatLine(int messageId, string displayText, Color color, bool outgoing,
            MessageDeliveryStatus deliveryStatus,
            ChatLineKind kind, byte[]? payloadBytes, string? fileSuggestedName)
        {
            MessageId = messageId;
            DisplayText = displayText;
            Color = color;
            Outgoing = outgoing;
            DeliveryStatus = deliveryStatus;
            Kind = kind;
            PayloadBytes = payloadBytes;
            FileSuggestedName = fileSuggestedName;
            PlayButtonBounds = Rectangle.Empty;
            if (kind == ChatLineKind.Image && payloadBytes is { Length: > 0 })
                Thumbnail = TryCreateThumbnail(payloadBytes, ThumbMaxEdge);
        }

        public int MessageId { get; }
        public string DisplayText { get; }
        public Color Color { get; }
        public bool Outgoing { get; }
        public MessageDeliveryStatus DeliveryStatus { get; }
        public ChatLineKind Kind { get; }
        public byte[]? PayloadBytes { get; }
        public string? FileSuggestedName { get; }
        public Rectangle PlayButtonBounds { get; set; }
        public Rectangle SaveButtonBounds { get; set; }
        public Bitmap? Thumbnail { get; private set; }

        public bool IsRetryable => Outgoing && DeliveryStatus == MessageDeliveryStatus.Failed && MessageId > 0;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Thumbnail?.Dispose();
            Thumbnail = null;
        }

        public override string ToString()
        {
            return DisplayText;
        }
    }
}