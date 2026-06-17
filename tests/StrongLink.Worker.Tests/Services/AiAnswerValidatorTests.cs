using StrongLink.Worker.Services;

namespace StrongLink.Worker.Tests.Services;

/// <summary>
/// Tests for <see cref="AiAnswerValidator.InterpretValidationResult"/> — the parsing of the
/// model's free-form verdict. The original implementation compared the reply with an exact
/// equality against "correct"/"верно"/etc., which silently rejected any reply that carried
/// punctuation, markdown, or an extra word — making validation far stricter than intended.
/// </summary>
public class AiAnswerValidatorTests
{
    [Theory]
    // Plain verdicts
    [InlineData("Correct", true)]
    [InlineData("Incorrect", false)]
    [InlineData("Верно", true)]
    [InlineData("Неверно", false)]
    // Punctuation / casing — these used to be rejected
    [InlineData("Верно.", true)]
    [InlineData("correct.", true)]
    [InlineData("ВЕРНО!", true)]
    [InlineData("Неверно.", false)]
    // Markdown emphasis
    [InlineData("**Correct**", true)]
    [InlineData("*верно*", true)]
    // Extra words around the verdict
    [InlineData("Да, верно", true)]
    [InlineData("Yes, that's correct", true)]
    [InlineData("Ответ верный", true)]
    [InlineData("Это неправильно", false)]
    // Negated positives must read as rejection
    [InlineData("не верно", false)]
    [InlineData("not correct", false)]
    // Empty / garbled → don't punish the player for a model hiccup
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void InterpretValidationResult_ReadsVerdictRobustly(string raw, bool expected)
    {
        Assert.Equal(expected, AiAnswerValidator.InterpretValidationResult(raw));
    }

    [Theory]
    // Partial-name answers — the exact reason the bot felt too strict when the AI was rate-limited.
    [InlineData("грибоедов", "александр грибоедов", true)]
    [InlineData("фродо", "фродо бэггинс", true)]
    [InlineData("александр", "александр грибоедов", true)]
    // Full match and reordering still pass.
    [InlineData("александр грибоедов", "александр грибоедов", true)]
    [InlineData("грибоедов александр", "александр грибоедов", true)]
    // Genuinely different answers are still rejected.
    [InlineData("пушкин", "александр грибоедов", false)]
    [InlineData("сэм", "фродо бэггинс", false)]
    // Short stray tokens shouldn't trigger a false accept.
    [InlineData("де", "шарль де голль", false)]
    public void LenientHeuristicMatch_AcceptsPartialNamesButRejectsWrongAnswers(string user, string correct, bool expected)
    {
        Assert.Equal(expected, AiAnswerValidator.LenientHeuristicMatch(user, correct));
    }
}
