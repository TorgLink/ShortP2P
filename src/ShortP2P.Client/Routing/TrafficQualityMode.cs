namespace ShortP2P.Discovery;

/// <summary>Режим качества медиа и частоты presence-пингов (экономия трафика).</summary>
public enum TrafficQualityMode
{
    /// <summary>Голос 24 kbit/s, видео 480p, presence раз в 1 с.</summary>
    Normal = 0,

    /// <summary>Голос 6 kbit/s, видео 240p, presence раз в 10 с.</summary>
    Economy = 1,

    /// <summary>Голос 4 kbit/s, видео 144p, presence раз в 10 с.</summary>
    UltraEconomy = 2
}

public static class TrafficQualityModeExtensions
{
    public const int NormalVoiceBitrate = 24_000;
    public const int EconomyVoiceBitrate = 6_000;
    public const int UltraEconomyVoiceBitrate = 4_000;

    public const int NormalVideoWidth = 854;
    public const int NormalVideoHeight = 480;
    /// <summary>H.264-friendly width (multiple of 16). Was 426; MediaTranscoder often rejects non-aligned sizes.</summary>
    public const int EconomyVideoWidth = 432;
    public const int EconomyVideoHeight = 240;
    public const int UltraEconomyVideoWidth = 256;
    public const int UltraEconomyVideoHeight = 144;

    public const int NormalCameraVideoBitrate = 700_000;
    public const int EconomyCameraVideoBitrate = 250_000;
    public const int UltraEconomyCameraVideoBitrate = 120_000;

    public static int GetVoiceBitrate(this TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.UltraEconomy => UltraEconomyVoiceBitrate,
            TrafficQualityMode.Economy => EconomyVoiceBitrate,
            _ => NormalVoiceBitrate
        };
    }

    public static (int Width, int Height) GetVideoResolution(this TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.UltraEconomy => (UltraEconomyVideoWidth, UltraEconomyVideoHeight),
            TrafficQualityMode.Economy => (EconomyVideoWidth, EconomyVideoHeight),
            _ => (NormalVideoWidth, NormalVideoHeight)
        };
    }

    public static int GetCameraVideoBitrate(this TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.UltraEconomy => UltraEconomyCameraVideoBitrate,
            TrafficQualityMode.Economy => EconomyCameraVideoBitrate,
            _ => NormalCameraVideoBitrate
        };
    }

    /// <summary>Экономия и ультраэкономия — реже presence-пинги.</summary>
    public static bool UsesReducedPresencePing(this TrafficQualityMode mode)
    {
        return mode is TrafficQualityMode.Economy or TrafficQualityMode.UltraEconomy;
    }

    public static string GetDisplayLabel(this TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.UltraEconomy => "Ультраэкономия (голос 4 kbit/s, видео 144p)",
            TrafficQualityMode.Economy => "Экономия (голос 6 kbit/s, видео 240p)",
            _ => "Нормальный (голос 24 kbit/s, видео 480p)"
        };
    }

    public static bool TryParse(string? raw, out TrafficQualityMode mode)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out mode) && Enum.IsDefined(typeof(TrafficQualityMode), mode))
            return true;
        mode = TrafficQualityMode.Normal;
        return false;
    }

    /// <summary>Миграция со старого bool: true → Economy, false → Normal.</summary>
    public static TrafficQualityMode FromLegacyTrafficSavingEnabled(bool enabled)
    {
        return enabled ? TrafficQualityMode.Economy : TrafficQualityMode.Normal;
    }
}
