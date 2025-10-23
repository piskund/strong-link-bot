using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using StrongLink.Worker.Standalone;
using StrongLink.Worker.Telegram;
using StrongLink.Worker.Telegram.Updates;
using StrongLink.Worker.Telegram.Updates.Handlers;
using Telegram.Bot;

if (args.Contains("--standalone", StringComparer.OrdinalIgnoreCase))
{
    await RunStandaloneAsync(args);
    return;
}

if (args.Contains("--standalone-ai-test", StringComparer.OrdinalIgnoreCase))
{
    await RunStandaloneAiTestAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<BotOptions>().Bind(builder.Configuration.GetSection("Bot"));
builder.Services.AddOptions<GameOptions>().Bind(builder.Configuration.GetSection("Game"));
builder.Services.AddOptions<OpenAiOptions>().Bind(builder.Configuration.GetSection("OpenAi"));
builder.Services.AddOptions<ChgkOptions>().Bind(builder.Configuration.GetSection("Chgk"));

builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
builder.Services.AddSingleton<IChatMessenger, ChatMessenger>();
builder.Services.AddSingleton<QuestionProviderFactory>();
builder.Services.AddSingleton<JsonGameSessionRepository>();
builder.Services.AddSingleton<IGameSessionRepository>(sp => sp.GetRequiredService<JsonGameSessionRepository>());
builder.Services.AddSingleton<IGameLifecycleService, GameLifecycleService>();
builder.Services.AddSingleton<IBotLifetimeService, TelegramBotService>();
builder.Services.AddSingleton<UpdateDispatcher>();

builder.Services.AddHttpClient<AiQuestionProvider>();
builder.Services.AddHttpClient<ChgkQuestionProvider>();

builder.Services.AddSingleton<IQuestionProvider>(sp => sp.GetRequiredService<AiQuestionProvider>());
builder.Services.AddSingleton<IQuestionProvider>(sp => sp.GetRequiredService<ChgkQuestionProvider>());

builder.Services.AddTransient<StartCommandHandler>();
builder.Services.AddTransient<JoinCommandHandler>();
builder.Services.AddTransient<PreparePoolCommandHandler>();
builder.Services.AddTransient<FetchPoolCommandHandler>();
builder.Services.AddTransient<HelpCommandHandler>();
builder.Services.AddTransient<StandingsCommandHandler>();
builder.Services.AddTransient<StartGameCommandHandler>();
builder.Services.AddTransient<StopCommandHandler>();
builder.Services.AddTransient<AnswerMessageHandler>();

builder.Services.AddSingleton<IEnumerable<IUpdateHandler>>(sp => new IUpdateHandler[]
{
    sp.GetRequiredService<StartCommandHandler>(),
    sp.GetRequiredService<JoinCommandHandler>(),
    sp.GetRequiredService<PreparePoolCommandHandler>(),
    sp.GetRequiredService<FetchPoolCommandHandler>(),
    sp.GetRequiredService<HelpCommandHandler>(),
    sp.GetRequiredService<StandingsCommandHandler>(),
    sp.GetRequiredService<StartGameCommandHandler>(),
    sp.GetRequiredService<StopCommandHandler>(),
    sp.GetRequiredService<AnswerMessageHandler>()
});

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<BotOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.Token) || options.Token == "YOUR_TELEGRAM_BOT_TOKEN")
    {
        throw new InvalidOperationException("Telegram bot token is not configured. Update appsettings.json or environment variables.");
    }
    return new TelegramBotClient(options.Token);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

async Task RunStandaloneAsync(string[] args)
{
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    var botOptions = configuration.GetSection("Bot").Get<BotOptions>()
        ?? throw new InvalidOperationException("Bot configuration is missing.");
    var gameOptions = configuration.GetSection("Game").Get<GameOptions>() ?? new GameOptions();
    var standaloneOptions = configuration.GetSection("Standalone").Get<StandaloneOptions>() ?? new StandaloneOptions();
    var openAiOptions = configuration.GetSection("OpenAi").Get<OpenAiOptions>() ?? new OpenAiOptions();
    var chgkOptions = configuration.GetSection("Chgk").Get<ChgkOptions>() ?? new ChgkOptions();

    services.AddSingleton(configuration);

    services.AddSingleton(botOptions);
    services.AddSingleton(gameOptions);
    services.AddSingleton(standaloneOptions);
    services.AddSingleton<IOptions<OpenAiOptions>>(Options.Create(openAiOptions));
    services.AddSingleton<IOptions<ChgkOptions>>(Options.Create(chgkOptions));

    services.AddSingleton<IChatMessenger, ConsoleMessenger>();
    services.AddSingleton<ILocalizationService, LocalizationService>();
    services.AddSingleton<IGameSessionRepository, InMemoryGameSessionRepository>();
    services.AddSingleton<IGameLifecycleService, GameLifecycleService>();

    services.AddHttpClient<AiQuestionProvider>();
    services.AddHttpClient<ChgkQuestionProvider>();
    services.AddSingleton<IQuestionProvider, AiQuestionProvider>();
    services.AddSingleton<IQuestionProvider, ChgkQuestionProvider>();
    services.AddSingleton<IQuestionProvider, JsonQuestionProvider>();
    services.AddSingleton<QuestionProviderFactory>();

    services.AddSingleton(new DummyPlayerOptions
    {
        CorrectAnswerProbability = standaloneOptions.DummyAccuracy
    });

    services.AddSingleton<StandaloneGameRunner>();

    using var provider = services.BuildServiceProvider();
    var runner = provider.GetRequiredService<StandaloneGameRunner>();
    var runOptions = ParseStandaloneArgs(args, botOptions, gameOptions);
    runner.Options = runOptions;
    Console.WriteLine("Starting Strong Link standalone...\n");
    await runner.RunAsync(CancellationToken.None);
    Console.WriteLine("\nStandalone session finished.");
}

