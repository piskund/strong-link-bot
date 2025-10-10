using CommunityToolkit.Diagnostics;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.QuestionProviders;

public sealed class QuestionProviderFactory
{
    private readonly IEnumerable<IQuestionProvider> _providers;

    public QuestionProviderFactory(IEnumerable<IQuestionProvider> providers)
    {
        _providers = providers;
    }

    public IQuestionProvider Resolve(QuestionSourceMode mode)
    {
        var provider = _providers.FirstOrDefault(p => p.Mode == mode);
        Guard.IsNotNull(provider, nameof(provider));
        return provider;
    }
}

