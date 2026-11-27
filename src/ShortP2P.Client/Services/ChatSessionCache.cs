using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ShortP2P.Client.Services;

/// <summary>Потокобезопасный кэш чат-сессий (одна запись на chatId).</summary>
public sealed class ChatSessionCache
{
    private readonly ConcurrentDictionary<int, SessionCacheEntry> _entries = new();

    public ChatP2PSession GetSession(int chatId, Func<ChatP2PSession> createSession, Action<ChatP2PSession> applyChat)
    {
        var entry = _entries.GetOrAdd(chatId, _ => new SessionCacheEntry(createSession()));
        applyChat(entry.Session);
        return entry.Session;
    }

    public bool TryGetSession(int chatId, [NotNullWhen(true)] out ChatP2PSession? session)
    {
        if (_entries.TryGetValue(chatId, out var entry))
        {
            session = entry.Session;
            return true;
        }

        session = null;
        return false;
    }

    public bool IsStarted(int chatId)
    {
        return _entries.TryGetValue(chatId, out var entry) && Volatile.Read(ref entry.Started) == 1;
    }

    public void MarkStarted(int chatId)
    {
        if (_entries.TryGetValue(chatId, out var entry))
            Interlocked.Exchange(ref entry.Started, 1);
    }

    public bool TryRemove(int chatId, out ChatP2PSession? session)
    {
        if (_entries.TryRemove(chatId, out var entry))
        {
            session = entry.Session;
            return true;
        }

        session = null;
        return false;
    }

    public IReadOnlyList<ChatP2PSession> DrainAll()
    {
        var list = _entries.Values.Select(x => x.Session).ToList();
        _entries.Clear();
        return list;
    }

    /// <summary>Snapshot of live sessions (for settings fan-out, e.g. ML-KEM toggle).</summary>
    public IReadOnlyList<ChatP2PSession> SnapshotSessions() =>
        _entries.Values.Select(x => x.Session).ToList();

    private sealed class SessionCacheEntry(ChatP2PSession session, bool isStarted = false)
    {
        public int Started = isStarted ? 1 : 0;
        public ChatP2PSession Session { get; } = session;
    }
}