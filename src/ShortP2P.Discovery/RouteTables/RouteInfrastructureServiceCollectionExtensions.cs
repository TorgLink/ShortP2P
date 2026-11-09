using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShortP2P.Discovery.Pings;
using ShortP2P.Discovery.Strategies;

namespace ShortP2P.Discovery.RouteTables;

public static class RouteInfrastructureServiceCollectionExtensions
{
    /// <summary>
    ///     Применяет накопленные EF-миграции для <see cref="RouteDbContext" /> (SQLite).
    ///     Вызывайте при старте клиента (WinForms, MAUI) до фоновых сервисов и до чтения маршрутов.
    /// </summary>
    public static async Task ApplyRouteDatabaseMigrationsAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<RouteDbContext>>()
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var connectionString = db.Database.GetConnectionString();
        if (!string.IsNullOrEmpty(connectionString))
            EnsureSqliteParentDirectoryExists(connectionString);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("ShortP2P.RouteDb");

        // Skip MigrateAsync when already up to date. A stale __EFMigrationsLock row from a
        // crashed prior start makes MigrateAsync retry INSERT OR IGNORE every ~1s forever.
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToArray();
        if (pending.Length == 0)
        {
            await TryClearSqliteMigrationsLockAsync(db, logger, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Route database migrations already applied");
            return;
        }

        // MAUI / desktop is single-process for this DB — clear an abandoned advisory lock first.
        await TryClearSqliteMigrationsLockAsync(db, logger, cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation("Route database migrations applied ({Count} pending)", pending.Length);
    }

    private static async Task TryClearSqliteMigrationsLockAsync(
        RouteDbContext db, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                    """DELETE FROM "__EFMigrationsLock";""",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Table may not exist yet on first run.
            logger?.LogDebug(ex, "Could not clear EF migrations lock (ok on first run)");
        }
    }

    private static void EnsureSqliteParentDirectoryExists(string connectionString)
    {
        const string prefix = "Data Source=";
        if (!connectionString.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;

        var path = connectionString[prefix.Length..].Trim();
        if (path.Length == 0 || string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase))
            return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <param name="sqliteDatabasePath"></param>
    /// <param name="enableDiscovery">
    ///     <see langword="true" /> — зарегистрировать фоновый сервис (см. <see cref="IHostedService" />) для MAUI / generic
    ///     host.
    ///     <see langword="false" /> — только контекст и опции; затем вызовите
    ///     <see cref="StartRoutePeerRoutesExpiryCleanupDetached" />.
    /// </param>
    /// <param name="services"></param>
    public static IServiceCollection AddRouteDbContextWithPeerExpiryCleanup(
        this IServiceCollection services,
        string sqliteDatabasePath,
        bool enableDiscovery = true,
        Action<RoutePeerRoutesExpiryOptions>? configureExpiry = null)
    {
        var dbPath = $"Data Source={sqliteDatabasePath}";
        services.AddDbContext<RouteDbContext>(o => o.UseSqlite(dbPath));
        services.AddDbContextFactory<RouteDbContext>(o => o.UseSqlite(dbPath));
        services.AddSingleton<IRouteTableSnapshotSource, EfRouteTableSnapshotSource>();
        services.AddSingleton<IDiscoveryPingStore, InMemoryDiscoveryPingStore>();
        services.AddSingleton<IDiscoveryStrategy, GossipStrategy>();
        services.AddSingleton(_ =>
        {
            var options = new RoutePeerRoutesExpiryOptions();
            configureExpiry?.Invoke(options);
            return options;
        });

        if (enableDiscovery)
            //services.AddHostedService<RoutePeerRoutesExpiryCleanupHostedService>();
            services.AddHostedService<DiscoveryRoutesUpdateHostedService>();

        return services;
    }

    /// <summary>
    ///     Старт цикла очистки для приложений без host (например WinForms).
    /// </summary>
    public static void StartRoutePeerRoutesExpiryCleanupDetached(this IServiceProvider rootProvider)
    {
        var options = rootProvider.GetRequiredService<RoutePeerRoutesExpiryOptions>();
        var loggerFactory = rootProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("ShortP2P.RoutePeerRoutesExpiry");
        var routeSyncLogger = loggerFactory.CreateLogger("ShortP2P.RouteSync");

        var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        RoutePeerRoutesExpiryCleanup.StartDetached(rootProvider, options, logger, cts.Token);
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var scope = rootProvider.CreateScope();
                    var strategies = scope.ServiceProvider.GetServices<IDiscoveryStrategy>();
                    foreach (var strategy in strategies)
                        await strategy.UpdateRoutesAsync(cancellationToken: cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    routeSyncLogger.LogError(ex, "Detached route sync tick failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }
}