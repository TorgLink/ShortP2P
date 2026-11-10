namespace ShortP2P.Client.Services;

/// <summary>
/// When LoRa is the only usable outbound path, automatic retransmission is limited to
/// messages younger than <see cref="AutoRetryMaxAge"/>. Older Pending/Failed rows must be
/// retried only by an explicit user click (UI shows "!").
/// </summary>
public static class LoRaOutboundRetryPolicy
{
    public static readonly TimeSpan AutoRetryMaxAge = TimeSpan.FromMinutes(1);

    public static bool IsOlderThanAutoRetryWindow(long sentUtcTicks, DateTime utcNow)
    {
        if (sentUtcTicks <= 0)
            return true;
        var sentUtc = new DateTime(sentUtcTicks, DateTimeKind.Utc);
        return utcNow - sentUtc > AutoRetryMaxAge;
    }
}
