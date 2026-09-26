using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agents.SoftwareDeveloper;

// Compatibility with the Producer's existing production-planning wire protocol.
// These are proposals only; readiness, assignment and sprint activation remain platform-owned.
internal static class ProductionEstimation
{
    private const string RequestType = "video-game.production.role-estimate-request.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentCoordinationTurnResult? TryHandle(AgentCoordinationTurnRequest request)
    {
        var turn = request.Transcript.OrderByDescending(x => x.Ordinal)
            .FirstOrDefault(x => x.Artifact?.Type == RequestType);
        if (turn?.Artifact is not { } artifact) return null;
        if (request.SourceKind != "Board" || request.WorkContext?.WorkstreamId is null ||
            request.WorkContext.BoardId is not { } boardId ||
            turn.SpeakerOrganizationUserId != request.Counterpart.OrganizationUserId ||
            artifact.SchemaVersion != "1.0" || !artifact.IsFinalPage)
            return AgentCoordinationTurnResult.Blocked("Estimation requires a current counterpart-authored board request within a project.");
        ProductionEstimateRequest? scope;
        try { scope = artifact.Payload.Deserialize<ProductionEstimateRequest>(JsonOptions); }
        catch (JsonException) { return AgentCoordinationTurnResult.Blocked("The typed estimate request is malformed."); }
        if (scope is null || scope.BoardId != boardId || scope.PlanningRevision <= 0 ||
            string.IsNullOrWhiteSpace(scope.PlanningDigest) || string.IsNullOrWhiteSpace(scope.RequestFingerprint) ||
            scope.RequestFingerprint != artifact.Key || scope.RoleKey is not ("game-engineer" or "software-developer") ||
            scope.WorkItems is not { Count: > 0 and <= 200 } ||
            scope.WorkItems.Any(x => x is null || x.WorkItemId == Guid.Empty || x.AccountableRoleKey != scope.RoleKey ||
                string.IsNullOrWhiteSpace(x.Title) || !HasText(x.Requirements) || !HasText(x.AcceptanceCriteria) ||
                x.Constraints is null || x.DependencyWorkItemIds is null ||
                string.IsNullOrWhiteSpace(x.ArtifactPackageDigest) || string.IsNullOrWhiteSpace(x.AssignmentDecisionFingerprint)) ||
            scope.WorkItems.Select(x => x.WorkItemId).Distinct().Count() != scope.WorkItems.Count)
            return AgentCoordinationTurnResult.Blocked("The estimate request is stale, incomplete, or outside the developer's accountable role.");

        // Use the same initial sizing policy as the production specialist kit, with
        // explicit assumptions. A proposal is not an execution or calendar commitment.
        var estimates = scope.WorkItems.OrderBy(x => x.WorkItemId).Select(x => new ProductionItemEstimate(
            x.WorkItemId, Math.Clamp(1m + x.AcceptanceCriteria.Count + x.DependencyWorkItemIds.Count, 1m, 13m),
            x.Constraints.Count + x.DependencyWorkItemIds.Count > 3 ? "low" : "medium")).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope.RequestFingerprint, scope.RoleKey, scope.PlanningRevision, scope.PlanningDigest, Estimates = estimates
        }))).ToLowerInvariant();
        var proposal = new ProductionEstimateProposal(scope.BoardId, scope.RoleKey, scope.PlanningRevision,
            scope.PlanningDigest, estimates, estimates.Sum(x => x.EstimatePoints),
            ["Initial sizing uses one base point plus acceptance-criterion and dependency counts, capped at thirteen points per item.",
             "Offered capacity covers this candidate batch assuming no competing allocation, approved inputs, an available toolchain, and resolved dependencies. Producer scheduling and QA readiness are still required."], [], digest);
        return AgentCoordinationTurnResult.Completed($"Submitted {estimates.Length} developer-owned estimates for the exact candidate scope.",
            new AgentCoordinationArtifactSubmission("video-game.production.role-estimate-capacity-proposal.v1", "1.0",
                scope.RequestFingerprint, 1, true, JsonSerializer.SerializeToElement(proposal)));
    }

    private static bool HasText(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } && values.All(x => !string.IsNullOrWhiteSpace(x));
}

internal sealed record ProductionEstimateRequest(Guid BoardId, string RoleKey, long PlanningRevision,
    string PlanningDigest, IReadOnlyList<ProductionSprintCandidate> WorkItems, string RequestFingerprint);
internal sealed record ProductionSprintCandidate(Guid WorkItemId, string Title, string AccountableRoleKey,
    IReadOnlyList<string> Requirements, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<string> Constraints,
    IReadOnlyList<Guid> DependencyWorkItemIds, string ArtifactPackageDigest, string AssignmentDecisionFingerprint);
internal sealed record ProductionItemEstimate(Guid WorkItemId, decimal EstimatePoints, string Confidence);
internal sealed record ProductionEstimateProposal(Guid BoardId, string RoleKey, long PlanningRevision,
    string PlanningDigest, IReadOnlyList<ProductionItemEstimate> Estimates, decimal AvailableSprintCapacity,
    IReadOnlyList<string> Assumptions, IReadOnlyList<string> Blockers, string ProposalDigest);
