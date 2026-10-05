using System.Net.Http.Headers;
using System.Text.Json;

namespace DeskNest.Inference;

/// <summary>One explicit, read-only Jev request. No filesystem or workspace authority.</summary>
public static class JevPreviewClient
{
    public const string Model = "jev-1.13.0";
    private const int MaximumBytes = 1024 * 1024;
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    public sealed record Result(string Model, string Choice, double[] Probabilities);

    public static Task<Result> RunAsync(string apiKey, Probe.Request request, CancellationToken token = default) =>
        RunAsync(DefaultClient, apiKey, request, token);

    // Retry only an explicit rate-limit refusal, not an ambiguous network/server failure or a malformed answer.
    private static async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(HttpClient client, string key, byte[] body, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == 2) return response;
            var retry = response.Headers.RetryAfter;
            TimeSpan delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromSeconds(10)) return response; // Do not shorten the server's requested wait.
            response.Dispose();
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    public static async Task<Result> RunAsync(HttpClient client, string apiKey, Probe.Request request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Probe.Validate(request);
        if (request.Candidates.Length > 255) throw new InvalidDataException("Jev supports at most 255 candidates.");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("A valid session API key is required.");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = Model,
            state = request.State,
            questions = new Dictionary<string, object>
            {
                [request.RequestId] = new
                {
                    type = "choice", instructions = request.Instructions,
                    criteria = request.Candidates.ToDictionary(c => c.Id, c => c.Description, StringComparer.Ordinal)
                }
            }
        });
        if (body.Length > MaximumBytes) throw new InvalidDataException("Jev preview request exceeds 1 MiB.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await SendWithRateLimitRetryAsync(client, apiKey, body, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Jev preview failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            await response.Content.LoadIntoBufferAsync(MaximumBytes, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false));
            var root = document.RootElement;
            var answer = root.GetProperty("answers").GetProperty(request.RequestId);
            if (root.GetProperty("model").GetString() != Model || answer.GetProperty("type").GetString() != "choice")
                throw new InvalidDataException("Unexpected Jev model or answer type.");
            var map = answer.GetProperty("probabilities").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
            if (map.Count != request.Candidates.Length || request.Candidates.Any(c => !map.ContainsKey(c.Id)))
                throw new InvalidDataException("Jev returned a different candidate set.");
            var probabilities = request.Candidates.Select(c => map[c.Id]).ToArray();
            string? choice = answer.GetProperty("choice").GetString();
            if (probabilities.Any(p => !double.IsFinite(p) || p < 0 || p > 1) || Math.Abs(probabilities.Sum() - 1) > 1e-6 ||
                choice is null || !map.TryGetValue(choice, out double selected) || selected != probabilities.Max())
                throw new InvalidDataException("Jev returned an inconsistent probability distribution.");
            return new Result(Model, choice, probabilities);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Jev preview exceeded 45 seconds."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Jev returned an invalid response schema."); }
    }
}
