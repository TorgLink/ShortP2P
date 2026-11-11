using ShortP2P.Client.Services;

namespace ShortP2P.Messenger.Tests;

public class LoRaOutboundRetryPolicyTests
{
    [Fact]
    public void IsOlderThanAutoRetryWindow_WithinOneMinute_IsFalse()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var sent = now.AddSeconds(-59).Ticks;
        Assert.False(LoRaOutboundRetryPolicy.IsOlderThanAutoRetryWindow(sent, now));
    }

    [Fact]
    public void IsOlderThanAutoRetryWindow_OverOneMinute_IsTrue()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var sent = now.AddSeconds(-61).Ticks;
        Assert.True(LoRaOutboundRetryPolicy.IsOlderThanAutoRetryWindow(sent, now));
    }

    [Fact]
    public void IsOlderThanAutoRetryWindow_MissingTimestamp_IsTrue()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(LoRaOutboundRetryPolicy.IsOlderThanAutoRetryWindow(0, now));
    }

    [Fact]
    public void AutoRetryMaxAge_IsOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), LoRaOutboundRetryPolicy.AutoRetryMaxAge);
    }
}
