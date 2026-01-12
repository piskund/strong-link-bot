using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Localization;

public sealed class LocalizationService : ILocalizationService
{
    private readonly Dictionary<GameLanguage, IReadOnlyDictionary<string, string>> _catalog;

    public LocalizationService()
    {
        _catalog = new Dictionary<GameLanguage, IReadOnlyDictionary<string, string>>
        {
            [GameLanguage.Russian] = BuildRussianPack(),
            [GameLanguage.English] = BuildEnglishPack()
        };
    }

    public string GetString(GameLanguage language, string key)
    {
        if (_catalog.TryGetValue(language, out var pack) && pack.TryGetValue(key, out var value))
        {
            return value;
        }

        return key;
    }

    public IReadOnlyDictionary<string, string> GetLanguagePack(GameLanguage language)
    {
        return _catalog.TryGetValue(language, out var pack)
            ? pack
            : _catalog[GameLanguage.English];
    }

    private static IReadOnlyDictionary<string, string> BuildRussianPack() => new Dictionary<string, string>
    {
        ["Bot.Welcome"] = "Привет! Это Strong Link — интеллектуальная викторина в стиле 'Самое сильное звено'.\n" +
                          "🤖 Версия: {0}\n\n" +
                          "📋 Доступные команды:\n" +
                          "/join — присоединиться к игре\n" +
                          "/standings — посмотреть таблицу результатов\n" +
                          "/schedule — расписание запланированных игр\n" +
                          "/help — показать эту справку",
        ["Bot.Help"] = "📋 Доступные команды:\n" +
                       "/join — присоединиться к игре\n" +
                       "/standings — посмотреть таблицу результатов\n" +
                       "/schedule — расписание запланированных игр\n" +
                       "/help — показать эту справку",
        ["Bot.NotAdmin"] = "Эта команда доступна только администраторам игры.",
        ["Bot.GameAlreadyRunning"] = "Игра уже запущена в этом чате.",
        ["Bot.GameNotConfigured"] = "Настройте или подготовьте пул вопросов перед стартом игры.",
        ["Bot.PoolPreparing"] = "Подготавливаем пул вопросов, подождите...",
        ["Bot.PoolReady"] = "Пул вопросов успешно подготовлен.",
        ["Bot.PoolFailure"] = "Не удалось подготовить пул вопросов: {0}",
        ["Bot.Joined"] = "{0} присоединился к игре.",
        ["Bot.AlreadyJoined"] = "{0}, вы уже участвуете в игре.",
        ["Bot.NoPlayers"] = "Никто не присоединился к игре. Используйте команду /join, чтобы участвовать.",
        ["Bot.ConfigUpdated"] = "Настройки игры обновлены.",
        ["Game.Start"] = "Игра Strong Link начинается! Тур {0}: {1}.",
        ["Game.TourStart"] = "🎯 Тур {0}: {1}",
        ["Game.Round"] = "🎯 Тур {0} — {1}\nРаунд {2}/{3}. Вопрос для {4}:\n{5}\n\n⏱️ У вас есть {6} секунд на ответ!",
        ["Game.Correct"] = "Верно!",
        ["Game.Incorrect"] = "Неверно. Правильный ответ: {0}.",
        ["Game.Timeout"] = "⏱️ Время вышло для {0}! Правильный ответ: {1}",
        ["Game.Eliminated"] = "Игрок {0} выбыл из борьбы за медали.",
        ["Game.TourComplete"] = "Тур {0} завершён. Следующий тур: {1}",
        ["Game.Finals"] = "Финальный раунд! В игре осталось {0} игроков.",
        ["Game.SuddenDeath"] = "⚡ Внезапная смерть! Игроки набрали одинаковое количество очков. Задаём вопросы по кругу до разрыва.",
        ["Game.SuddenDeathRound"] = "⚡ Внезапная смерть. Вопрос для {0}:\n{1}\n\n⏱️ У вас есть {2} секунд на ответ!",
        ["Game.SuddenDeathResolved"] = "✅ Внезапная смерть завершена! Места распределены.",
        ["Game.RoundSummary"] = "📊 Раунд {0}/{1} завершён. Результаты:",
        ["Game.TourSummary"] = "📊 Тур завершён! Итоги:",
        ["Game.Points"] = "очков",
        ["Game.StandingsHeader"] = "Текущие результаты:",
        ["Game.NoActiveSession"] = "Сейчас игра не запущена.",
        ["Game.Stopped"] = "Игра остановлена администратором.",
        ["Game.Paused"] = "⏸️ Игра поставлена на паузу. Используйте /resume для продолжения.",
        ["Game.Resumed"] = "▶️ Игра продолжена!",
        ["Game.Completed"] = "Игра завершена. Победитель: {0}!",
        ["Game.NotEnoughPlayers"] = "В игре должен быть хотя бы один игрок. Используйте /join для участия.",
        ["Game.NoQuestionPool"] = "Подготовьте пул вопросов перед стартом игры.",
        ["Game.AnswerIgnored"] = "Сейчас отвечает другой игрок.",
        ["Error.Unknown"] = "Произошла неизвестная ошибка. Попробуйте позже." 
    };

