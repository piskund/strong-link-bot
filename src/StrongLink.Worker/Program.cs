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
    await RunStandaloneAsync();
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

async Task RunStandaloneAsync()
{
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));

    services.AddSingleton<IChatMessenger, ConsoleMessenger>();
    services.AddSingleton<ILocalizationService, LocalizationService>();
    services.AddSingleton<IGameSessionRepository, InMemoryGameSessionRepository>();
    services.AddSingleton<IGameLifecycleService, GameLifecycleService>();

    services.AddSingleton(new GameOptions
    {
        Tours = 3,
        RoundsPerTour = 4,
        AnswerTimeoutSeconds = 20,
        EliminateLowest = 1,
        Topics = new[] { "History", "Science", "Culture" }
    });

    services.AddSingleton(new DummyPlayerOptions
    {
        CorrectAnswerProbability = 0.45
    });

    services.AddSingleton<StandaloneGameRunner>();

    using var provider = services.BuildServiceProvider();
    var runner = provider.GetRequiredService<StandaloneGameRunner>();
    Console.WriteLine("Starting Strong Link standalone demo...\n");
    await runner.RunAsync(CancellationToken.None);
    Console.WriteLine("\nStandalone session finished.");
}
