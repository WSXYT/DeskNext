using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ModelDecisionMessagesTests
{
    private static (WorkspaceState State, ModelDecisionRequest Request) Fixture()
    {
        var category = new WorkspaceSpace(Guid.NewGuid(), "Documents", "", SpaceStorageMode.Managed,
            Path.Combine(Path.GetTempPath(), "DeskNest", "Documents"));
        var pending = new PendingFile(Guid.NewGuid(), "draft", Path.Combine(Path.GetTempPath(), "draft"),
            TriageReason.FilenameAmbiguous, null, DateTimeOffset.UtcNow);
        var state = new WorkspaceState { Revision = 7, Spaces = [category], Pending = [pending] };
        var request = new ModelDecisionRequest(ModelDecisionMessages.SchemaVersion, "request-1", pending.Id,
            state.Revision, "model-sha256", "tokenizer-sha256",
            [new(category.Id.ToString(), "Documents"), new(ModelDecisionMessages.Ambiguous, ""),
                new(ModelDecisionMessages.Insufficient, "")]);
        return (state, request);
    }

    [Fact]
    public void OnlyMatchedCurrentScoresCanProduceMetadataProposal()
    {
        var (state, request) = Fixture();
        var reply = new ModelDecisionReply(1, request.RequestId, request.PendingId, 7, request.ModelVersion,
            request.TokenizerVersion, request.Candidates[0].Id, [0.8, 0.1, 0.1]);
        var outcome = ModelDecisionMessages.Evaluate(state, request, reply);
        Assert.Equal(ModelDecisionDisposition.Proposed, outcome?.Disposition);
        Assert.Equal(state.Spaces[0].Id, outcome?.SuggestedSpaceId);
        Assert.Null(outcome?.Reason);
        Assert.Null(ModelDecisionMessages.Evaluate(state with { Revision = 8 }, request, reply));
        Assert.Null(ModelDecisionMessages.Evaluate(state with { Pending = [] }, request, reply));
        Assert.Null(ModelDecisionMessages.Evaluate(state, request, reply with { RequestId = "late" }));
        Assert.Null(ModelDecisionMessages.Evaluate(state, request, reply with { TokenizerVersion = "changed" }));
        Assert.Empty(state.Operations); // A proposal is never an executed file operation.
    }

    [Fact]
    public void SpecialChoicesAndNearTiesAlwaysStayPending()
    {
        var (state, request) = Fixture();
        var template = new ModelDecisionReply(1, request.RequestId, request.PendingId, 7, request.ModelVersion,
            request.TokenizerVersion, ModelDecisionMessages.Ambiguous, [0.2, 0.7, 0.1]);
        var ambiguous = ModelDecisionMessages.Evaluate(state, request, template);
        Assert.Equal(ModelDecisionDisposition.Pending, ambiguous?.Disposition);
        Assert.Equal(TriageReason.FilenameAmbiguous, ambiguous?.Reason);
        Assert.Equal(state.Spaces[0].Id, ambiguous?.SuggestedSpaceId);
        var insufficient = ModelDecisionMessages.Evaluate(state, request, template with
        {
            ChoiceId = ModelDecisionMessages.Insufficient, Probabilities = [0.2, 0.1, 0.7]
        });
        Assert.Equal(TriageReason.CategoriesInsufficient, insufficient?.Reason);
        var tie = ModelDecisionMessages.Evaluate(state, request, template with
        {
            ChoiceId = request.Candidates[0].Id, Probabilities = [0.50001, 0.49999, 0]
        });
        Assert.Equal(TriageReason.NearTie, tie?.Reason);
    }

    [Fact]
    public void InvalidMessagesFailClosed()
    {
        var (state, request) = Fixture();
        var reply = new ModelDecisionReply(1, request.RequestId, request.PendingId, 7, request.ModelVersion,
            request.TokenizerVersion, request.Candidates[0].Id, [0.8, 0.1, 0.1]);
        Assert.Throws<InvalidDataException>(() => ModelDecisionMessages.Evaluate(state, request,
            reply with { Probabilities = [0.8, double.NaN, 0.2] }));
        Assert.Throws<InvalidDataException>(() => ModelDecisionMessages.Evaluate(state, request,
            reply with { ChoiceId = ModelDecisionMessages.Ambiguous }));
        Assert.Throws<InvalidDataException>(() => ModelDecisionMessages.Evaluate(state,
            request with { SchemaVersion = 2 }, reply));
        Assert.Null(ModelDecisionMessages.Evaluate(state with { Spaces = [] }, request, reply));
    }

    [Fact]
    public void ReusedRequestIdCannotCrossPendingItemBoundary()
    {
        var (state, request) = Fixture();
        var second = new PendingFile(Guid.NewGuid(), "second", Path.Combine(Path.GetTempPath(), "second"),
            TriageReason.FilenameAmbiguous, null, DateTimeOffset.UtcNow);
        state = state with { Pending = [.. state.Pending, second] };
        var reply = new ModelDecisionReply(1, request.RequestId, request.PendingId, 7,
            request.ModelVersion, request.TokenizerVersion, request.Candidates[0].Id, [0.8, 0.1, 0.1]);
        Assert.Null(ModelDecisionMessages.Evaluate(state, request with { PendingId = second.Id }, reply));
        Assert.Equal(ModelDecisionDisposition.Proposed,
            ModelDecisionMessages.Evaluate(state, request, reply)?.Disposition);
    }

    [Fact]
    public void AlternateGuidFormatsCannotDuplicateOneCategory()
    {
        var (state, request) = Fixture();
        var duplicate = request with { Candidates = [
            request.Candidates[0], new(state.Spaces[0].Id.ToString("B"), "same space"),
            request.Candidates[1], request.Candidates[2] ] };
        var reply = new ModelDecisionReply(1, request.RequestId, request.PendingId, 7,
            request.ModelVersion, request.TokenizerVersion, request.Candidates[0].Id, [0.8, 0.1, 0.05, 0.05]);
        Assert.Throws<InvalidDataException>(() => ModelDecisionMessages.Evaluate(state, duplicate, reply));
    }
}