    private static IReadOnlyDictionary<string, string> BuildEnglishPack() => new Dictionary<string, string>
    {
        ["Bot.Welcome"] = "Welcome to Strong Link — a high-stakes quiz game for your group!\n" +
                          "🤖 Version: {0}\n\n" +
                          "📋 Available commands:\n" +
                          "/join — join the game\n" +
                          "/standings — view the leaderboard\n" +
                          "/schedule — view scheduled games\n" +
                          "/help — show this help",
        ["Bot.Help"] = "📋 Available commands:\n" +
                       "/join — join the game\n" +
                       "/standings — view the leaderboard\n" +
                       "/schedule — view scheduled games\n" +
                       "/help — show this help",
        ["Bot.NotAdmin"] = "This command is restricted to game administrators.",
        ["Bot.GameAlreadyRunning"] = "A game is already running in this chat.",
        ["Bot.GameNotConfigured"] = "Please prepare a question pool before starting the game.",
        ["Bot.PoolPreparing"] = "Preparing the question pool. Please wait...",
        ["Bot.PoolReady"] = "Question pool prepared successfully.",
        ["Bot.PoolFailure"] = "Failed to prepare question pool: {0}",
        ["Bot.Joined"] = "{0} joined the game.",
        ["Bot.AlreadyJoined"] = "{0}, you are already in the game.",
        ["Bot.NoPlayers"] = "No one has joined the game yet. Use /join to participate.",
        ["Bot.ConfigUpdated"] = "Game settings updated.",
        ["Game.Start"] = "Strong Link is starting! Tour {0}: {1}.",
        ["Game.TourStart"] = "🎯 Tour {0}: {1}",
        ["Game.Round"] = "🎯 Tour {0} — {1}\nRound {2}/{3}. Question for {4}:\n{5}\n\n⏱️ You have {6} seconds to answer!",
        ["Game.Correct"] = "Correct!",
        ["Game.Incorrect"] = "Incorrect. The correct answer is {0}.",
        ["Game.Timeout"] = "⏱️ Time's up for {0}! The correct answer was: {1}",
        ["Game.Eliminated"] = "Player {0} has been eliminated from medal contention.",
        ["Game.TourComplete"] = "Tour {0} complete. Next tour: {1}",
        ["Game.Finals"] = "Final rounds! {0} players remain.",
        ["Game.SuddenDeath"] = "⚡ Sudden Death! Players are tied. We'll ask questions in turns until the tie is broken.",
        ["Game.SuddenDeathRound"] = "⚡ Sudden Death. Question for {0}:\n{1}\n\n⏱️ You have {2} seconds to answer!",
        ["Game.SuddenDeathResolved"] = "✅ Sudden Death complete! Rankings resolved.",
        ["Game.RoundSummary"] = "📊 Round {0}/{1} complete. Current standings:",
        ["Game.TourSummary"] = "📊 Tour complete! Results:",
        ["Game.Points"] = "points",
        ["Game.StandingsHeader"] = "Current standings:",
        ["Game.NoActiveSession"] = "No active game in this chat.",
        ["Game.Stopped"] = "The game has been stopped by an administrator.",
        ["Game.Paused"] = "⏸️ Game paused. Use /resume to continue.",
        ["Game.Resumed"] = "▶️ Game resumed!",
        ["Game.Completed"] = "Game over. Winner: {0}!",
        ["Game.NotEnoughPlayers"] = "At least one player must join. Use /join to participate.",
        ["Game.NoQuestionPool"] = "Prepare a question pool before starting the game.",
        ["Game.AnswerIgnored"] = "Another player is answering right now.",
        ["Error.Unknown"] = "An unknown error occurred. Please try again later." 
    };
}

