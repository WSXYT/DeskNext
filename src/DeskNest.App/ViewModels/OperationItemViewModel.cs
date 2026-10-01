using System;
using System.ComponentModel;
using Avalonia.Utilities;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class OperationItemViewModel : ViewModelBase
{
    private readonly Func<Task>? _onUndo;

    public Guid Id { get; }
    public Guid FileId { get; }
    public Guid? TargetSpaceId { get; }
    public ProposedOperationStatus Status { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? SourcePath { get; }
    public string? DestinationPath { get; }
    public string FileName { get; }
    public string TargetSpaceName { get; }

    public LocalizationManager Localizer => LocalizationManager.Instance;

    public string FormattedCreatedAt => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public bool IsProposed => Status == ProposedOperationStatus.Proposed;
    public bool IsPendingUser => Status == ProposedOperationStatus.PendingUser;
    public bool IsCompleted => Status == ProposedOperationStatus.Completed;
    public bool IsUndone => Status == ProposedOperationStatus.Undone;
    public bool IsRecoveryRequired => Status == ProposedOperationStatus.RecoveryRequired;

    public bool CanUndo => Status == ProposedOperationStatus.Completed;

    public bool HasSourcePath => !string.IsNullOrWhiteSpace(SourcePath);
    public bool HasDestinationPath => !string.IsNullOrWhiteSpace(DestinationPath);

    public string StatusLocalized => Status switch
    {
        ProposedOperationStatus.Proposed => Localizer["Operations.StatusProposed"],
        ProposedOperationStatus.PendingUser => Localizer["Operations.StatusPendingUser"],
        ProposedOperationStatus.Completed => Localizer["Operations.StatusCompleted"],
        ProposedOperationStatus.Undone => Localizer["Operations.StatusUndone"],
        ProposedOperationStatus.RecoveryRequired => Localizer["Operations.StatusRecoveryRequired"],
        _ => Status.ToString()
    };

    public string StatusDescription => Status switch
    {
        ProposedOperationStatus.Proposed => Localizer["Operations.DescProposed"],
        ProposedOperationStatus.PendingUser => Localizer["Operations.DescPendingUser"],
        ProposedOperationStatus.Completed => Localizer["Operations.DescCompleted"],
        ProposedOperationStatus.Undone => Localizer["Operations.DescUndone"],
        ProposedOperationStatus.RecoveryRequired => Localizer["Operations.DescRecoveryRequired"],
        _ => string.Empty
    };

    public string RecoveryActionPrompt => Localizer["Operations.RecoveryRequiredAction"];
    public string NoUndoNotice => Localizer["Operations.NoUndoNotice"];

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_onUndo != null)
        {
            await _onUndo().ConfigureAwait(false);
        }
    }

    public OperationItemViewModel(
        ProposedOperation operation,
        string fileName,
        string? targetSpaceName,
        Func<Task>? onUndo = null)
    {
        _onUndo = onUndo;
        Id = operation.Id;
        FileId = operation.FileId;
        TargetSpaceId = operation.TargetSpaceId;
        Status = operation.Status;
        CreatedAt = operation.CreatedAt;
        SourcePath = operation.SourcePath;
        DestinationPath = operation.DestinationPath;
        FileName = string.IsNullOrWhiteSpace(fileName) ? operation.FileId.ToString() : fileName;
        TargetSpaceName = targetSpaceName ?? string.Empty;

        WeakEventHandlerManager.Subscribe<LocalizationManager, PropertyChangedEventArgs, OperationItemViewModel>(
            Localizer, nameof(LocalizationManager.PropertyChanged), OnLocalizerChanged);
    }

    private void OnLocalizerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.CurrentLanguage)) return;
        OnPropertyChanged(nameof(StatusLocalized));
        OnPropertyChanged(nameof(StatusDescription));
        OnPropertyChanged(nameof(RecoveryActionPrompt));
        OnPropertyChanged(nameof(NoUndoNotice));
    }
}
