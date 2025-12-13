using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Persistence;
using Xunit;

namespace StrongLink.Worker.Tests.Persistence;

public class QuestionPoolRepositoryTests : IDisposable
{
    private readonly string _testPath;
    private readonly QuestionPoolRepository _repository;

    public QuestionPoolRepositoryTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "StrongLinkTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testPath);

        var options = Options.Create(new BotOptions
        {
            StateStoragePath = _testPath,
            ResultsStoragePath = Path.Combine(_testPath, "results")
        });

        var logger = LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<QuestionPoolRepository>();

        _repository = new QuestionPoolRepository(logger, options);
    }

    [Fact]
    public async Task AddToUnusedPool_CreatesTopicFiles()
    {
        // Arrange
        var questions = new List<Question>
        {
            new() { Topic = "Literature", Text = "Who wrote Hamlet?", Answer = "Shakespeare", SourceName = "Test" },
            new() { Topic = "Literature", Text = "Who wrote 1984?", Answer = "Orwell", SourceName = "Test" },
            new() { Topic = "History", Text = "When was WW2?", Answer = "1939-1945", SourceName = "Test" }
        };

        // Act
        await _repository.AddToUnusedPoolAsync(questions, CancellationToken.None);

        // Assert
        var literatureFile = Path.Combine(_testPath, "pools", "unused", "Literature.json");
        var historyFile = Path.Combine(_testPath, "pools", "unused", "History.json");

        Assert.True(File.Exists(literatureFile), "Literature.json should exist");
        Assert.True(File.Exists(historyFile), "History.json should exist");

        var litContent = await File.ReadAllTextAsync(literatureFile);
        Assert.Contains("Hamlet", litContent);
        Assert.Contains("1984", litContent);

        var histContent = await File.ReadAllTextAsync(historyFile);
        Assert.Contains("WW2", histContent);
    }

    [Fact]
    public async Task GetUnusedQuestions_ReturnsAllTopicQuestions()
    {
        // Arrange
        var questions = new List<Question>
        {
            new() { Topic = "Science", Text = "What is H2O?", Answer = "Water", SourceName = "Test" },
            new() { Topic = "Math", Text = "What is 2+2?", Answer = "4", SourceName = "Test" }
        };
        await _repository.AddToUnusedPoolAsync(questions, CancellationToken.None);

        // Act
        var result = await _repository.GetUnusedQuestionsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, q => q.Text == "What is H2O?");
        Assert.Contains(result, q => q.Text == "What is 2+2?");
    }

    [Fact]
    public async Task SelectQuestions_RemovesFromPool()
    {
        // Arrange
        var questions = new List<Question>
        {
            new() { Topic = "Geography", Text = "What is the capital of France?", Answer = "Paris", SourceName = "Test" },
            new() { Topic = "Geography", Text = "What is the capital of Germany?", Answer = "Berlin", SourceName = "Test" },
            new() { Topic = "Geography", Text = "What is the capital of Italy?", Answer = "Rome", SourceName = "Test" }
        };
        await _repository.AddToUnusedPoolAsync(questions, CancellationToken.None);

        // Act
        var selected = await _repository.SelectQuestionsAsync("Geography", 2, CancellationToken.None);

        // Assert
        Assert.Equal(2, selected.Count);

        // Verify they were removed from pool
        var remaining = await _repository.GetUnusedQuestionsAsync(CancellationToken.None);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task MoveToArchive_CreatesDatePartitionedFiles()
    {
        // Arrange
        var questions = new List<Question>
        {
            new() { Topic = "Art", Text = "Who painted Mona Lisa?", Answer = "Da Vinci", SourceName = "Test" }
        };
        await _repository.AddToUnusedPoolAsync(questions, CancellationToken.None);

        // Act
        await _repository.MoveToArchiveAsync(questions, CancellationToken.None);

        // Assert
        var currentMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var archiveFile = Path.Combine(_testPath, "pools", "archived", currentMonth, "Art.json");

        Assert.True(File.Exists(archiveFile), $"Archive file should exist at {archiveFile}");

        var content = await File.ReadAllTextAsync(archiveFile);
        Assert.Contains("Mona Lisa", content);

        // Verify removed from unused
        var unused = await _repository.GetUnusedQuestionsAsync(CancellationToken.None);
        Assert.Empty(unused);
    }

    [Fact]
    public async Task GetArchivedQuestions_ReturnsFromAllMonths()
    {
        // Arrange
        var questions = new List<Question>
        {
            new() { Topic = "Sports", Text = "How many players in soccer?", Answer = "11", SourceName = "Test" }
        };
        await _repository.AddToUnusedPoolAsync(questions, CancellationToken.None);
        await _repository.MoveToArchiveAsync(questions, CancellationToken.None);

        // Act
        var archived = await _repository.GetArchivedQuestionsAsync(CancellationToken.None);

        // Assert
        Assert.Single(archived);
        Assert.Equal("How many players in soccer?", archived[0].Text);
    }

    [Fact]
    public async Task AddToUnusedPool_DeduplicatesByText()
    {
        // Arrange
        var questions1 = new List<Question>
        {
            new() { Topic = "Tech", Text = "What is CPU?", Answer = "Processor", SourceName = "Test" }
        };
        var questions2 = new List<Question>
        {
            new() { Topic = "Tech", Text = "What is CPU?", Answer = "Central Processing Unit", SourceName = "Test2" }
        };

        // Act
        await _repository.AddToUnusedPoolAsync(questions1, CancellationToken.None);
        await _repository.AddToUnusedPoolAsync(questions2, CancellationToken.None);

        // Assert
        var result = await _repository.GetUnusedQuestionsAsync(CancellationToken.None);
        Assert.Single(result); // Should only have one, not two
    }

    [Fact]
    public async Task GetPoolStats_ReturnsCorrectCounts()
    {
        // Arrange
        var unusedQuestions = new List<Question>
        {
            new() { Topic = "Culture", Text = "Q1?", Answer = "A1", SourceName = "Test" },
            new() { Topic = "Culture", Text = "Q2?", Answer = "A2", SourceName = "Test" }
        };
        var toArchive = new List<Question>
        {
            new() { Topic = "Culture", Text = "Q3?", Answer = "A3", SourceName = "Test" }
        };

        await _repository.AddToUnusedPoolAsync(unusedQuestions, CancellationToken.None);
        await _repository.AddToUnusedPoolAsync(toArchive, CancellationToken.None);
        await _repository.MoveToArchiveAsync(toArchive, CancellationToken.None);

        // Act
        var stats = await _repository.GetPoolStatsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, stats.Unused);
        Assert.Equal(1, stats.Archived);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }
}
