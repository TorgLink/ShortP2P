using ShortP2P.MessengerServer.Tests.Fakes;
using ShortP2P.MessengerServer.UseCases;
using ShortP2P.MessengerServer.UseCases.Auth;

namespace ShortP2P.MessengerServer.Tests.Auth;

public sealed class ChangeVisibilityUseCaseTests
{
    [Fact]
    public async Task ChangeVisibility_UpdatesStoredFlag()
    {
        var h = new TestHarness();
        await h.RegisterClient().ExecuteAsync(new RegisterClientCommand("Alice", "net-a", "secret", TestIds.DeviceA));

        await h.ChangeVisibility().ExecuteAsync(new ChangeVisibilityCommand("net-a", false));

        var account = Assert.Single(h.Stores.Accounts.Values);
        Assert.False(account.VisibleForSearch);
    }

    [Fact]
    public async Task GetClients_IncludesVisibleForSearchFlag()
    {
        var h = new TestHarness();
        await h.RegisterClient().ExecuteAsync(
            new RegisterClientCommand("Alice", "net-a", "secret", TestIds.DeviceA, VisibleForSearch: false));

        var list = await h.GetClientPresences().ExecuteAsync();
        var alice = Assert.Single(list);
        Assert.False(alice.VisibleForSearch);
    }

    [Fact]
    public async Task ChangeVisibility_WhenMissing_ThrowsNotFound()
    {
        var h = new TestHarness();
        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.ChangeVisibility().ExecuteAsync(new ChangeVisibilityCommand("missing", false)));
        Assert.Equal("NotFound", ex.Code);
    }
}
