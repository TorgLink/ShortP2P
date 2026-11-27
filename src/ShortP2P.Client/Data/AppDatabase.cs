using ShortP2P.Auth.Data;
using SQLite;

namespace ShortP2P.Client.Data;

/// <summary>
/// Local SQLite store with WAL and a small read-connection pool.
/// One shared <see cref="SQLiteAsyncConnection"/> serializes everything on its internal lock;
/// separate reader connections allow SELECTs to proceed while a writer inserts blobs.
/// </summary>
public sealed class AppDatabase
{
    public const int DefaultReadPoolSize = 4;

    private readonly string _databasePath;
    private readonly int _readPoolSize;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private SQLiteAsyncConnection? _write;
    private SQLiteAsyncConnection[]? _readers;
    private int _readerCursor;
    private bool _initialized;

    public AppDatabase(string databasePath, int readPoolSize = DefaultReadPoolSize)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _readPoolSize = Math.Max(1, Math.Min(readPoolSize, 16));
    }

    /// <summary>Write connection (Insert/Update/Delete). Prefer <see cref="WriteAsync{T}"/> for exclusive writers.</summary>
    public async Task<SQLiteAsyncConnection> GetConnectionAsync() =>
        await GetWriteConnectionAsync().ConfigureAwait(false);

    public async Task<SQLiteAsyncConnection> GetWriteConnectionAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return _write!;
    }

    /// <summary>
    /// Round-robin reader connection. Safe for concurrent SELECT under WAL while another thread writes.
    /// </summary>
    public async Task<SQLiteAsyncConnection> GetReadConnectionAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var readers = _readers!;
        var i = (Interlocked.Increment(ref _readerCursor) & 0x7FFFFFFF) % readers.Length;
        return readers[i];
    }

    /// <summary>Serialize writers so large blob inserts do not stampede SQLITE_BUSY.</summary>
    public async Task<T> WriteAsync<T>(Func<SQLiteAsyncConnection, Task<T>> work)
    {
        Require.NotNull(work);
        var conn = await GetWriteConnectionAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await work(conn).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Func<SQLiteAsyncConnection, Task> work) =>
        WriteAsync(async c =>
        {
            await work(c).ConfigureAwait(false);
            return true;
        });

    public async Task<T> ReadAsync<T>(Func<SQLiteAsyncConnection, Task<T>> work)
    {
        Require.NotNull(work);
        var conn = await GetReadConnectionAsync().ConfigureAwait(false);
        return await work(conn).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;

        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _write = CreateConnection();
            await ConfigureConnectionAsync(_write, setJournalMode: true).ConfigureAwait(false);
            await EnsureSchemaAsync(_write).ConfigureAwait(false);

            _readers = new SQLiteAsyncConnection[_readPoolSize];
            for (var i = 0; i < _readPoolSize; i++)
            {
                _readers[i] = CreateConnection();
                // journal_mode is persistent on the file; readers only need busy_timeout.
                await ConfigureConnectionAsync(_readers[i], setJournalMode: false).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private SQLiteAsyncConnection CreateConnection() =>
        new(_databasePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);

    private static async Task ConfigureConnectionAsync(SQLiteAsyncConnection connection, bool setJournalMode)
    {
        try
        {
            await connection.ExecuteAsync("PRAGMA busy_timeout = 15000").ConfigureAwait(false);
            if (setJournalMode)
            {
                var mode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode = WAL")
                    .ConfigureAwait(false);
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    mode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode = WAL")
                        .ConfigureAwait(false);
                }

                if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    await connection.ExecuteAsync("PRAGMA synchronous = NORMAL").ConfigureAwait(false);
            }
            else
            {
                // Ensure we see WAL commits from the writer promptly.
                await connection.ExecuteAsync("PRAGMA read_uncommitted = 0").ConfigureAwait(false);
            }
        }
        catch
        {
            // Prefer opening the DB over failing boot if a pragma is unsupported.
        }
    }

    private static async Task EnsureSchemaAsync(SQLiteAsyncConnection connection)
    {
        await connection.CreateTableAsync<UserEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<ChatEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<ChatMessageEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<SeenServerMessageEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<OutgoingServerMessageEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<BleDiscoveredPeerEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<MessengerServerEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<BotVersionEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<PeerBlacklistEntity>().ConfigureAwait(false);
        await connection.CreateTableAsync<PeerProfileEntity>().ConfigureAwait(false);

        await TryAlterAsync(connection,
            "ALTER TABLE messenger_servers ADD COLUMN TrustRating REAL NOT NULL DEFAULT 0.8").ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE users ADD COLUMN AboutMe TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE users ADD COLUMN Avatar BLOB NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN RelayRouteBlob TEXT NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN PeerEndpointsJson TEXT NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN PeerKeySourceKind TEXT NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN PeerKeySourceDetail TEXT NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN DeliveryPath INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE chats ADD COLUMN IsBot INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
                "ALTER TABLE chats ADD COLUMN BotCatalogChecked INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
                "ALTER TABLE chats ADD COLUMN MlKemSessionVerified INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
                "ALTER TABLE chats ADD COLUMN PeerAdvertisesMlKem INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
            "ALTER TABLE messages ADD COLUMN DeliveryStatus INTEGER NOT NULL DEFAULT 2").ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync("UPDATE messages SET DeliveryStatus = 0 WHERE Outgoing = 0")
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        await TryAlterAsync(connection,
            "ALTER TABLE messages ADD COLUMN PayloadKind INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN MimeType TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN ImageBlob BLOB NULL")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferId TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferToken TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
            "ALTER TABLE messages ADD COLUMN TransferPayloadKind TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferFileName TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
            "ALTER TABLE messages ADD COLUMN TransferSizeBytes INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferHost TEXT NOT NULL DEFAULT ''")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferPort INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection,
            "ALTER TABLE messages ADD COLUMN TransferExpiresUtcTicks INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferState INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
        await TryAlterAsync(connection, "ALTER TABLE messages ADD COLUMN TransferChunkCount INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);

        await MigrateBleDiscoveredPeersToFullNetworkIdAsync(connection).ConfigureAwait(false);
    }

    private static async Task TryAlterAsync(SQLiteAsyncConnection connection, string sql)
    {
        try
        {
            await connection.ExecuteAsync(sql).ConfigureAwait(false);
        }
        catch
        {
            // column already exists
        }
    }

    private static async Task MigrateBleDiscoveredPeersToFullNetworkIdAsync(SQLiteAsyncConnection connection)
    {
        const string flagKey = "network_id_12_bytes_v3";
        try
        {
            await connection.ExecuteAsync(
                    "CREATE TABLE IF NOT EXISTS app_schema_flags (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
                .ConfigureAwait(false);
            var applied = await connection.ExecuteScalarAsync<string>(
                "SELECT value FROM app_schema_flags WHERE key = ?", flagKey).ConfigureAwait(false);
            if (string.Equals(applied, "1", StringComparison.Ordinal))
                return;

            await connection.ExecuteAsync("DELETE FROM ble_discovered_peers").ConfigureAwait(false);
            await connection.ExecuteAsync(
                    "INSERT OR REPLACE INTO app_schema_flags (key, value) VALUES (?, ?)", flagKey, "1")
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore migration issues on very old DB files
        }
    }
}
