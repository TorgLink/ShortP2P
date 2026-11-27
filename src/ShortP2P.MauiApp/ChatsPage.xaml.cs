using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ShortP2P.Auth;
using ShortP2P.Auth.Data;
using ShortP2P.Client;
using ShortP2P.Client.Bluetooth;
using ShortP2P.Client.Data;
using ShortP2P.Client.ProfileBackup;
using ShortP2P.Client.Services;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Crypto;
using ShortP2P.Transport;

namespace ShortP2P.MauiApp;

public partial class ChatsPage : ContentPage
{
    private readonly AuthService _auth;
    private readonly IBluetoothRadioCatalog _bluetoothCatalog;
    private readonly ObservableCollection<ChatListRowVm> _chatRows = [];
    private readonly ChatRepository _chats;
    private readonly ILogger<ChatsPage> _logger;
    private readonly MessengerServerManager _messengerServers;
    private readonly UserP2pRuntime _p2p;
    private IDispatcherTimer? _presenceRefreshTimer;

    public ChatsPage(AuthService auth, ChatRepository chats, UserP2pRuntime p2p,
        IBluetoothRadioCatalog bluetoothCatalog, MessengerServerManager messengerServers,
        ILogger<ChatsPage> logger)
    {
        InitializeComponent();
        _auth = auth;
        _chats = chats;
        _p2p = p2p;
        _bluetoothCatalog = bluetoothCatalog;
        _messengerServers = messengerServers;
        _logger = logger;
        ChatsCollection.ItemsSource = _chatRows;
    }

