using System.Net;
using System.Text.Json.Serialization;
using ShortP2P.Auth.Data;
using ShortP2P.Transport;

namespace ShortP2P.Client.Qr;

/// <summary>Compact JSON payload embedded in a peer contact QR code (version 1).</summary>
public sealed class PeerQrPayload
{
    [JsonPropertyName("v")] public int V { get; set; }

    [JsonPropertyName("n")] public string N { get; set; } = "";

    [JsonPropertyName("h")] public string H { get; set; } = "";

    /// <summary>Дополнительные IPv4/IPv6 (первый по-прежнему в <see cref="H" />). Старые клиенты поле игнорируют.</summary>
    [JsonPropertyName("ha")]
    public List<string>? Ha { get; set; }

    /// <summary>Устаревшее: раньше Bluetooth MAC, затем дублировал <see cref="Id" />. Не пишется в новых QR.</summary>
    [JsonPropertyName("b")]
    public string? B { get; set; }

    /// <summary>Устаревшее: доп. MAC или network id из старых QR.</summary>
    [JsonPropertyName("ba")]
    public List<string>? Ba { get; set; }

    [JsonPropertyName("p")] public int P { get; set; }

    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>RSA public key JSON (same format as <see cref="T:ShortP2P.Crypto.IdentityKeySerializer" />).</summary>
    [JsonPropertyName("k")]
    public string K { get; set; } = "";

    /// <summary>IP, network id и (для старых QR) MAC для <see cref="Data.ChatEntity.PeerHost" />.</summary>
    public string GetCommaSeparatedHosts()
    {
        var list = new List<string>();

        void addIp(string? s)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0)
                return;
            if (!IPAddress.TryParse(t, out _))
                return;
            if (!list.Contains(t, StringComparer.OrdinalIgnoreCase))
                list.Add(t);
        }

        void addNetworkId(string? s)
        {
            if (!CompressedNetworkId.TryParseShortString(s, out var nid))
                return;
            var canon = nid.ToShortString();
            if (!list.Contains(canon, StringComparer.OrdinalIgnoreCase))
                list.Add(canon);
        }

        void addMac(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return;
            if (!BluetoothTransportAddress.TryParseMac(s, out var mac))
                return;
            var canon = BluetoothTransportAddress.ToMacString(mac);
            if (!list.Contains(canon, StringComparer.OrdinalIgnoreCase))
                list.Add(canon);
        }

        addIp(H);
        if (Ha != null)
            foreach (var x in Ha)
                addIp(x);

        addNetworkId(Id);
        addNetworkId(B);
        if (Ba != null)
            foreach (var x in Ba)
                addNetworkId(x);

        // Старые QR: MAC в b/ba (если b — не network id)
        if (B != null && !CompressedNetworkId.TryParseShortString(B, out _))
            addMac(B);
        if (Ba != null)
            foreach (var x in Ba)
                if (!CompressedNetworkId.TryParseShortString(x, out _))
                    addMac(x);

        return string.Join(", ", list);
    }
}