async Task RunStandaloneAiTestAsync()
{
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    var botOptions = configuration.GetSection("Bot").Get<BotOptions>()
        ?? throw new InvalidOperationException("Bot configuration is missing.");
    var gameOptions = configuration.GetSection("Game").Get<GameOptions>() ?? new GameOptions();
    var openAiOptions = configuration.GetSection("OpenAi").Get<OpenAiOptions>() ?? new OpenAiOptions();

    services.AddSingleton(botOptions);
    services.AddSingleton(gameOptions);
    services.AddSingleton<IOptions<OpenAiOptions>>(Options.Create(openAiOptions));

    services.AddSingleton<LocalizationService>();
    services.AddHttpClient<AiQuestionProvider>();

    services.AddSingleton<AiTestRunner>();

    using var provider = services.BuildServiceProvider();
    var runner = provider.GetRequiredService<AiTestRunner>();
    await runner.RunAsync(CancellationToken.None);
}

static StrongLink.Worker.Standalone.StandaloneRunOptions ParseStandaloneArgs(string[] args, BotOptions bot, GameOptions game)
{
    static string? GetValue(string[] a, string key)
    {
        var idx = Array.FindIndex(a, s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < a.Length) return a[idx + 1];
        return null;
    }

    static bool HasFlag(string[] a, string key) => a.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));

    var src = GetValue(args, "--source");
    Domain.QuestionSourceMode? source = src?.ToLowerInvariant() switch
    {
        "ai" => Domain.QuestionSourceMode.AI,
        "chgk" => Domain.QuestionSourceMode.Chgk,
        "json" => Domain.QuestionSourceMode.Json,
        _ => null
    };

    var lang = GetValue(args, "--language");
    Domain.GameLanguage? language = lang?.ToLowerInvariant() switch
    {
        "ru" => Domain.GameLanguage.Russian,
        "en" => Domain.GameLanguage.English,
        _ => null
    };

    var topicsCsv = GetValue(args, "--topics");
    string[]? topics = string.IsNullOrWhiteSpace(topicsCsv)
        ? null
        : topicsCsv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;

    var tours = ParseInt(GetValue(args, "--tours"));
    var rounds = ParseInt(GetValue(args, "--rounds"));
    var players = ParseInt(GetValue(args, "--players"));
    var timeLimit = ParseInt(GetValue(args, "--time-limit"));
    var seed = ParseInt(GetValue(args, "--seed"));

    var opts = new StrongLink.Worker.Standalone.StandaloneRunOptions
    {
        Source = source,
        PoolFile = GetValue(args, "--pool-file"),
        Topics = topics,
        Tours = tours,
        Rounds = rounds,
        Language = language,
        Players = players,
        DummyProfile = GetValue(args, "--dummy-profile"),
        TimeLimitSeconds = timeLimit,
        ExportPath = GetValue(args, "--export"),
        DryRun = HasFlag(args, "--dry-run"),
        ShowAnswers = HasFlag(args, "--show-answers") ? true : (HasFlag(args, "--no-show-answers") ? false : null),
        Shuffle = HasFlag(args, "--shuffle") ? true : (HasFlag(args, "--no-shuffle") ? false : null),
        StrictMatch = HasFlag(args, "--strict-match") ? true : (HasFlag(args, "--no-strict-match") ? false : null),
        Seed = seed
    };

    // Dummy profile mapping to a single global accuracy for now
    var profile = opts.DummyProfile?.ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(profile))
    {
        var accuracy = profile switch
        {
            "easy" => 0.3,
            "medium" => 0.5,
            "hard" => 0.7,
            "mix" => 0.5,
            _ => (double?)null
        };
        if (accuracy is not null)
        {
            // we don't have DI here; this will be applied in runner via DummyPlayerOptions existing instance
        }
    }

    return opts;
}
