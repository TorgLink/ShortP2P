using System.Text;
using Microsoft.Extensions.Logging;

namespace ShortP2P.Crypto;

/// <summary>Форматирование и запись plaintext при шифровании/дешифровании P2P-сессии. Hex — только DEBUG.</summary>
public static class CryptoTrafficLog
{
    public static string FormatPayloadHex(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return "(empty)";

        var sb = new StringBuilder(payload.Length * 5);
        for (var i = 0; i < payload.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append("0x").Append(payload[i].ToString("X2"));
        }

        return sb.ToString();
    }

    public static void LogEncryptPlaintext(ILogger? logger, ReadOnlySpan<byte> plaintext)
    {
#if DEBUG
        // Trace only: hex dumps are huge and flood async NLog queues (Discard drops LoRa TX Info).
        if (logger?.IsEnabled(LogLevel.Trace) != true)
            return;

        logger.LogTrace("CRYPT encrypt plaintext: {Payload}", FormatPayloadHex(plaintext));
#endif
    }

    public static void LogDecryptPlaintext(ILogger? logger, ReadOnlySpan<byte> plaintext)
    {
#if DEBUG
        if (logger?.IsEnabled(LogLevel.Trace) != true)
            return;

        logger.LogTrace("CRYPT decrypt plaintext: {Payload}", FormatPayloadHex(plaintext));
#endif
    }
}
