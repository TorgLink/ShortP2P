using ShortP2P.Client.Services;

namespace ShortP2P.Client.Tests;

public class PeerMlKemAdvertisePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void True_always_applies()
    {
        Assert.True(PeerMlKemAdvertisePolicy.ShouldApply(
            wants: true,
            currentlyAdvertises: false,
            liveP2pCryptoPath: false,
            utcNow: T0,
            lastTrueUtc: default));
    }

    [Fact]
    public void False_ignored_when_never_latched_true()
    {
        Assert.False(PeerMlKemAdvertisePolicy.ShouldApply(
            wants: false,
            currentlyAdvertises: false,
            liveP2pCryptoPath: false,
            utcNow: T0,
            lastTrueUtc: default));
    }

    [Fact]
    public void False_ignored_soon_after_true_without_live_p2p()
    {
        Assert.False(PeerMlKemAdvertisePolicy.ShouldApply(
            wants: false,
            currentlyAdvertises: true,
            liveP2pCryptoPath: false,
            utcNow: T0.AddSeconds(5),
            lastTrueUtc: T0));
    }

    [Fact]
    public void False_accepted_after_grace_without_live_p2p()
    {
        Assert.True(PeerMlKemAdvertisePolicy.ShouldApply(
            wants: false,
            currentlyAdvertises: true,
            liveP2pCryptoPath: false,
            utcNow: T0 + PeerMlKemAdvertisePolicy.StaleFalseGrace + TimeSpan.FromSeconds(1),
            lastTrueUtc: T0));
    }

    [Fact]
    public void False_accepted_immediately_on_live_p2p_path()
    {
        Assert.True(PeerMlKemAdvertisePolicy.ShouldApply(
            wants: false,
            currentlyAdvertises: true,
            liveP2pCryptoPath: true,
            utcNow: T0.AddSeconds(1),
            lastTrueUtc: T0));
    }
}
