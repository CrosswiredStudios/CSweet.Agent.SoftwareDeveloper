namespace CSweet.Agents.SoftwareDeveloper;

internal static class GeneratedCommitMessage
{
    internal static string Create(string title)
    {
        var subject = string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (subject.Length == 0) throw new ArgumentException("A commit subject is required.", nameof(title));
        const string prefix = "feat(task): ";
        return prefix + (subject.Length > 512 - prefix.Length ? subject[..(512 - prefix.Length)].TrimEnd() : subject);
    }
}
