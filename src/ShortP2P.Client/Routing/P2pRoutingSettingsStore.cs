using ShortP2P.Auth;
using ShortP2P.Discovery;

namespace ShortP2P.Client.Routing;

/// <summary>Хранит настройки маршрутизации в <see cref="ISessionStorage" />.</summary>
public sealed class P2pRoutingSettingsStore(ISessionStorage storage)
{
    private const string KMaxHops = "p2p_route_max_hops";
    private const string KAttempts = "p2p_send_search_attempts";
    private const string KDelayMs = "p2p_send_retry_delay_ms";
    private const string KSearchTimeoutMs = "p2p_search_timeout_ms";
    private const string KLinkTechnology = "p2p_link_technology";
    private const string KEnableUdpTransport = "p2p_transport_udp_enabled";
    private const string KEnableBluetoothTransport = "p2p_transport_bluetooth_enabled";
    private const string KBluetoothAdapterDeviceId = "p2p_bluetooth_adapter_device_id";
    private const string KBluetoothAdapterMac = "p2p_bluetooth_adapter_mac";
    private const string KBluetoothPairingPrompt = "p2p_bluetooth_pairing_prompt";
    private const string KTrafficQuality = "p2p_traffic_quality";
    private const string KTrafficSavingEnabledLegacy = "p2p_traffic_saving_enabled";
    private const string KAdvertisedCaps = "p2p_advertised_caps";
    private const string KVisibleForSearch = "p2p_visible_for_search";

    private readonly ISessionStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    public async Task<P2pRoutingSettings> LoadAsync()
    {
        var s = new P2pRoutingSettings();
        if (int.TryParse(await _storage.GetAsync(KMaxHops).ConfigureAwait(false), out var mh) && mh is >= 1 and <= 3)
            s.MaxSearchHops = mh;
        if (int.TryParse(await _storage.GetAsync(KAttempts).ConfigureAwait(false), out var at) && at >= 1)
            s.SendFailureSearchAttempts = at;
        if (int.TryParse(await _storage.GetAsync(KDelayMs).ConfigureAwait(false), out var dm) && dm >= 0)
            s.SendFailureRetryDelay = TimeSpan.FromMilliseconds(dm);
        if (int.TryParse(await _storage.GetAsync(KSearchTimeoutMs).ConfigureAwait(false), out var st) && st >= 500)
            s.SearchWaitTimeout = TimeSpan.FromMilliseconds(st);
        if (int.TryParse(await _storage.GetAsync(KLinkTechnology).ConfigureAwait(false), out var lt) &&
            Enum.IsDefined(typeof(LinkTechnologyPreset), lt))
            s.LinkTechnology = (LinkTechnologyPreset)lt;
        if (bool.TryParse(await _storage.GetAsync(KEnableUdpTransport).ConfigureAwait(false), out var udp))
            s.EnableUdpTransport = udp;
        if (bool.TryParse(await _storage.GetAsync(KEnableBluetoothTransport).ConfigureAwait(false), out var bt))
            s.EnableBluetoothTransport = bt;
        s.SelectedBluetoothAdapterDeviceId =
            NullIfWhiteSpace(await _storage.GetAsync(KBluetoothAdapterDeviceId).ConfigureAwait(false));
        s.SelectedBluetoothAdapterMac =
            NullIfWhiteSpace(await _storage.GetAsync(KBluetoothAdapterMac).ConfigureAwait(false));
        if (bool.TryParse(await _storage.GetAsync(KBluetoothPairingPrompt).ConfigureAwait(false), out var bp))
            s.SuggestBluetoothPairing = bp;
        s.TrafficQuality = await LoadTrafficQualityAsync().ConfigureAwait(false);
        if (int.TryParse(await _storage.GetAsync(KAdvertisedCaps).ConfigureAwait(false), out var capsRaw) &&
            capsRaw is >= 0 and <= ushort.MaxValue)
        {
            var masked = (PresencePeerCapabilities)((ushort)capsRaw & (ushort)PresencePeerCapabilities.AllDefined);
            s.AdvertisedPeerCapabilities = masked | PresencePeerCapabilities.Chat;
        }

        // Missing key → default true (visible).
        var visibleRaw = await _storage.GetAsync(KVisibleForSearch).ConfigureAwait(false);
        if (bool.TryParse(visibleRaw, out var visible))
            s.VisibleForSearch = visible;
        else
            s.VisibleForSearch = true;

        return s;
    }

    private async Task<TrafficQualityMode> LoadTrafficQualityAsync()
    {
        var raw = await _storage.GetAsync(KTrafficQuality).ConfigureAwait(false);
        if (TrafficQualityModeExtensions.TryParse(raw, out var mode))
            return mode;
        if (bool.TryParse(await _storage.GetAsync(KTrafficSavingEnabledLegacy).ConfigureAwait(false), out var ts))
            return TrafficQualityModeExtensions.FromLegacyTrafficSavingEnabled(ts);
        return TrafficQualityMode.Normal;
    }

    private static string? NullIfWhiteSpace(string? s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s))
            return null;
        return s.Trim();
    }

    public async Task SaveAsync(P2pRoutingSettings settings)
    {
        await _storage.SetAsync(KMaxHops, settings.MaxSearchHops.ToString()).ConfigureAwait(false);
        await _storage.SetAsync(KAttempts, settings.SendFailureSearchAttempts.ToString()).ConfigureAwait(false);
        await _storage.SetAsync(KDelayMs, ((int)settings.SendFailureRetryDelay.TotalMilliseconds).ToString())
            .ConfigureAwait(false);
        await _storage.SetAsync(KSearchTimeoutMs, ((int)settings.SearchWaitTimeout.TotalMilliseconds).ToString())
            .ConfigureAwait(false);
        await _storage.SetAsync(KLinkTechnology, ((int)settings.LinkTechnology).ToString()).ConfigureAwait(false);
        await _storage.SetAsync(KEnableUdpTransport, settings.EnableUdpTransport.ToString()).ConfigureAwait(false);
        await _storage.SetAsync(KEnableBluetoothTransport, settings.EnableBluetoothTransport.ToString())
            .ConfigureAwait(false);
        await _storage.SetAsync(KBluetoothAdapterDeviceId, settings.SelectedBluetoothAdapterDeviceId ?? string.Empty)
            .ConfigureAwait(false);
        await _storage.SetAsync(KBluetoothAdapterMac, settings.SelectedBluetoothAdapterMac ?? string.Empty)
            .ConfigureAwait(false);
        await _storage.SetAsync(KBluetoothPairingPrompt, settings.SuggestBluetoothPairing.ToString())
            .ConfigureAwait(false);
        await _storage.SetAsync(KTrafficQuality, settings.TrafficQuality.ToString()).ConfigureAwait(false);
        var caps = settings.AdvertisedPeerCapabilities | PresencePeerCapabilities.Chat;
        await _storage.SetAsync(KAdvertisedCaps, ((ushort)caps).ToString()).ConfigureAwait(false);
        await _storage.SetAsync(KVisibleForSearch, settings.VisibleForSearch.ToString()).ConfigureAwait(false);
    }
}
