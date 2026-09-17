using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    private static string PersonalBoardUrl(string businessId, PersonalTodoItem item) =>
        $"/organizations/{businessId}/employees/{item.OwnerOrganizationUserId:D}?tab=personal-board";

    internal static string ReviewDeliveryMessage(string title, string url, DateTimeOffset expiry,
        string sourceUrl, string boardUrl) => $"""
Your review build is running: **[{url}]({url})**

I've built and started “{BlockerExcerpt(title, 160)}” and confirmed that its web page responds. Please try it and let me know what you'd like changed.

{(expiry == DateTimeOffset.MaxValue
    ? "Open this link on the computer hosting the test build. It stays available until the instance is stopped or access is removed."
    : $"Open this link on the computer hosting the test build. Access expires on {expiry.UtcDateTime:MMM d, yyyy 'at' HH:mm 'UTC'}.")}

[View source]({sourceUrl}) · [Test notes and task details]({boardUrl})
""";

    internal static string DevelopmentBlockerChatMessage(Exception error, string title, string boardUrl)
    {
        var cause = error is PlatformCapabilityException platform && platform.Capability.StartsWith("platform.llm", StringComparison.Ordinal)
            ? "My model service couldn't finish the request. Please ask your administrator to check the model connection, then move the blocked ticket to To Do so I can continue."
            : error is PlatformCapabilityException { Code: PlatformCapabilityErrorCode.Denied }
                ? "I don't have permission for a step this task needs. Please ask your administrator to review the permission shown on the blocked ticket, then move it to To Do."
                : error is PlanValidationException
                    ? $"The checks still failed after my repair attempts: {BlockerExcerpt(error.Message, 240)}\n\nPlease review the failing check on the ticket. Once the cause is addressed, move it to To Do so I can try again."
                    : error is ImplementationOutcomeException
                        ? "I couldn't verify the completion report after my repair attempts. The ticket explains which evidence is missing. Please review it before moving the ticket to To Do for another attempt."
                        : $"{BlockerExcerpt(error.Message, 240)}\n\nI've put the diagnostic and recovery steps on the ticket. Please review those before retrying; I haven't confirmed a review build for this attempt.";
        return $"I'm blocked on “{BlockerExcerpt(title, 160)}”. {cause}\n\n[See the blocker on my board]({boardUrl})";
    }

    // Run the actual project tests in the cached Node runtime, even when the author's
    // workspace has no Node. A missing test script is not successful validation.
    internal static string NodeDeploymentValidationScript(int attempt, int diagnosticCharacters) => $$"""
if [ -f source-{{attempt}}/package.json ]; then
  set +e
  docker run --rm --pull=never --network=none --entrypoint /bin/sh \
    -v "$PWD/source-{{attempt}}:/source:ro" csweet/node:22 -c '
      set -eu
      mkdir -p /tmp/validation
      cp -R /source/. /tmp/validation/
      cd /tmp/validation
      node -e "const p=require(\"./package.json\"); if (!p.scripts || typeof p.scripts.test !== \"string\" || !p.scripts.test.trim()) { console.error(\"Node validation requires a package.json test script.\"); process.exit(1); }"
      npm --offline test
    ' > tests-{{attempt}}.log 2>&1
  test_exit=$?
  set -e
  if [ "$test_exit" -ne 0 ]; then
    printf 'Node test suite failed (exit %s).\n' "$test_exit" >&2
    tail -c {{Math.Clamp(diagnosticCharacters, 1, 7000)}} tests-{{attempt}}.log >&2
    exit "$test_exit"
  fi
  echo 'Node project test suite passed in the deployment runtime.'
fi
""";
}
