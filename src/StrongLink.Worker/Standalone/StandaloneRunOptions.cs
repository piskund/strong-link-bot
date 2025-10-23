using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Standalone;

public sealed class StandaloneRunOptions
{
	public QuestionSourceMode? Source { get; init; }

	public string? PoolFile { get; init; }

	public string[]? Topics { get; init; }

	public int? Tours { get; init; }

	public int? Rounds { get; init; }

	public GameLanguage? Language { get; init; }

	public int? Players { get; init; }

	public string? DummyProfile { get; init; } // easy|medium|hard|mix or JSON path (not implemented)

	public double? DummyAccuracyOverride { get; init; }

	public int? TimeLimitSeconds { get; init; }

	public string? ExportPath { get; init; }

	public bool DryRun { get; init; }

	public bool? ShowAnswers { get; init; }

	public bool? Shuffle { get; init; }

	public bool? StrictMatch { get; init; }

	public int? Seed { get; init; }
}


