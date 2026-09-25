using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskNest.App.Localization;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class OperationItemViewModel : ViewModelBase
{
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
    public bool IsRecoveryRequired => Status == ProposedOperationStatus.RecoveryRequired;

    public bool HasSourcePath => !string.IsNullOrWhiteSpace(SourcePath);
    public bool HasDestinationPath => !string.IsNullOrWhiteSpace(DestinationPath);

    public string StatusLocalized => Status switch
    {
        ProposedOperationStatus.Proposed => Localizer["Operations.StatusProposed"],
        ProposedOperationStatus.PendingUser => Localizer["Operations.StatusPendingUser"],
        ProposedOperationStatus.Completed => Localizer["Operations.StatusCompleted"],
        ProposedOperationStatus.RecoveryRequired => Localizer["Operations.StatusRecoveryRequired"],
        _ => Status.ToString()
    };

    public string StatusDescription => Status switch
    {
        ProposedOperationStatus.Proposed => Localizer["Operations.DescProposed"],
        ProposedOperationStatus.PendingUser => Localizer["Operations.DescPendingUser"],
        ProposedOperationStatus.Completed => Localizer["Operations.DescCompleted"],
        ProposedOperationStatus.RecoveryRequired => Localizer["Operations.DescRecoveryRequired"],
        _ => string.Empty
    };

    public string RecoveryActionPrompt => Localizer["Operations.RecoveryRequiredAction"];
    public string NoUndoNotice => Localizer["Operations.NoUndoNotice"];

    public OperationItemViewModel(
        ProposedOperation operation,
        string fileName,
        string? targetSpaceName)
    {
        Id = operation.Id;
        FileId = operation.FileId;
        TargetSpaceId = operation.TargetSpaceId;
        Status = operation.Status;
        CreatedAt = operation.CreatedAt;
        SourcePath = operation.SourcePath;
        DestinationPath = operation.DestinationPath;
        FileName = string.IsNullOrWhiteSpace(fileName) ? operation.FileId.ToString() : fileName;
        TargetSpaceName = targetSpaceName ?? string.Empty;

        Localizer.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusLocalized));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(RecoveryActionPrompt));
            OnPropertyChanged(nameof(NoUndoNotice));
        };
    }
}
