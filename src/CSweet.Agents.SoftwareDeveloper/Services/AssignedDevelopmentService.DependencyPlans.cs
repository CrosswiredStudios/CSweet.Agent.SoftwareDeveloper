using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed partial class AssignedDevelopmentService
{
    // Keep the exact hash-verified source available for reference without replaying a whole
    // research document in every model turn. Short documents still travel inline.
    internal const int DocumentPageCharacters = 4096;
    internal static AIFunction DependencyReader(IReadOnlyList<DependencyPlan> plans) =>
        AIFunctionFactory.Create((Guid revisionId, int offset, string? find) =>
        {
            var document = plans.SingleOrDefault(p => p.RevisionId == revisionId);
            if (document is null || offset < 0 || offset > document.Content.Length)
                return "Unknown assignment revision or invalid character offset.";
            if (!string.IsNullOrWhiteSpace(find))
            {
                offset = document.Content.IndexOf(find, offset, StringComparison.OrdinalIgnoreCase);
                if (offset < 0) return "Text not found.";
            }
            var length = Math.Min(DocumentPageCharacters, document.Content.Length - offset);
            return $"Revision {revisionId:D}, characters {offset} through {offset + length} of {document.Content.Length}:\n" +
                document.Content.Substring(offset, length);
        }, "read_dependency_document", "Read the exact approved assignment dependency by revisionId and zero-based character offset. Optionally find a heading or phrase from that offset.");

    internal static IReadOnlyList<DependencyPlan> ReadDependencyPlans(WorkExecutionAssignment assignment, WorkItem item)
    {
        var references = (assignment.Evidence ?? []).Where(x => x.Kind == "dependency-document.v1").ToArray();
        if (references.Length > 16) throw new OperationalDevelopmentException("Too many dependency documents in the assignment.");
        var allowed = (item.Planning?.DependencyItemIds ?? item.Delivery?.DependencyItemIds ?? []).ToHashSet();
        var result = new List<DependencyPlan>();
        var revisions = new HashSet<Guid>();
        var size = 0;
        foreach (var reference in references)
        {
            DependencyPlan? plan;
            try { plan = JsonSerializer.Deserialize<DependencyPlan>(reference.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException error) { throw new OperationalDevelopmentException("Invalid dependency document evidence.", error); }
            if (plan is null || !allowed.Contains(plan.SourceWorkItemId) || plan.StageExecutionId == Guid.Empty || plan.AttemptId == Guid.Empty ||
                plan.ArtifactId == Guid.Empty || plan.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(plan.Content) ||
                !revisions.Add(plan.RevisionId) || !string.Equals(plan.Sha256,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Content))), StringComparison.OrdinalIgnoreCase))
                throw new OperationalDevelopmentException("Dependency plan does not match its declared ticket, exact revision and content hash.");
            size += Encoding.UTF8.GetByteCount(plan.Content);
            if (size > 262144) throw new OperationalDevelopmentException("Dependency documents exceed the assignment context limit.");
            result.Add(plan);
        }
        return result;
    }
}

internal sealed record DependencyPlan(Guid SourceWorkItemId, Guid StageExecutionId, Guid AttemptId,
    Guid ArtifactId, Guid RevisionId, string Sha256, string Title, string Content);
