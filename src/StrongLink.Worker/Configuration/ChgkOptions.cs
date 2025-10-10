namespace StrongLink.Worker.Configuration;

public sealed class ChgkOptions
{
    public string RandomEndpoint { get; init; } = "https://db.chgk.info/xml/random";

    public int BatchSize { get; init; } = 100;
}

