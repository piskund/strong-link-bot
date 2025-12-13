using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public interface IAnswerValidator
{
    /// <summary>
    /// Validates if the user's answer is semantically correct compared to the expected answer.
    /// </summary>
    /// <param name="userAnswer">The answer provided by the user</param>
    /// <param name="correctAnswer">The expected correct answer</param>
    /// <param name="question">The question being answered</param>
    /// <param name="language">The language of the question and answer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the answer is considered correct, false otherwise</returns>
    Task<bool> ValidateAnswerAsync(
        string userAnswer,
        string correctAnswer,
        string question,
        GameLanguage language,
        CancellationToken cancellationToken);
}