    private void OnChatListChangedFromInvite(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => _ = OnChatListChangedAsync());
    }

    private async Task OnChatListChangedAsync()
    {
        var ui = SynchronizationContext.Current;
        await RefreshAsync().ConfigureAwait(true);
        var u = _auth.CurrentUser;
        if (u == null)
            return;
        _ = EnsureSessionsAfterChatListChangedAsync(u, ui);
    }

    private async Task EnsureSessionsAfterChatListChangedAsync(UserEntity u, SynchronizationContext? ui)
    {
        try
        {
            await _p2p.EnsureAllChatSessionsStartedAsync(u, _auth, _chats, ui, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensure chat sessions after list change");
        }
    }

    private async void OnRoutingClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<RoutingSettingsPage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private async void OnServersClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<MessengerServersPage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private async void OnLanScanClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<LanScanPage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private async void OnProfileClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<ProfilePage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private async void OnLogsClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<LogsPage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _chats.ChatListChanged -= OnChatListChangedFromInvite;
        _chats.ChatListChanged += OnChatListChangedFromInvite;
        _p2p.LocalScan.ClientsChanged -= OnLanPresenceChanged;
        _p2p.LocalScan.ClientsChanged += OnLanPresenceChanged;
        _messengerServers.TrustThreatDetected -= OnMessengerServerTrustThreat;
        _messengerServers.TrustThreatDetected += OnMessengerServerTrustThreat;
        _chats.PeerPublicKeyChanged -= OnPeerPublicKeyChanged;
        _chats.PeerPublicKeyChanged += OnPeerPublicKeyChanged;
        _chats.PeerKeyChangeProposed -= OnPeerKeyChangeProposed;
        _chats.PeerKeyChangeProposed += OnPeerKeyChangeProposed;
        _messengerServers.FailoverCompleted -= OnMessengerServerFailover;
        _messengerServers.FailoverCompleted += OnMessengerServerFailover;
        EnsurePresenceRefreshTimerStarted();
        var u = _auth.CurrentUser;
        if (u != null)
            try
            {
                await _p2p.EnsureStartedAsync(u).ConfigureAwait(true);
                await _p2p.EnsureAllChatSessionsStartedAsync(u, _auth, _chats, SynchronizationContext.Current)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ensure P2P on chats page appearing");
            }

        await RefreshAsync().ConfigureAwait(true);
    }

    protected override void OnDisappearing()
    {
        _p2p.LocalScan.ClientsChanged -= OnLanPresenceChanged;
        _messengerServers.TrustThreatDetected -= OnMessengerServerTrustThreat;
        _chats.PeerPublicKeyChanged -= OnPeerPublicKeyChanged;
        _chats.PeerKeyChangeProposed -= OnPeerKeyChangeProposed;
        _messengerServers.FailoverCompleted -= OnMessengerServerFailover;
        if (_presenceRefreshTimer != null)
            _presenceRefreshTimer.Stop();
        base.OnDisappearing();
    }

    private void OnMessengerServerTrustThreat(object? sender, MessengerServerTrustThreatEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlert(
                "Угроза безопасности",
                $"Сертификат сервера {e.Server.BaseUrl} не совпадает с сохранённым fingerprint.\n\n" +
                $"Ожидался: {e.ExpectedFingerprint}\nПолучен: {e.ActualFingerprint}\n\n" +
                "Сервер отключён и помечен как недоверенный.",
                "OK").ConfigureAwait(true);
        });
    }

    private void OnPeerPublicKeyChanged(object? sender, PeerPublicKeyChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var u = _auth.CurrentUser;
            if (u != null)
                RefreshSafetyHeader(u);
            await DisplayAlert(
                PeerSafetyDisplay.KeyChangeTitle,
                PeerSafetyDisplay.FormatKeyChangeWarning(e),
                "OK").ConfigureAwait(true);
        });
    }

    private void OnPeerKeyChangeProposed(object? sender, PeerKeyChangeProposedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var accept = await DisplayAlert(
                PeerSafetyDisplay.KeyChangeTitle,
                PeerSafetyDisplay.FormatKeyChangeProposal(e),
                "Accept",
                "Reject").ConfigureAwait(true);
            if (accept)
                await _chats.AcceptPendingPeerKeyChangeAsync(e.ChatId).ConfigureAwait(true);
            else
                _chats.RejectPendingPeerKeyChange(e.ChatId);
            var u = _auth.CurrentUser;
            if (u != null)
                RefreshSafetyHeader(u);
        });
    }

    private void OnMessengerServerFailover(object? sender, MessengerServerFailoverEventArgs e)
    {
        if (!e.SwitchedToMesh)
            return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlert("Messenger-сервер", PeerSafetyDisplay.MeshWarning, "OK").ConfigureAwait(true);
        });
    }

    private void RefreshSafetyHeader(UserEntity user)
    {
        try
        {
            var mine = SafetyNumber.FromPublicKey(_auth.GetCurrentPublicKey());
            SafetyNumberLabel.Text = $"{user.Nickname}: {mine}";
        }
        catch
        {
            SafetyNumberLabel.Text = "";
        }
    }

    private async void OnEmergencyUntrustClicked(object? sender, EventArgs e)
    {
        try
        {
            await _messengerServers.MarkUntrustedWithFailoverAsync(null, null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emergency untrust failed");
            await DisplayAlert("🚨", ex.Message, "OK").ConfigureAwait(true);
        }
    }

    private void OnLanPresenceChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdatePeerOnlineFlags);
    }

    private void UpdatePeerOnlineFlags()
    {
        foreach (var row in _chatRows)
            row.IsPeerOnline = _p2p.LocalScan.IsPeerSeenRecentlyOnLan(row.Chat.PeerNetworkIdShort);
        ReorderChatRows();
    }

    private void ReorderChatRows()
    {
        if (_chatRows.Count <= 1)
            return;

        var ordered = SortChatRows(_chatRows);
        for (var i = 0; i < ordered.Count; i++)
        {
            var currentIndex = _chatRows.IndexOf(ordered[i]);
            if (currentIndex >= 0 && currentIndex != i)
                _chatRows.Move(currentIndex, i);
        }
    }

    private List<ChatListRowVm> SortChatRows(IEnumerable<ChatListRowVm> rows) =>
        ChatListOrder.Sort(rows, r => r.IsPeerOnline, r => r.PeerNickname);

    private List<ChatEntity> SortChats(IEnumerable<ChatEntity> chats) =>
        ChatListOrder.Sort(
            chats,
            c => _p2p.LocalScan.IsPeerSeenRecentlyOnLan(c.PeerNetworkIdShort),
            c => c.PeerNickname);

    private void EnsurePresenceRefreshTimerStarted()
    {
        _presenceRefreshTimer ??= Dispatcher.CreateTimer();
        _presenceRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        _presenceRefreshTimer.Tick -= OnPresenceRefreshTimerTick;
        _presenceRefreshTimer.Tick += OnPresenceRefreshTimerTick;
        if (!_presenceRefreshTimer.IsRunning)
            _presenceRefreshTimer.Start();
    }

    private void OnPresenceRefreshTimerTick(object? sender, EventArgs e)
    {
        UpdatePeerOnlineFlags();
    }

    private async Task RefreshAsync()
    {
        var u = _auth.CurrentUser;
        if (u == null)
        {
            _chats.ChatListChanged -= OnChatListChangedFromInvite;
            _p2p.LocalScan.ClientsChanged -= OnLanPresenceChanged;
            Application.Current!.MainPage = new NavigationPage(MauiProgram.Services.GetRequiredService<LoginPage>());
            return;
        }

        try
        {
            var about = string.IsNullOrWhiteSpace(u.AboutMe) ? "" : $" · {TrimAbout(u.AboutMe, 40)}";
            ProfileLabel.Text =
                $"You: {u.Nickname} · id {u.NetworkIdShort} · local UDP {u.DataUdpPort}{about}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render own profile header (best-effort)");
            ProfileLabel.Text =
                $"You: {u.Nickname} · id {u.NetworkIdShort} · local UDP {u.DataUdpPort}";
        }
        RefreshSafetyHeader(u);

        var list = await _chats.ListChatsAsync(u.Id).ConfigureAwait(true);
        ApplyChatList(list);
    }

    private void ApplyChatList(IReadOnlyList<ChatEntity> list)
    {
        var sorted = SortChats(list);

        // Same membership and order: update rows in place (no Clear → no flicker).
        if (_chatRows.Count == sorted.Count)
        {
            var sameIds = true;
            for (var i = 0; i < sorted.Count; i++)
            {
                if (_chatRows[i].Chat.Id != sorted[i].Id)
                {
                    sameIds = false;
                    break;
                }
            }

            if (sameIds)
            {
                for (var i = 0; i < sorted.Count; i++)
                {
                    _chatRows[i].ApplyChat(sorted[i]);
                    _chatRows[i].IsPeerOnline =
                        _p2p.LocalScan.IsPeerSeenRecentlyOnLan(sorted[i].PeerNetworkIdShort);
                }

                return;
            }
        }

        _chatRows.Clear();
        foreach (var c in sorted)
            _chatRows.Add(new ChatListRowVm(c, _p2p.LocalScan.IsPeerSeenRecentlyOnLan(c.PeerNetworkIdShort)));
    }

    private async void OnAddChatClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<AddChatPage>();
        await Navigation.PushModalAsync(new NavigationPage(page)).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnMyQrClicked(object? sender, EventArgs e)
    {
        var page = MauiProgram.Services.GetRequiredService<MyQrPage>();
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private async void OnMyAddressesClicked(object? sender, EventArgs e)
    {
        var u = _auth.CurrentUser;
        if (u == null)
            return;
        string? bt = null;
        try
        {
            bt = await BluetoothRoutingMac.GetEffectiveMacAsync(_p2p.Settings, _bluetoothCatalog)
                .ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }

        // NIC enum + public IP lookup are sync/blocking — keep off UI thread.
        var text = await Task.Run(() => MyTransportEndpointsText.Build(u, _p2p.Settings, bt))
            .ConfigureAwait(true);
        await Clipboard.Default.SetTextAsync(text).ConfigureAwait(true);
        await DisplayAlert("Copied", "My addresses copied to clipboard.", "OK").ConfigureAwait(true);
    }

    private async void OnCopyKeysClicked(object? sender, EventArgs e)
    {
        var u = _auth.CurrentUser;
        if (u == null) return;
        var pub = IdentityKeySerializer.SerializePublic(_auth.GetCurrentPublicKey());
        var text = $"Network id: {u.NetworkIdShort}\nPublic key JSON:\n{pub}";
        await Clipboard.Default.SetTextAsync(text).ConfigureAwait(true);
        await DisplayAlert("Copied", "Network id and public key JSON copied to clipboard.", "OK").ConfigureAwait(true);
    }

    private async void OnExportProfileClicked(object? sender, EventArgs e)
    {
        var backup = MauiProgram.Services.GetRequiredService<ProfileBackupService>();
        await ProfileFileShare.ExportProfileAsync(this, _auth, backup, _logger).ConfigureAwait(true);
    }

    private async void OnChatSwipeDelete(object? sender, EventArgs e)
    {
        ChatEntity? chat = null;
        for (var p = sender as Element; p != null; p = p.Parent)
            if (p is SwipeView sw && sw.BindingContext is ChatListRowVm row)
            {
                chat = row.Chat;
                break;
            }

        if (chat == null)
            return;

        var u = _auth.CurrentUser;
        if (u == null)
            return;

        var confirm = await DisplayAlert("Delete chat",
            $"Remove «{chat.PeerNickname}» only on this device? All messages will be deleted.",
            "Delete",
            "Cancel").ConfigureAwait(true);
        if (!confirm)
            return;

        await _p2p.RemoveChatSessionAsync(chat.Id).ConfigureAwait(true);
        await _chats.DeleteChatAsync(chat.Id, u.Id).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        _chats.ChatListChanged -= OnChatListChangedFromInvite;
        _p2p.LocalScan.ClientsChanged -= OnLanPresenceChanged;
        if (_presenceRefreshTimer != null)
            _presenceRefreshTimer.Stop();
        try
        {
            await _p2p.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stop P2P on logout");
        }

        await _auth.LogoutAsync().ConfigureAwait(true);
        Application.Current!.MainPage = new NavigationPage(MauiProgram.Services.GetRequiredService<LoginPage>());
    }

    private async void OnChatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ChatListRowVm row)
            return;

        ChatsCollection.SelectedItem = null;
        var page = MauiProgram.Services.GetRequiredService<ChatDetailPage>();
        page.ChatId = row.Chat.Id;
        await Navigation.PushAsync(page).ConfigureAwait(true);
    }

    private static string TrimAbout(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        return text[..maxChars] + "…";
    }
}

public sealed class ChatListRowVm(ChatEntity chat, bool isPeerOnline) : INotifyPropertyChanged
{
    private bool _isPeerOnline = isPeerOnline;
    private ChatEntity _chat = chat;

    public ChatEntity Chat => _chat;

    public string PeerNickname => _chat.PeerNickname;

    public string PeerNetworkIdShort => _chat.PeerNetworkIdShort;

    public bool IsPeerOnline
    {
        get => _isPeerOnline;
        set
        {
            if (_isPeerOnline == value)
                return;
            _isPeerOnline = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPeerOnline)));
        }
    }

    public void ApplyChat(ChatEntity chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (chat.Id != _chat.Id)
            throw new ArgumentException("Chat id mismatch.", nameof(chat));

        var nickChanged = !string.Equals(_chat.PeerNickname, chat.PeerNickname, StringComparison.Ordinal);
        var idChanged = !string.Equals(_chat.PeerNetworkIdShort, chat.PeerNetworkIdShort, StringComparison.Ordinal);
        _chat = chat;
        if (nickChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeerNickname)));
        if (idChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PeerNetworkIdShort)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}