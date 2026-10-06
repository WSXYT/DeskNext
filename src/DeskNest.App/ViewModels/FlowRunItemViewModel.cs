using DeskNest.App.Localization;
using DeskNest.Core.Flow;

namespace DeskNest.App.ViewModels;

public enum FlowRunOutcome { Running, Completed, Cancelled, Failed }

/// <summary>Bounded session display. File-operation history remains the durable move/undo authority.</summary>
public sealed class FlowRunItemViewModel : ViewModelBase
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public string Name { get; internal set; } = string.Empty;
    public FlowRunOutcome Outcome { get; private set; } = FlowRunOutcome.Running;
    public IReadOnlyList<FlowStepCompletion> CompletedSteps { get; private set; } = [];
    public string Notice { get; private set; } = string.Empty;
    public string StartedText => StartedAt.ToLocalTime().ToString("HH:mm:ss");
    public string StatusText => LocalizationManager.Instance["Flow.Log" + Outcome];
    public string StepsText => string.Join(Environment.NewLine, CompletedSteps.Select(step =>
        LocalizationManager.Instance.GetString("Flow.CompletedStep", step.Sequence,
            LocalizationManager.Instance[step.IsMove ? "Flow.StepMove" : "Flow.StepTip"])));
    public string Summary => $"{StartedText} · {Name} · {StatusText}";

    internal void Finish(FlowRunOutcome outcome, IEnumerable<FlowStepCompletion> completed, string notice)
    {
        Outcome = outcome;
        CompletedSteps = completed.Take(100).ToArray();
        Notice = notice.Length <= 8192 ? notice : notice[..8192];
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(CompletedSteps));
        RefreshLanguage();
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StepsText));
    }
}
