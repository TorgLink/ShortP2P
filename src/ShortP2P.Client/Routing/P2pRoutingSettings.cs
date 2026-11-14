namespace ShortP2P.Discovery;

/// <summary>
///     Параметры поиска по графу и повторов при ошибке отправки (настраиваются через
///     <see cref="P2pRoutingSettingsStore" />).
/// </summary>
public sealed class P2pRoutingSettings
{
    /// <summary>Максимальная глубина поиска (число рёбер от инициатора), 1–3.</summary>
    public int MaxSearchHops { get; set; } = 3;

    /// <summary>Сколько раз повторить полный поиск при неудаче отправки.</summary>
    public int SendFailureSearchAttempts { get; set; } = 3;

    /// <summary>Пауза между попытками поиска/отправки.</summary>
    public TimeSpan SendFailureRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Ожидание ответа на FIND.</summary>
    public TimeSpan SearchWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Пресет скорости для симуляции канала (data UDP / Bluetooth), не влияет на порт presence.</summary>
    public LinkTechnologyPreset LinkTechnology { get; set; } = LinkTechnologyPreset.Unlimited;

    /// <summary>Разрешить UDP-транспорт (передача и приём).</summary>
    public bool EnableUdpTransport { get; set; } = true;

    /// <summary>Разрешить Bluetooth-транспорт (передача и приём).</summary>
    public bool EnableBluetoothTransport { get; set; } = true;

    /// <summary>
    ///     WinRT <c>DeviceInformation.Id</c> выбранного радио (пусто — системный адаптер по умолчанию).
    ///     Должен совпадать с адаптером, через который поднят GATT (см. Routing → перезапуск после смены).
    /// </summary>
    public string? SelectedBluetoothAdapterDeviceId { get; set; }

    /// <summary>Канонический MAC выбранного адаптера (AA:BB:CC:DD:EE:FF) для контактов.</summary>
    public string? SelectedBluetoothAdapterMac { get; set; }

    /// <summary>Показывать предложение открыть системное сопряжение Bluetooth при недоступном BT-пире.</summary>
    public bool SuggestBluetoothPairing { get; set; }

    /// <summary>Режим качества медиа и частоты presence-пингов.</summary>
    public TrafficQualityMode TrafficQuality { get; set; } = TrafficQualityMode.Normal;

    /// <summary>
    ///     Возможности, заявляемые в presence-пинге (всегда включается <see cref="PresencePeerCapabilities.Chat" /> в кодеке).
    ///     <see cref="PresencePeerCapabilities.PeerSearch" /> — ответ на UDP-запрос маршрутной таблицы.
    /// </summary>
    public PresencePeerCapabilities AdvertisedPeerCapabilities { get; set; } = PresencePeerCapabilities.Chat;

    /// <summary>
    ///     Видимость в поиске / каталоге GetClients. По умолчанию включена.
    ///     При false: не отвечаем на discovery и не шлём presence ping; инвайты и прочий трафик не затрагиваются.
    /// </summary>
    public bool VisibleForSearch { get; set; } = true;
}