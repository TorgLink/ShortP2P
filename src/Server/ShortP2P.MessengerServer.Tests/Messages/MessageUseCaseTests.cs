using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.Tests.Fakes;
using ShortP2P.MessengerServer.UseCases;
using ShortP2P.MessengerServer.UseCases.Messages;

namespace ShortP2P.MessengerServer.Tests.Messages;

public class SendMessageUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WritesToCacheAndRepository_AndFansOut()
    {
        var h = new TestHarness();
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceB,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });

        var message = TestHarness.NewMessage("m1", "alice", "bob");
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(message));

        Assert.NotNull(await h.Stores.MessageCache.FindByIdAsync("m1"));
        Assert.True(h.Stores.Messages.ContainsKey("m1"));
        Assert.True(h.Stores.MessageInboxes.ContainsKey(("m1", TestIds.DeviceB)));
    }

    [Fact]
    public async Task ExecuteAsync_WhenDuplicateMessageId_IsIdempotent()
    {
        var h = new TestHarness();
        var message = TestHarness.NewMessage("m1", "alice", "bob");
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(message));
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob", ciphertext: "b3RoZXI=")));

        Assert.Equal("Y2lwaGVy", h.Stores.Messages["m1"].EncryptedDataBase64);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBothStoresDisabled_ThrowsValidation()
    {
        var h = new TestHarness();
        h.CacheOptions.CacheEnabled = false;
        h.CacheOptions.RepositoryEnabled = false;

        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.SendMessage().ExecuteAsync(new SendMessageCommand(
                TestHarness.NewMessage("m1", "alice", "bob"))));
        Assert.Equal("Validation", ex.Code);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBothStoresUnavailable_ThrowsUnavailable()
    {
        var h = new TestHarness();
        h.Stores.MessageCache.IsWriteAvailable = false;
        h.Stores.MessagesRepo.ThrowOnWrite = true;

        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.SendMessage().ExecuteAsync(new SendMessageCommand(
                TestHarness.NewMessage("m1", "alice", "bob"))));
        Assert.Equal("Unavailable", ex.Code);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCacheOnly_WritesOnlyCache()
    {
        var h = new TestHarness();
        h.CacheOptions.RepositoryEnabled = false;

        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob")));

        Assert.NotNull(await h.Stores.MessageCache.FindByIdAsync("m1"));
        Assert.Empty(h.Stores.Messages);
    }
}

public class SubmitDeliveryReceiptUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WhenRecipient_StoresTicketAndClearsInbox()
    {
        var h = new TestHarness();
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceB,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });

        var message = TestHarness.NewMessage("m1", "alice", "bob");
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(message));

        await h.SubmitDeliveryReceipt().ExecuteAsync(
            new SubmitDeliveryReceiptCommand("bob", TestIds.DeviceB, "m1", TestHarness.T0.AddSeconds(5)));

        Assert.True(h.Stores.DeliveryTickets.ContainsKey("m1"));
        Assert.False(h.Stores.MessageInboxes.ContainsKey(("m1", TestIds.DeviceB)));
        Assert.False(h.Stores.Messages.ContainsKey("m1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotRecipient_ThrowsUnauthorized()
    {
        var h = new TestHarness();
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob")));

        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.SubmitDeliveryReceipt().ExecuteAsync(
                new SubmitDeliveryReceiptCommand("alice", TestIds.DeviceA, "m1", TestHarness.T0)));
        Assert.Equal("Unauthorized", ex.Code);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMessageMissing_ThrowsNotFound()
    {
        var h = new TestHarness();
        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.SubmitDeliveryReceipt().ExecuteAsync(
                new SubmitDeliveryReceiptCommand("bob", TestIds.DeviceB, "missing", TestHarness.T0)));
        Assert.Equal("NotFound", ex.Code);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOtherDeviceStillHasCopy_KeepsMessage()
    {
        var h = new TestHarness();
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceB,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceC,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });

        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob")));

        await h.SubmitDeliveryReceipt().ExecuteAsync(
            new SubmitDeliveryReceiptCommand("bob", TestIds.DeviceB, "m1", TestHarness.T0));

        Assert.True(h.Stores.Messages.ContainsKey("m1"));
        Assert.True(h.Stores.MessageInboxes.ContainsKey(("m1", TestIds.DeviceC)));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotifySenderFalse_ClearsInboxWithoutTicket()
    {
        var h = new TestHarness();
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceB,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });

        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob")));

        await h.SubmitDeliveryReceipt().ExecuteAsync(
            new SubmitDeliveryReceiptCommand("bob", TestIds.DeviceB, "m1", TestHarness.T0, NotifySender: false));

        Assert.False(h.Stores.DeliveryTickets.ContainsKey("m1"));
        Assert.False(h.Stores.MessageInboxes.ContainsKey(("m1", TestIds.DeviceB)));
        Assert.False(h.Stores.Messages.ContainsKey("m1"));
    }
}

public class GetDeliveryReceiptsUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsAndRemovesTicketsForSender()
    {
        var h = new TestHarness();
        await h.Stores.StatusesRepo.UpsertAsync(new ClientStatuses
        {
            NetworkId = "bob",
            DeviceId = TestIds.DeviceB,
            Status = ClientOnlineStatus.Online,
            CreatedAtUtc = TestHarness.T0
        });
        await h.SendMessage().ExecuteAsync(new SendMessageCommand(
            TestHarness.NewMessage("m1", "alice", "bob")));
        await h.SubmitDeliveryReceipt().ExecuteAsync(
            new SubmitDeliveryReceiptCommand("bob", TestIds.DeviceB, "m1", TestHarness.T0));

        var tickets = await h.GetDeliveryReceipts().ExecuteAsync(
            new GetDeliveryReceiptsQuery("alice"));

        Assert.Single(tickets);
        Assert.Equal("m1", tickets[0].MessageId);
        Assert.Empty(await h.Stores.TicketCache.ListForSourceNetworkIdAsync("alice"));
        Assert.Empty(await h.Stores.TicketsRepo.ListForSourceNetworkIdAsync("alice"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerMissing_ThrowsValidation()
    {
        var h = new TestHarness();
        var ex = await Assert.ThrowsAsync<UseCaseException>(() =>
            h.GetDeliveryReceipts().ExecuteAsync(new GetDeliveryReceiptsQuery("")));
        Assert.Equal("Validation", ex.Code);
    }
}
