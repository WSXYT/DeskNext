using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.Core.Flow;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    [ObservableProperty] private bool _isFlowPromptOpen;
    [ObservableProperty] private string _flowPromptTitle = string.Empty;
    [ObservableProperty] private string _flowPromptMessage = string.Empty;
    private TaskCompletionSource<bool>? _flowPromptDecision;
    private Task<int>? _promptRuntimeTask;

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task RunPromptFlowAsync(CancellationToken token)
    {
        if (IsFlowBusy) return;
        string draft = FlowJsonDraft;
        IsFlowBusy = true;
        FlowNotice = Localizer["Flow.RunningPrompts"];
        try
        {
            var validation = await Task.Run(() => ManualFlowDefinitions.Validate(draft), token);
            if (validation != FlowDefinitionValidation.Valid)
            {
                FlowNotice = Localizer[validation == FlowDefinitionValidation.NativeUnavailable ? "Flow.NativeUnavailable" : "Flow.Invalid"];
                return;
            }
            _promptRuntimeTask = ManualPromptFlowRunner.RunAsync(draft, ShowFlowPromptAsync, token);
            int completed = await _promptRuntimeTask;
            FlowNotice = Localizer.GetString("Flow.PromptsCompleted", completed);
        }
        catch (OperationCanceledException) { FlowNotice = Localizer["Flow.RunCancelled"]; }
        catch (NotSupportedException) { FlowNotice = Localizer["Flow.PromptOnly"]; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { FlowNotice = Localizer["Flow.NativeUnavailable"]; }
        catch (Exception error) { FlowNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally
        {
            _promptRuntimeTask = null;
            _flowPromptDecision?.TrySetResult(false);
            _flowPromptDecision = null;
            IsFlowPromptOpen = false;
            FlowPromptTitle = FlowPromptMessage = string.Empty;
            IsFlowBusy = false;
        }
    }

    private async Task<bool> ShowFlowPromptAsync(FlowPrompt prompt, CancellationToken token)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                _flowPromptDecision = decision;
                FlowPromptTitle = prompt.Title;
                FlowPromptMessage = prompt.Message;
                IsFlowPromptOpen = true;
            });
            return await decision.Task.WaitAsync(token);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_flowPromptDecision, decision)) return;
                _flowPromptDecision = null;
                IsFlowPromptOpen = false;
                FlowPromptTitle = FlowPromptMessage = string.Empty;
            });
        }
    }

    [RelayCommand]
    public void AcknowledgeFlowPrompt() => _flowPromptDecision?.TrySetResult(true);

    internal async Task StopPromptFlowAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        RunPromptFlowCommand.Cancel();
        _flowPromptDecision?.TrySetResult(false);
        IsFlowPromptOpen = false;
        var runtime = _promptRuntimeTask;
        if (runtime is null) return;
        // Await native draining, not the UI command continuation. The latter may need this dispatcher.
        try { await runtime.ConfigureAwait(false); }
        catch (Exception) { /* The command reports the run outcome; shutdown must still release its owner. */ }
    }
}
