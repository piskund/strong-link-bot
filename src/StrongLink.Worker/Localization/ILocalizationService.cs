namespace StrongLink.Worker.Localization;

using StrongLink.Worker.Domain;

public interface ILocalizationService
{
    string GetString(GameLanguage language, string key);

    IReadOnlyDictionary<string, string> GetLanguagePack(GameLanguage language);
}

