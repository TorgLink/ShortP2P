using ShortP2P.MessengerServer.Api.Hosting;
using ShortP2P.MessengerServer.Api.HostPowers;
using ShortP2P.MessengerServer.Infrastructure.BotRateLimit;
using ShortP2P.MessengerServer.UseCases.Abstractions;
using ShortP2P.MessengerServer.UseCases.Auth;
using ShortP2P.MessengerServer.UseCases.BotAccess;
using ShortP2P.MessengerServer.UseCases.BotCatalog;
using ShortP2P.MessengerServer.UseCases.BotClient;
using ShortP2P.MessengerServer.UseCases.BotRateLimit;
using ShortP2P.MessengerServer.UseCases.BotServer;
using ShortP2P.MessengerServer.UseCases.BotTrust;
using ShortP2P.MessengerServer.UseCases.Blobs;
using ShortP2P.MessengerServer.UseCases.Chats;
using ShortP2P.MessengerServer.UseCases.Forwards;
using ShortP2P.MessengerServer.UseCases.Hosting;
using ShortP2P.MessengerServer.UseCases.Inbox;
using ShortP2P.MessengerServer.UseCases.Messages;
using ShortP2P.MessengerServer.UseCases.Presence;
using ShortP2P.MessengerServer.UseCases.Server;
using ShortP2P.MessengerServer.UseCases.ServerTech;
using ShortP2P.MessengerServer.UseCases.Trust;

namespace ShortP2P.MessengerServer.Api.DependencyInjection;

public static class UseCasesServiceCollectionExtensions
{
    public static IServiceCollection AddMessengerUseCases(this IServiceCollection services)
    {
        services.AddSingleton<IInboxWaitService, InboxWaitService>();
        services.AddSingleton<IBotToClientWaitService, BotToClientWaitService>();
        services.AddSingleton<IClientToBotWaitService, ClientToBotWaitService>();
        services.AddSingleton<IForwardHub, InMemoryForwardHub>();
        services.AddSingleton<IHostHardwareInfoProvider, OsHostHardwareInfoProvider>();
        services.AddSingleton<IServerDiskSpace, DriveServerDiskSpace>();
        services.AddSingleton<IHostLoadInfoProvider, OsHostLoadInfoProvider>();
        services.AddSingleton<IBotRateLimiter, InMemoryBotRateLimiter>();
        services.AddScoped<HostPowersMeasurementService>();
        services.AddScoped<GetTotalPowerUseCase>();
        services.AddScoped<GetFreePowersUseCase>();
        services.AddScoped<DeviceFanoutService>();
        services.AddScoped<RegisterClientUseCase>();
        services.AddScoped<LoginClientUseCase>();
        services.AddScoped<ChangeVisibilityUseCase>();
        services.AddScoped<GetChatsUseCase>();
        services.AddScoped<CreateChatRequestUseCase>();
        services.AddScoped<SendMessageUseCase>();
        services.AddScoped<SubmitDeliveryReceiptUseCase>();
        services.AddScoped<GetDeliveryReceiptsUseCase>();
        services.AddScoped<PutBlobUseCase>();
        services.AddScoped<GetBlobUseCase>();
        services.AddScoped<DeleteBlobUseCase>();
        services.AddScoped<PutBlobChunkUseCase>();
        services.AddScoped<GetBlobChunkUseCase>();
        services.AddScoped<GetBlobChunkManifestUseCase>();
        services.AddScoped<ForwardPeerProfileUseCase>();
        services.AddScoped<PollInboxEventsUseCase>();
        services.AddScoped<BotAccessLogService>();
        services.AddScoped<BotTrustGuard>();
        services.AddScoped<BotRateLimitGuard>();
        services.AddScoped<RegisterBotUseCase>();
        services.AddScoped<LoginBotUseCase>();
        services.AddScoped<RemoveBotUseCase>();
        services.AddScoped<DeliverBotMessagesUseCase>();
        services.AddScoped<WaitForClientMessagesUseCase>();
        services.AddScoped<AcknowledgeClientMessageUseCase>();
        services.AddScoped<WaitForBotMessagesUseCase>();
        services.AddScoped<SendMessageToBotUseCase>();
        services.AddScoped<AcknowledgeBotMessageUseCase>();
        services.AddScoped<ListBotsUseCase>();
        services.AddScoped<GetBotVersionUseCase>();
        services.AddScoped<GetBotCommandsUseCase>();
        services.AddScoped<ClaimBotUseCase>();
        services.AddScoped<GetClientPresencesUseCase>();
        services.AddScoped<GetServerCertificateUseCase>();
        services.AddScoped<AskRatingUseCase>();
        services.AddScoped<AskServersUseCase>();
        services.AddScoped<ClaimServerUseCase>();
        services.AddHostedService<BotRpmMetricsHostedService>();
        services.AddHostedService<MessageRetentionHostedService>();
        services.AddHostedService<TrustRecoveryHostedService>();
        services.AddHostedService<BotTrustAntifraudHostedService>();
        services.AddHostedService<HostPowersMeasurementHostedService>();
        return services;
    }
}
