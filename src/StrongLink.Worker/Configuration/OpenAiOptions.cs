namespace StrongLink.Worker.Configuration;

public sealed class OpenAiOptions
{
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Model to use for question generation (e.g., gpt-5.2 for best quality)
    /// </summary>
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>
    /// Model to use for answer validation (e.g., gpt-4o-mini for cost efficiency)
    /// If not specified, uses the same model as question generation
    /// </summary>
    public string? AnswerValidationModel { get; init; }

    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
}

