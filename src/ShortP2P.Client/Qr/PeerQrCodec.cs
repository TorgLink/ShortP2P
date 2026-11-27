using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShortP2P.Auth.Data;
using ShortP2P.Crypto;
using ShortP2P.Transport;

namespace ShortP2P.Client.Qr;

public static class PeerQrCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(PeerQrPayload payload)
    {
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryDeserialize(string json, [NotNullWhen(true)] out PeerQrPayload? payload, out string? error)
    {
        payload = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "QR payload is empty.";
            return false;
        }

        PeerQrPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<PeerQrPayload>(json.Trim(), JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid QR data: {ex.Message}";
            return false;
        }

        if (p == null)
        {
            error = "Invalid QR data.";
            return false;
        }

        if (p.V != 1)
        {
            error = "Unsupported QR version.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.N) || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.K))
        {
            error = "QR is missing nickname, id, or public key.";
            return false;
        }

        TryMergeQrHosts(p, out var mergedHosts);
        TryMergeQrBleContacts(p, out var mergedBle);

        if (mergedHosts.Count == 0 && mergedBle.Count == 0
                                   && !CompressedNetworkId.TryParseShortString(p.Id, out _))
        {
            error = "QR is missing a valid host IP (h / ha), network id (id), or legacy contact (b / ba).";
            return false;
        }

        p.H = mergedHosts.Count > 0 ? mergedHosts[0] : "";
        p.Ha = mergedHosts.Count > 1 ? mergedHosts.Skip(1).ToList() : null;
        p.B = mergedBle.Count > 0 ? mergedBle[0] : null;
        p.Ba = mergedBle.Count > 1 ? mergedBle.Skip(1).ToList() : null;

        if (p.P is < 1 or > 65535)
        {
            error = "QR has an invalid UDP port.";
            return false;
        }

        if (!IdentityKeySerializer.TryDeserializePublic(p.K.Trim(), out var pub))
        {
            error = "QR contains an invalid identity public key.";
            return false;
        }

        if (!CompressedNetworkId.TryParseShortString(p.Id, out var nid) || !nid.MatchesIdentityPublicKey(pub))
        {
            error = "QR NetworkId does not match identity public key.";
            return false;
        }

        payload = p;
        return true;
    }

    private static void TryMergeQrHosts(PeerQrPayload p, out List<string> merged)
    {
        merged = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryAddQrHost(merged, seen, p.H);
        if (p.Ha != null)
            foreach (var x in p.Ha)
                TryAddQrHost(merged, seen, x);
    }

    private static void TryMergeQrBleContacts(PeerQrPayload p, out List<string> merged)
    {
        merged = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TryAddQrBleContact(merged, seen, p.B);
        if (p.Ba != null)
            foreach (var x in p.Ba)
                TryAddQrBleContact(merged, seen, x);
    }

    private static void TryAddQrHost(List<string> merged, HashSet<string> seen, string? s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s))
            return;
        var t = s.Trim();
        if (!IPAddress.TryParse(t, out _))
            return;
        if (!seen.Add(t))
            return;
        merged.Add(t);
    }

    private static void TryAddQrBleContact(List<string> merged, HashSet<string> seen, string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return;
        if (CompressedNetworkId.TryParseShortString(s, out var nid))
        {
            var canon = nid.ToShortString();
            if (!seen.Add(canon))
                return;
            merged.Add(canon);
            return;
        }

        if (!BluetoothTransportAddress.TryParseMac(s, out var mac))
            return;
        var macCanon = BluetoothTransportAddress.ToMacString(mac);
        if (!seen.Add(macCanon))
            return;
        merged.Add(macCanon);
    }
}