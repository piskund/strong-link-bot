namespace StrongLink.Worker.Domain;

public enum GameStatus
{
    NotConfigured,
    AwaitingPlayers,
    PreparingQuestionPool,
    ReadyToStart,
    InProgress,
    SuddenDeath,
    Completed,
    Cancelled
}

public enum PlayerStatus
{
    Pending,
    Active,
    Eliminated,
    Spectator
}

public enum GameLanguage
{
    Russian,
    English
}

public enum QuestionSourceMode
{
    AI,
    Chgk,
    Json
}

