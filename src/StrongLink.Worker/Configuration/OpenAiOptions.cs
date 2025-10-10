namespace StrongLink.Worker.Configuration;

public sealed class OpenAiOptions
{
    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "gpt-4o-mini";

    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
}

