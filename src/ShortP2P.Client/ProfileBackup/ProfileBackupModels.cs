namespace ShortP2P.Client.ProfileBackup;

public sealed class ProfileBackupDocument
{
    public ProfileBackupManifest Manifest { get; init; } = new();
    public ProfileBackupUser User { get; init; } = new();
    public IReadOnlyList<ProfileBackupServer> Servers { get; init; } = [];
}

public sealed class ProfileBackupManifest
{
    public int SchemaVersion { get; init; } = 2;
    public long ExportedUtcTicks { get; init; }
    public string App { get; init; } = "TorgLink";
    public IReadOnlyList<string> Sections { get; init; } = ["user", "servers"];
}

public sealed class ProfileBackupUser
{
    public string Nickname { get; init; } = "";
    public string NetworkIdShort { get; init; } = "";
    public ProfileBackupPassword Password { get; init; } = new();
    public string RsaPrivateJson { get; init; } = "";
    public string RsaPublicJson { get; init; } = "";
    public string AboutMe { get; init; } = "";
    public byte[]? Avatar { get; init; }
    public int DataUdpPort { get; init; } = 17500;
}

public sealed class ProfileBackupPassword
{
    public string Alg { get; init; } = "pbkdf2-sha256";
    public int Iterations { get; init; }
    public byte[] Salt { get; init; } = [];
    public byte[] Hash { get; init; } = [];
}

public sealed class ProfileBackupServer
{
    public string BaseUrl { get; init; } = "";
    public string FingerprintSha256 { get; init; } = "";
    public bool Trusted { get; init; }
    public float TrustRating { get; init; }
    public bool Active { get; init; }
    public bool IsRegistered { get; init; }
    public string AccountPassword { get; init; } = "";
    public string NetworkId { get; init; } = "";
    public string Nick { get; init; } = "";
    public long CreatedUtcTicks { get; init; }
    public long UpdatedUtcTicks { get; init; }
}

public sealed class ProfileBackupPreview
{
    public required string Nickname { get; init; }
    public required string NetworkIdShort { get; init; }
    public int ServerCount { get; init; }
    public int ActiveServerCount { get; init; }
    public IReadOnlyList<string> Sections { get; init; } = [];
}

public enum ProfileImportConflictKind
{
    None,
    OverwriteSameNetworkId,
    NicknameTakenByOther
}

public sealed class ProfileImportResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public ProfileImportConflictKind Conflict { get; init; }
    public ProfileBackupPreview? Preview { get; init; }
}
