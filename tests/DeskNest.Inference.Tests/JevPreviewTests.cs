using System.Net;
using System.Text.Json;
using DeskNest.Inference;
using Xunit;

namespace DeskNest.Inference.Tests;

public sealed class JevPreviewTests
{
    private static Probe.Request Request() => new("preview-1", 7, "设计稿.pdf", "Pick a category",
        [new("design", "Design files"), new(Probe.Ambiguous, "Unclear name"), new(Probe.Insufficient, "No fit")]);
    private static string Reply(string choice = "design", double score = 0.8, string id = "preview-1") =>
        JsonSerializer.Serialize(new { model = JevPreviewClient.Model, answers = new Dictionary<string, object>
        { [id] = new { type = "choice", choice, probabilities = new Dictionary<string, double>
            { [Probe.Insufficient] = 0.05, ["design"] = score, [Probe.Ambiguous] = 0.15 } } } });

    [Fact]
    public async Task ChoiceWireContractMapsProbabilitiesByIdNotResponseOrder()
    {
        using var client = new HttpClient(new Handler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.typesafe.ai/v1/systemone", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-key", message.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(JevPreviewClient.Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal("设计稿.pdf", body.RootElement.GetProperty("state").GetString());
            var question = body.RootElement.GetProperty("questions").GetProperty("preview-1");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.Equal(3, question.GetProperty("criteria").EnumerateObject().Count());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply()) };
        }));
        var result = await JevPreviewClient.RunAsync(client, "fixture-key", Request());
        Assert.Equal("design", result.Choice);
        Assert.Equal(new[] { 0.8, 0.15, 0.05 }, result.Probabilities);
    }

    [Theory]
    [InlineData("filename-ambiguous", 0.8, "preview-1")]
    [InlineData("design", 0.9, "preview-1")]
    [InlineData("design", 0.8, "another-request")]
    public async Task InvalidChoiceDistributionOrQuestionCannotBecomeAPreview(string choice, double score, string id)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Reply(choice, score, id)) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request()));
    }

    [Fact]
    public async Task FailureDoesNotEchoResponseBodyOrRetryPost()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("secret-not-returned fixture-key") });
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request()));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("fixture-key", error.ToString());
        Assert.DoesNotContain("secret-not-returned", error.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledOrOversizedInputDoesNotSendAndResponseIsBounded()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', 1024 * 1024 + 1)) });
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request(), new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request() with { State = new string('x', 1024 * 1024) }));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<HttpRequestException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request()));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitRetryPreservesRequestAndStopsAtThreeAttempts(bool alwaysLimited)
    {
        int calls = 0;
        string? original = null;
        using var client = new HttpClient(new Handler(async (message, token) =>
        {
            calls++;
            string body = await message.Content!.ReadAsStringAsync(token);
            original ??= body;
            Assert.Equal(original, body);
            var response = new HttpResponseMessage(alwaysLimited || calls < 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                { Content = new StringContent(Reply()) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }));
        if (alwaysLimited)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request()));
            Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
            Assert.Equal(3, calls);
        }
        else
        {
            Assert.Equal("design", (await JevPreviewClient.RunAsync(client, "fixture-key", Request())).Choice);
            Assert.Equal(2, calls);
        }
    }

    [Theory]
    [InlineData(429, 60)]
    [InlineData(503, 0)]
    public async Task LongServerWaitAndAmbiguousFailureDoNotRetry(int status, int seconds)
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationDuringRateLimitWaitPreventsAnotherRequest()
    {
        using var stop = new CancellationTokenSource();
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            stop.CancelAfter(25);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JevPreviewClient.RunAsync(client, "fixture-key", Request(), stop.Token));
        Assert.Equal(1, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token);
    }
}
