using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using ShortP2P.Auth;
using ShortP2P.Crypto;
using ShortP2P.Client;
using ShortP2P.Client.Bluetooth;
using ShortP2P.Client.ChatMedia;
using ShortP2P.Client.ChatMedia.Vocabulary;
using ShortP2P.Client.Data;
using ShortP2P.Client.ProfileBackup;
using ShortP2P.Client.Routing;
using ShortP2P.Client.Services;
using ShortP2P.Client.Services.MessengerServers;
using ShortP2P.Discovery;
using ShortP2P.Discovery.Ble;
using ShortP2P.Discovery.Pings;
using ShortP2P.Discovery.Profile;
using ShortP2P.Discovery.RouteTables;
using ShortP2P.Transport;
using ShortP2P.Transport.Abstractions;
using ShortP2P.Transport.Bluetooth.Windows;
using SQLitePCL;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ShortP2P.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ConfigureNLog();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddNLog();

        var services = builder.Services;
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShortP2P", "WinForms");
        services.AddSingleton(_ => ChatMediaOptions.LoadOrDefault(Path.Combine(appRoot, "chat-media.json")));
        services.AddSingleton(_ => new AppDatabase(Path.Combine(appRoot, "shortp2p.db")));
        services.AddSingleton<IUserAuthRepository, SqliteUserAuthRepository>();
        services.AddRouteDbContextWithPeerExpiryCleanup(Path.Combine(appRoot, "routes.db"), true);
        services.AddSingleton<ISessionStorage>(_ => new FileSessionStorage(Path.Combine(appRoot, "session")));
        services.AddSingleton<AuthService>();
        services.AddSingleton<PeerBlacklist>();
        services.AddSingleton<ChatRepository>();
        services.AddSingleton<IBluetoothPresencePingTargetsProvider, BluetoothPresencePingTargetsProvider>();
        services.AddSingleton<IBleDiscoveredPeerStore, SqliteBleDiscoveredPeerStore>();
        services.AddSingleton<IPeerProfileStore, SqlitePeerProfileStore>();
        services.AddSingleton<ILocalPeerProfileSource, AuthLocalPeerProfileSource>();
        services.AddSingleton<P2pRoutingSettingsStore>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<BluetoothTransportRegistration>();
        services.AddSingleton<IBluetoothTransportProvider>(sp =>
            sp.GetRequiredService<BluetoothTransportRegistration>());
        services.AddSingleton<IBluetoothRadioCatalog, WindowsBluetoothRadioCatalog>();
        services.AddSingleton<IBleShortP2PPeripheralScanner>(sp =>
            sp.GetRequiredService<BluetoothTransportRegistration>().PeripheralScanner);
        services.AddSingleton<IUdpTransportFactory, UdpTransportFactory>();
        services.AddSingleton<ChatSessionCache>();
        services.AddSingleton<P2pCryptoSessionCache>();
        services.AddSingleton<IMessengerServerRepository, SqliteMessengerServerRepository>();
        services.AddSingleton<IBotVersionRepository, SqliteBotVersionRepository>();
        services.AddSingleton<ProfileBackupService>();
        services.AddSingleton<DeviceIdProvider>();
        services.AddSingleton<MessengerServerManager>();
        services.AddSingleton<MessengerServerSyncService>();
        services.AddSingleton(sp => new UserP2pRuntime(
            sp.GetRequiredService<P2pRoutingSettingsStore>(),
            sp.GetRequiredService<AuthService>(),
            sp.GetRequiredService<ChatRepository>(),
            sp.GetRequiredService<ChatMediaOptions>(),
            sp.GetRequiredService<IUdpTransportFactory>(),
            sp.GetRequiredService<ChatSessionCache>(),
            sp.GetRequiredService<P2pCryptoSessionCache>(),
            sp.GetRequiredService<IBluetoothTransportProvider>(),
            null,
            sp.GetService<IRouteTableSnapshotSource>(),
            sp.GetService<IDiscoveryPingStore>(),
            sp.GetRequiredService<IBleShortP2PPeripheralScanner>(),
            sp.GetRequiredService<IBleDiscoveredPeerStore>(),
            sp.GetRequiredService<IBluetoothPresencePingTargetsProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<MessengerServerSyncService>(),
            sp.GetRequiredService<IPeerProfileStore>(),
            sp.GetRequiredService<ILocalPeerProfileSource>()));

        services.AddTransient<LoginForm>();
        services.AddTransient<RegisterForm>();
        services.AddTransient<MainChatsForm>();
        services.AddTransient<AddChatForm>();
        services.AddTransient<MyQrForm>();
        services.AddTransient<RoutingSettingsForm>();
        services.AddTransient<MessengerServersForm>();
        services.AddTransient<AppSettingsForm>();
        services.AddTransient<ProfileForm>();

        using var host = builder.Build();
        host.Services.ApplyRouteDatabaseMigrationsAsync().GetAwaiter().GetResult();
        host.StartAsync().GetAwaiter().GetResult();

        P2PSession.TrafficLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<P2PSession>();

        var provider = host.Services;
        var hostLogger = provider.GetRequiredService<ILogger<WinFormsHost>>();
        var userActionLogger = provider.GetRequiredService<ILogger<UserAction>>();
        RegisterGlobalExceptionLogging(hostLogger);

        Batteries_V2.Init();
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point));
        hostLogger.LogInformation("WinForms application started");

        // SMT-3: load ShortP2P.Vocabulary packs (submodule / sibling repo) once at startup.
        try
        {
            VocabularyHost.TryInitializeNearApp(Path.Combine(appRoot, "vocabularies"), hostLogger);
        }
        catch (Exception ex)
        {
            hostLogger.LogWarning(ex, "VocabularyHost initialization failed");
        }

        var appSettings = provider.GetRequiredService<AppSettingsStore>();
        try
        {
            appSettings.InitializeAsync().GetAwaiter().GetResult();
            while (true)
            {
                using var login = provider.GetRequiredService<LoginForm>();
                if (login.ShowDialog() != DialogResult.OK)
                {
                    userActionLogger.LogInformation("Login: closed without signing in");
                    return;
                }

                using var main = provider.GetRequiredService<MainChatsForm>();
                var mainResult = main.ShowDialog();
                if (mainResult != DialogResult.Retry)
                {
                    userActionLogger.LogInformation("Main: closed (dialog result {Result})", mainResult);
                    return;
                }
            }
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
            LogManager.Shutdown();
        }
    }

    private static void ConfigureNLog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "nlog.config");
        LogManager.Configuration = new XmlLoggingConfiguration(path);
    }

    private static void RegisterGlobalExceptionLogging(ILogger<WinFormsHost> logger)
    {
        Application.ThreadException += (_, args) =>
            logger.LogError(args.Exception, "Unhandled UI thread exception");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                logger.LogCritical(ex, "Unhandled domain exception");
            else
                logger.LogCritical("Unhandled domain exception (non-CLR): {Data}", args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }
}