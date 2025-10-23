using System.Text.Json.Serialization;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Configuration;

public sealed class StandaloneOptions
{
    public string HumanName { get; init; } = "Игрок";

    public double DummyAccuracy { get; init; } = 0.45;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QuestionSourceMode? PreferredSource { get; init; }
}

