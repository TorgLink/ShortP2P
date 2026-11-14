using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShortP2P.MessengerServer.Domain;
using ShortP2P.MessengerServer.UseCases.Abstractions;
using ShortP2P.MessengerServer.UseCases.Auth;
using ShortP2P.MessengerServer.UseCases.Blobs;
using ShortP2P.MessengerServer.UseCases.Chats;
using ShortP2P.MessengerServer.UseCases.Forwards;
using ShortP2P.MessengerServer.UseCases.Hosting;
using ShortP2P.MessengerServer.UseCases.Inbox;
using ShortP2P.MessengerServer.UseCases.Messages;
using ShortP2P.MessengerServer.UseCases.Presence;
using ShortP2P.MessengerServer.UseCases.Server;
using ShortP2P.MessengerServer.UseCases.ServerTech;

namespace ShortP2P.MessengerServer.Tests.Fakes;

internal sealed class TestHarness
{
    public static readonly DateTime T0 = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    public FakeClock Clock { get; } = new(T0);
    public FakeStores Stores { get; } = new();
    public FakePasswordHasher PasswordHasher { get; } = new();
    public FakeAuthTokenService TokenService { get; }
    public MessengerCacheOptions CacheOptions { get; } = new() { CacheEnabled = true, RepositoryEnabled = true };
    public MessengerInboxOptions InboxOptions { get; } = new() { MaxPollTimeoutSeconds = 30, MessageRetention = TimeSpan.FromDays(30) };
    public InboxWaitService InboxWait { get; } = new();
    public InMemoryForwardHub ForwardHub { get; } = new();
    public DeviceFanoutService Fanout { get; }

    public TestHarness()
    {
        TokenService = new FakeAuthTokenService(Clock);
        Fanout = new DeviceFanoutService(
            Stores.StatusesRepo,
            Stores.MessagesRepo,
            Stores.MessageInboxRepo,
            Stores.MessageCache,
            Stores.MessageInboxCache,
            Stores.ChatRequestsRepo,
            CacheOptions,
            Options.Create(InboxOptions),
            Clock);
    }

    public RegisterClientUseCase RegisterClient() =>
        new(Stores.AccountsRepo, Stores.StatusesRepo, PasswordHasher, Clock);

    public ChangeVisibilityUseCase ChangeVisibility() =>
        new(Stores.AccountsRepo);

    public LoginClientUseCase LoginClient() =>
        new(Stores.AccountsRepo, Stores.StatusesRepo, PasswordHasher, TokenService, Fanout, Clock);

    public CreateChatRequestUseCase CreateChatRequest() =>
        new(Stores.ChatsRepo, Stores.ChatRequestsRepo, Stores.CryptoKeysRepo, Fanout, InboxWait, Clock);

    public GetChatsUseCase GetChats() => new(Stores.ChatsRepo);

    public SendMessageUseCase SendMessage() =>
        new(Stores.MessagesRepo, Stores.MessageCache, CacheOptions, Fanout, InboxWait);

    public SubmitDeliveryReceiptUseCase SubmitDeliveryReceipt() =>
        new(
            Stores.MessagesRepo,
            Stores.MessageCache,
            Stores.MessageInboxRepo,
            Stores.MessageInboxCache,
            Stores.TicketsRepo,
            Stores.TicketCache,
            CacheOptions);

    public GetDeliveryReceiptsUseCase GetDeliveryReceipts() =>
        new(Stores.TicketsRepo, Stores.TicketCache, CacheOptions);

    public GetClientPresencesUseCase GetClientPresences() =>
        new(Stores.AccountsRepo, Stores.StatusesRepo, Clock, Options.Create(InboxOptions));

    public PutBlobUseCase PutBlob() => new(Stores.BlobsRepo, Clock);

    public GetBlobUseCase GetBlob() => new(Stores.BlobsRepo);

    public DeleteBlobUseCase DeleteBlob() => new(Stores.BlobsRepo);

    public PollInboxEventsUseCase PollInbox() =>
        new(
            Stores.MessagesRepo,
            Stores.MessageCache,
            Stores.MessageInboxRepo,
            Stores.MessageInboxCache,
            Stores.ChatRequestsRepo,
            ForwardHub,
            InboxWait,
            CacheOptions,
            Options.Create(InboxOptions),
            Clock);

    public ForwardPeerProfileUseCase ForwardPeerProfile() =>
        new(
            ForwardHub,
            Stores.StatusesRepo,
            InboxWait,
            Clock,
            Options.Create(InboxOptions),
            NullLogger<ForwardPeerProfileUseCase>.Instance);

    public GetServerCertificateUseCase GetServerCertificate(IServerCertificateReader? reader = null) =>
        new(reader ?? new FakeServerCertificateReader());

    public static Message NewMessage(
        string id,
        string src,
        string tgt,
        DateTime? created = null,
        string ciphertext = "Y2lwaGVy") =>
        new()
        {
            MessageId = id,
            SrcNetworkId = src,
            TgtNetworkId = tgt,
            CreatedUtc = created ?? T0,
            UpdatedUtc = created ?? T0,
            EncryptedDataBase64 = ciphertext
        };
}
