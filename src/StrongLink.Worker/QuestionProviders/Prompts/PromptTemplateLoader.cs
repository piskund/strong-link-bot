using System.Collections.Concurrent;
using Scriban;
using Scriban.Runtime;

namespace StrongLink.Worker.QuestionProviders.Prompts;

/// <summary>
/// Loads, parses, and caches the Scriban prompt templates shipped alongside the app under
/// <c>QuestionProviders/Prompts/</c> (copied to the output directory at build time). Templates are
/// parsed once and reused; rendering binds a <see cref="ScriptObject"/> model.
/// </summary>
internal static class PromptTemplateLoader
{
    private static readonly ConcurrentDictionary<string, Template> Cache = new();

    private static readonly string PromptDirectory =
        Path.Combine(AppContext.BaseDirectory, "QuestionProviders", "Prompts");

    /// <summary>
    /// Renders a template that has no placeholders (e.g. a fixed system prompt), returning the
    /// trimmed result.
    /// </summary>
    public static string Render(string fileName) => Render(fileName, new ScriptObject());

    /// <summary>
    /// Renders the named template (e.g. <c>"QuestionGeneration.en.scriban"</c>) against the supplied
    /// model, returning the trimmed result.
    /// </summary>
    public static string Render(string fileName, ScriptObject model)
    {
        var template = Cache.GetOrAdd(fileName, static name =>
        {
            var path = Path.Combine(PromptDirectory, name);
            var source = File.ReadAllText(path);
            var parsed = Template.Parse(source, path);
            if (parsed.HasErrors)
            {
                throw new InvalidOperationException(
                    $"Failed to parse prompt template '{name}': {string.Join("; ", parsed.Messages)}");
            }

            return parsed;
        });

        var context = new TemplateContext();
        context.PushGlobal(model);
        return template.Render(context).Trim();
    }
}
