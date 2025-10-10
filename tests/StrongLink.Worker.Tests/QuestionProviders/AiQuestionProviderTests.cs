using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker.Tests.QuestionProviders;

public class AiQuestionProviderTests
{
    [Fact]
    public async Task PrepareQuestionPoolAsync_ParsesQuestionsFromResponse()
    {
        const string json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Question: 2+2?\\nAnswer: 4\\n\"}}]}";
        using var handler = new StubHttpMessageHandler(json);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var provider = new AiQuestionProvider(
            client,
            Options.Create(new OpenAiOptions { ApiKey = "test", Model = "gpt-test" }),
            new LocalizationService(),
            NullLogger<AiQuestionProvider>.Instance);

        var players = new List<Player>
        {
            new() { Id = 1, DisplayName = "Alice", Status = PlayerStatus.Active }
        };

        var pool = await provider.PrepareQuestionPoolAsync(new[] { "Math" }, 1, 1, players, GameLanguage.English, CancellationToken.None);

        Assert.True(pool.ContainsKey(1));
        Assert.Single(pool[1]);
        Assert.Equal("Math", pool[1][0].Topic);
        Assert.Equal("2+2?", pool[1][0].Text);
        Assert.Equal("4", pool[1][0].Answer);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHttpMessageHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}

