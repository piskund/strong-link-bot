namespace StrongLink.Worker.Configuration;

public sealed class OpenAiOptions
{
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Model to use for question generation (e.g., gpt-5.2 for best quality)
    /// </summary>
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>
    /// Model to use for answer validation. By default uses the same model as question generation
    /// for best accuracy. Can be set to a lighter model (e.g., gpt-4o-mini) for cost efficiency.
    /// If not specified, uses the same model as question generation.
    /// </summary>
    public string? AnswerValidationModel { get; init; }

    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// Percentage of questions that should include images (0-100)
    /// </summary>
    public int ImagePercentage { get; init; } = 30;

    /// <summary>
    /// Whether to use DALL-E to generate images for questions instead of using existing URLs.
    /// When enabled, questions without images will have images generated via DALL-E API.
    /// Note: This increases cost and latency significantly.
    /// </summary>
    public bool UseDallEImageGeneration { get; init; } = false;

    /// <summary>
    /// DALL-E model to use for image generation (e.g., "dall-e-3", "dall-e-2")
    /// Only used if UseDallEImageGeneration is true.
    /// </summary>
    public string DallEModel { get; init; } = "dall-e-3";

    /// <summary>
    /// DALL-E image size (e.g., "1024x1024", "1792x1024", "1024x1792" for dall-e-3)
    /// Only used if UseDallEImageGeneration is true.
    /// </summary>
    public string DallEImageSize { get; init; } = "1024x1024";

    /// <summary>
    /// DALL-E API endpoint for image generation
    /// Only used if UseDallEImageGeneration is true.
    /// </summary>
    public string DallEEndpoint { get; init; } = "https://api.openai.com/v1/images/generations";
}

