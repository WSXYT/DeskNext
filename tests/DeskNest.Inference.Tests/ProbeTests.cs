using System.Buffers.Binary;
using System.Text.Json;
using DeskNest.Inference;
using Xunit;

namespace DeskNest.Inference.Tests;

public sealed class ProbeTests
{
    private static readonly Probe.Candidate[] Candidates =
    [
        new("design", "Design files"),
        new(Probe.Ambiguous, "Unknown filename"),
        new(Probe.Insufficient, "No matching category")
    ];

    private static Probe.Request Request(Probe.Candidate[]? candidates = null) =>
        new("request-1", 7, "设计稿.pdf", "Pick a category", candidates ?? Candidates);

    private static Probe.Tensors Tensors() => new([[2, 3, 1]], [[1, 1, 1]], [[1, 1, 1]], [[true, true, true]], [0]);

    private static JsonDocument Config() => JsonDocument.Parse("""{"temperature":[1,1,1],"temperature_by_options":{}}""");

    [Fact]
    public void InsufficientCategoryIsPendingAndKeepsSecondBestRealCategory()
    {
        using var config = Config();
        var result = Probe.Decide(Request(), Tensors(), [1, 0, 3], [2, 1], config.RootElement);
        Assert.Equal(Probe.Insufficient, result.Choice);
        Assert.Equal("design", result.BestReal);
        Assert.Equal("pending", result.Action);
        Assert.InRange(result.Probabilities.Sum(), 0.999999, 1.000001);
    }

    [Fact]
    public void AmbiguousFilenameIsPendingEvenWhenTopChoiceIsCertain()
    {
        using var config = Config();
        var result = Probe.Decide(Request(), Tensors(), [1, 8, 0], [2, 1], config.RootElement);
        Assert.Equal(Probe.Ambiguous, result.Choice);
        Assert.Equal("pending", result.Action);
    }

    [Fact]
    public void NumericTieNeverProposesAnAutomaticAction()
    {
        using var config = Config();
        var result = Probe.Decide(Request(), Tensors(), [2, 1.9999f, 0], [1, 1], config.RootElement);
        Assert.Equal("pending", result.Action);
        Assert.Throws<InvalidDataException>(() => Probe.Validate(Request([Candidates[0], Candidates[0], Candidates[2]])));
    }

    [Fact]
    public void PreviewRejectsStaleOrInconsistentResults()
    {
        using var config = Config();
        var request = Request();
        var result = Probe.Decide(request, Tensors(), [3, 1, 0], [1, 0], config.RootElement);
        LocalPreviewClient.ValidateResult(request, result);
        string modelHash = new string('A', 64);
        var reply = new Probe.WorkerReply(Probe.WorkerProtocolVersion, modelHash, result);
        LocalPreviewClient.ValidateReply(request, modelHash, reply);
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateReply(request, modelHash, reply with { ProtocolVersion = 0 }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateReply(request, modelHash, reply with { ModelManifestSha256 = new string('B', 64) }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateReply(request, modelHash, reply with { Result = null! }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateReply(request, modelHash, reply with { Result = result with { Revision = 8 } }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateResult(request, result with { Revision = 8 }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateResult(request, result with { Choice = Probe.Ambiguous }));
        Assert.Throws<InvalidDataException>(() => LocalPreviewClient.ValidateResult(request, result with { Probabilities = [double.NaN, 0, 0] }));
    }

    [Fact]
    public async Task PreviewRefusesOversizedOrCancelledInputBeforeStartingAProcess()
    {
        var start = new System.Diagnostics.ProcessStartInfo("must-not-be-launched");
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalPreviewClient.RunAsync(start, Path.GetTempPath(),
            Request() with { State = new string('x', 1024 * 1024) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalPreviewClient.RunAsync(start, Path.GetTempPath(),
            Request(), new CancellationToken(true)));
        Assert.ThrowsAny<OperationCanceledException>(() => Probe.VerifyModel("missing-model", new CancellationToken(true)));
    }

    [Fact]
    public void VersionedWorkerRejectsProtocolAndModelMismatchWithoutInference()
    {
        void Refuses(Probe.WorkerRequest envelope, string directory)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var input = new MemoryStream();
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
            input.Write(header);
            input.Write(data);
            input.Position = 0;
            using var output = new MemoryStream();
            Assert.Throws<InvalidDataException>(() => Probe.Worker(directory, input, output, versioned: true));
            Assert.Empty(output.ToArray());
        }
        var valid = new Probe.WorkerRequest(Probe.WorkerProtocolVersion, new string('0', 64), Request());
        Refuses(valid with { ProtocolVersion = 2 }, "missing-model");
        Refuses(valid with { ModelManifestSha256 = "invalid" }, "missing-model");
        Refuses(valid with { Request = null! }, "missing-model");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-worker-version-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "manifest.json"), "{}");
            // This is not an inference model: mismatched identity must stop before model parsing/loading.
            Refuses(valid, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WorkerRejectsOversizedFrameBeforeModelAccess()
    {
        Span<byte> frame = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1024 * 1024 + 1);
        using var input = new MemoryStream(frame.ToArray());
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => Probe.Worker("missing-model", input, output));
        Assert.Empty(output.ToArray());
    }
}
