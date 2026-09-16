using System.Text;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class ImplementationOutcomeTests
{
    internal const string VerifiedUnchanged = """
        {"summary":"Verified retained Dockerfile against this ticket; focused checks pass.",
         "changedFiles":[],
         "validations":[{"command":"python3 -m unittest tests.test_dockerfile","succeeded":true,"exitCode":0}],
         "remainingRisks":["Node runtime tests have not been run."]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepts_fresh_validation_of_unchanged_work_with_or_without_utf8_bom(bool bom)
    {
        var root = CreateWorkspace();
        try
        {
            var path = Path.Combine(root, ".csweet", "outcome.json");
            await File.WriteAllTextAsync(path, VerifiedUnchanged, new UTF8Encoding(bom));
            var outcome = await SoftwareDeveloperAgent.ReadOutcomeAsync(root, default);
            Assert.Empty(outcome.ChangedFiles);
            Assert.True(Assert.Single(outcome.Validations).Succeeded);
            Assert.Contains("not been run", Assert.Single(outcome.RemainingRisks!));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{}", "summary")]
    [InlineData("null", "summary")]
    [InlineData("{broken", "valid JSON")]
    [InlineData("""{"summary":"Verified","validations":[]}""", "changedFiles")]
    [InlineData("""{"summary":"Verified","changedFiles":[null],"validations":[]}""", "changedFiles")]
    [InlineData("""{"summary":"Verified","changedFiles":[]}""", "validations")]
    [InlineData("""{"summary":"Verified","changedFiles":[],"validations":[]}""", "validations")]
    [InlineData("""{"summary":"Verified","changedFiles":[],"validations":[null]}""", "validations")]
    [InlineData("""{"summary":"Verified","changedFiles":[],"validations":[{"command":""}]}""", "validations")]
    public async Task Invalid_reports_identify_the_missing_field_instead_of_throwing_null_reference(string json, string field)
    {
        var root = CreateWorkspace();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".csweet", "outcome.json"), json);
            var error = await Assert.ThrowsAsync<SoftwareDeveloperAgent.ImplementationOutcomeException>(
                () => SoftwareDeveloperAgent.ReadOutcomeAsync(root, default));
            Assert.Contains(field, error.Message);
            Assert.StartsWith("The completion report", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".csweet"));
        return root;
    }
}
