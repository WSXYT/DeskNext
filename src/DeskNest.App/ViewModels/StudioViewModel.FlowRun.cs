using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.Core.Flow;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    public Action<FlowMove>? OnValidateFlowMove { get; set; }
    public Func<FlowMove, CancellationToken, Task<string>>? OnExecuteFlowMove { get; set; }
    [ObservableProperty] private bool _isFlowPromptOpen;
    [ObservableProperty] private string _flowPromptTitle = string.Empty;
    [ObservableProperty] private string _flowPromptMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlowPromptActionText))]
    private bool _isFlowMoveConfirmation;
    [ObservableProperty] private string _flowMoveSource = string.Empty;
    [ObservableProperty] private string _flowMoveDestination = string.Empty;
    public string FlowPromptActionText => Localizer[IsFlowMoveConfirmation ? "Files.ActionMoveToSpace" : "Flow.Continue"];
    private TaskCompletionSource<bool>? _flowPromptDecision;
    private Task<int>? _promptRuntimeTask;

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task RunManualFlowAsync(CancellationToken token)
    {
        if (IsFlowBusy) return;
        string draft = FlowJsonDraft;
        IsFlowBusy = true;
        FlowNotice = Localizer["Flow.RunningManual"];
        try
        {
            var validation = await Task.Run(() => ManualFlowDefinitions.Validate(draft), token);
            if (validation != FlowDefinitionValidation.Valid)
            {
                FlowNotice = Localizer[validation == FlowDefinitionValidation.NativeUnavailable ? "Flow.NativeUnavailable" : "Flow.Invalid"];
                return;
            }
            _promptRuntimeTask = ManualFlowRunner.RunAsync(draft, (prompt, t) => ShowFlowPromptAsync(prompt, t), token,
                executeMove: OnExecuteFlowMove, validateMove: OnValidateFlowMove);
            int completed = await _promptRuntimeTask;
            FlowNotice = Localizer.GetString("Flow.RunCompleted", completed);
        }
        catch (OperationCanceledException) { FlowNotice = Localizer["Flow.RunCancelled"]; }
        catch (NotSupportedException) { FlowNotice = Localizer["Flow.PromptOnly"]; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { FlowNotice = Localizer["Flow.NativeUnavailable"]; }
        catch (Exception error) { FlowNotice = Localizer.GetString("Flow.RunFailed", error.Message); }
        finally
        {
            _promptRuntimeTask = null;
            _flowPromptDecision?.TrySetResult(false);
            _flowPromptDecision = null;
            IsFlowPromptOpen = IsFlowMoveConfirmation = false;
            FlowPromptTitle = FlowPromptMessage = FlowMoveSource = FlowMoveDestination = string.Empty;
            IsFlowBusy = false;
        }
    }

    internal Task<bool> ConfirmFlowMoveAsync(FlowMoveReview review, CancellationToken token) =>
        ShowFlowPromptAsync(new(Localizer["Flow.MoveConfirmation"], Localizer["Triage.ImportNotice"]), token, review);

    private async Task<bool> ShowFlowPromptAsync(FlowPrompt prompt, CancellationToken token, FlowMoveReview? move = null)
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
                FlowMoveSource = move?.SourcePath ?? "";
                FlowMoveDestination = move?.DestinationPath ?? "";
                IsFlowMoveConfirmation = move is not null;
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
                IsFlowPromptOpen = IsFlowMoveConfirmation = false;
                FlowPromptTitle = FlowPromptMessage = FlowMoveSource = FlowMoveDestination = string.Empty;
            });
        }
    }

    [RelayCommand]
    public void AcknowledgeFlowPrompt() => _flowPromptDecision?.TrySetResult(true);

    internal async Task StopPromptFlowAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        RunManualFlowCommand.Cancel();
        _flowPromptDecision?.TrySetResult(false);
        IsFlowPromptOpen = false;
        var runtime = _promptRuntimeTask;
        if (runtime is null) return;
        // This raw task includes native callback drain and any already-started Core commit, not a queued UI continuation.
        try { await runtime.ConfigureAwait(false); }
        catch (Exception) { /* The command reports the outcome; shutdown must still release its owner. */ }
    }
}
