using System.Text.Json.Nodes;
using DeskNest.Core.Flow;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ManualPromptFlowTests
{
    [Fact]
    public void FileControlAndAutomaticDefinitionsCannotReachNativeExecution()
    {
        string json = ManualFlowDefinitions.Create("manual", "title", "message");
        int callbacks = 0;
        Task<bool> Show(FlowPrompt _, CancellationToken __) { callbacks++; return Task.FromResult(true); }
        foreach (string type in new[] { "pogget.action.file.move", "pogget.action.file.map", "pogget.action.file.regexRename", "pogget.action.file.readText", "pogget.control.if" })
        {
            var document = JsonNode.Parse(json)!;
            document["actions"]![0]!["type"] = type;
            document["actions"]![0]!["enabled"] = false; // Even dormant file steps stay outside this restricted runner.
            Assert.Throws<NotSupportedException>(() => { _ = ManualPromptFlowRunner.RunAsync(document.ToJsonString(), Show, nativeLibraryPath: "missing"); });
        }
        var automatic = JsonNode.Parse(json)!;
        automatic["trigger"]!["type"] = "pogget.trigger.schedule";
        Assert.Throws<InvalidDataException>(() => { _ = ManualPromptFlowRunner.RunAsync(automatic.ToJsonString(), Show); });
        Assert.Throws<NotSupportedException>(() => { _ = ManualPromptFlowRunner.RunAsync(ManualFlowDefinitions.Create("manual", "title", "${file.path}"), Show); });
        Assert.Throws<OperationCanceledException>(() => { _ = ManualPromptFlowRunner.RunAsync(json, Show, new CancellationToken(true)); });
        Assert.Equal(0, callbacks);
    }

    [FlowNativeFact]
    public async Task NativePromptRunAcknowledgesInOrderAndDrainsOnCancelOrCallbackFailure()
    {
        string library = Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")!;
        var document = JsonNode.Parse(ManualFlowDefinitions.Create("manual", "标题", "你好 — مرحبا"))!;
        var second = document["actions"]![0]!.DeepClone();
        second["id"] = "second";
        second["parameters"]!["message"] = "second message";
        document["actions"]!.AsArray().Add(second);
        string json = document.ToJsonString();
        var messages = new List<string>();
        Assert.Equal(2, await ManualPromptFlowRunner.RunAsync(json, (prompt, _) =>
        { messages.Add(prompt.Message); return Task.FromResult(true); }, nativeLibraryPath: library));
        Assert.Equal(new[] { "你好 — مرحبا", "second message" }, messages);
        Assert.False(JsonNode.Parse(json)!["enabled"]!.GetValue<bool>());
        int declinedCalls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualPromptFlowRunner.RunAsync(json, (_, _) =>
        { declinedCalls++; return Task.FromResult(false); }, nativeLibraryPath: library));
        Assert.Equal(1, declinedCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManualPromptFlowRunner.RunAsync(json,
            (_, _) => throw new InvalidOperationException("callback failure"), nativeLibraryPath: library));
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ManualPromptFlowRunner.RunAsync(json, (_, _) => { entered.TrySetResult(); return pending.Task; }, cancelled.Token, library);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await ManualPromptFlowRunner.RunAsync(json, (_, _) => Task.FromResult(true), nativeLibraryPath: library));
    }
}
