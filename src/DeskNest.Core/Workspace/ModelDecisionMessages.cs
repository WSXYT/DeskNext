namespace DeskNest.Core.Workspace;

// P2 metadata-only handoff. The P1 worker has a separate probe protocol;
// production worker negotiation and transport are deliberately deferred to P4.
public sealed record ModelCandidate(string Id, string Description);

public sealed record ModelDecisionRequest(
    int SchemaVersion, string RequestId, Guid PendingId, long WorkspaceRevision,
    string ModelVersion, string TokenizerVersion, IReadOnlyList<ModelCandidate> Candidates);

public sealed record ModelDecisionReply(
    int SchemaVersion, string RequestId, Guid PendingId, long WorkspaceRevision,
    string ModelVersion, string TokenizerVersion, string ChoiceId,
    IReadOnlyList<double> Probabilities);

public enum ModelDecisionDisposition { Pending, Proposed }

public sealed record ModelDecisionOutcome(
    ModelDecisionDisposition Disposition, TriageReason? Reason, Guid? SuggestedSpaceId);

public static class ModelDecisionMessages
{
    public const int SchemaVersion = 1;
    public const string Ambiguous = "filename-ambiguous";
    public const string Insufficient = "categories-insufficient";

    /// <summary>Returns null for a stale/mismatched response. Never changes workspace state or user files.</summary>
    public static ModelDecisionOutcome? Evaluate(
        WorkspaceState state, ModelDecisionRequest request, ModelDecisionReply reply)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reply);

        if (request.SchemaVersion != SchemaVersion || reply.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("Unsupported decision message schema");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
            request.PendingId == Guid.Empty || request.WorkspaceRevision < 0 ||
            string.IsNullOrWhiteSpace(request.ModelVersion) || request.ModelVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(request.TokenizerVersion) || request.TokenizerVersion.Length > 128 ||
            request.Candidates is null || request.Candidates.Count is < 3 or > 256 ||
            request.Candidates.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128 || c.Description is null) ||
            request.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count ||
            request.Candidates.Count(c => c.Id == Ambiguous) != 1 ||
            request.Candidates.Count(c => c.Id == Insufficient) != 1)
            throw new InvalidDataException("Invalid decision request");

        // A settings/category edit, dismissed pending item, new model, or a delayed response
        // invalidates the request. Never let a stale result authorize even a proposed action.
        if (state.Revision != request.WorkspaceRevision ||
            !state.Pending.Any(p => p.Id == request.PendingId) ||
            reply.RequestId != request.RequestId || reply.PendingId != request.PendingId ||
            reply.WorkspaceRevision != request.WorkspaceRevision ||
            reply.ModelVersion != request.ModelVersion || reply.TokenizerVersion != request.TokenizerVersion)
            return null;

        var realCandidates = request.Candidates.Where(c => c.Id != Ambiguous && c.Id != Insufficient)
            .Select(c => Guid.TryParse(c.Id, out var id) ? id : Guid.Empty).ToArray();
        var realIds = realCandidates.ToHashSet();
        if (realIds.Contains(Guid.Empty) || realIds.Count != realCandidates.Length)
            throw new InvalidDataException("Invalid or duplicate category candidates");
        if (realIds.Count != state.Spaces.Count || !state.Spaces.All(s => realIds.Contains(s.Id)))
            return null; // Category choices changed or do not represent the snapshot.

        if (reply.Probabilities is null || reply.Probabilities.Count != request.Candidates.Count ||
            reply.Probabilities.Any(p => !double.IsFinite(p) || p is < 0 or > 1) ||
            Math.Abs(reply.Probabilities.Sum() - 1) > 1e-6)
            throw new InvalidDataException("Invalid decision probabilities");

        var ranked = Enumerable.Range(0, reply.Probabilities.Count)
            .OrderByDescending(i => reply.Probabilities[i])
            .ThenBy(i => request.Candidates[i].Id, StringComparer.Ordinal).ToArray();
        var winner = request.Candidates[ranked[0]].Id;
        if (reply.ChoiceId != winner)
            throw new InvalidDataException("Decision choice does not match calibrated scores");

        var bestReal = ranked.Select(i => request.Candidates[i].Id)
            .FirstOrDefault(id => id != Ambiguous && id != Insufficient);
        Guid? suggestion = bestReal is null ? null : Guid.Parse(bestReal);
        bool nearTie = reply.Probabilities[ranked[0]] - reply.Probabilities[ranked[1]] <= 0.0002;
        if (nearTie || winner is Ambiguous or Insufficient)
            return new ModelDecisionOutcome(ModelDecisionDisposition.Pending,
                winner == Ambiguous ? TriageReason.FilenameAmbiguous :
                winner == Insufficient ? TriageReason.CategoriesInsufficient : TriageReason.NearTie,
                suggestion);
        return new ModelDecisionOutcome(ModelDecisionDisposition.Proposed, null, suggestion);
    }
}
