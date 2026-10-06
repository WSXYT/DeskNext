namespace DeskNest.Core.Flow;

/// <summary>Prompt-only entry retained for callers without file-operation authority.</summary>
public static class ManualPromptFlowRunner
{
    public static Task<int> RunAsync(string json, Func<FlowPrompt, CancellationToken, Task<bool>> showPrompt,
        CancellationToken token = default, string? nativeLibraryPath = null) =>
        ManualFlowRunner.RunAsync(json, showPrompt, token, nativeLibraryPath);
}
