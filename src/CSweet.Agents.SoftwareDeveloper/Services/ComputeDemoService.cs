using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

internal sealed class ComputeDemoService(AgentSettings settings)
{
    internal const string DemoMarker = "csweet-linux-hello-v1";
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly AgentSettings _settings = settings;
    internal async Task<PersonalTodoResult> AdvanceAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var terms = JsonSerializer.Deserialize<DemoTerms>(item.Description, _serializerOptions);
            if (terms is null || terms.Kind != DemoMarker)
                return PersonalTodoResult.Blocked("The test-instance request is missing its approved terms.");
            var prefix = $"{DemoMarker}:{item.Id:N}";
            if (terms.WorkstreamId == Guid.Empty || string.IsNullOrWhiteSpace(terms.TemplateId))
            {
                var defaults = await context.Platform.Compute.GetDefaultsAsync(token);
                if (defaults.State == "Failed") return await BlockAsync("C-Sweet could not finish preparing Linux compute. " + defaults.ErrorCode);
                if (defaults is not { State: "Ready", WorkstreamId: { } workstreamId, TemplateId: { } templateId })
                    return Wait("C-Sweet is preparing the Linux test environment; approve the Windows administrator prompt if one appears.");
                terms = new(DemoMarker, workstreamId, templateId);
            }
            var environment = await context.Platform.Compute.ProvisionAsync(new(
                terms.WorkstreamId, prefix, prefix + ":provision",
                new ComputeSpecification("linux", "x64", terms.TemplateId, new(1, 1024, 20480), _settings.GetInt32("computeLifetimeSeconds", 0))), token);
            if (environment.State is "failed" or "destroying" or "destroyed" or "stopped" || environment.LeaseExpiresAt <= DateTimeOffset.UtcNow)
                return await BlockAsync("The Linux test instance is unavailable or expired. " + environment.FailureCode);
            if (environment.Generation == 1 && environment.State != "ready") return Wait("Waiting for Linux provisioning.");
            var script = BuildHelloScript(item.Id);
            // Stable IDs, payloads and expected generations survive duplicate wake delivery and agent restarts.
            var command = await context.Platform.Compute.ExecuteAsync(new(
                environment.Id, 1, prefix + ":run", new ComputeCommand(item.Id, "/bin/sh",
                    "/var/lib/csweet-compute/work", ["-c", script])), token);
            command = await context.Platform.Compute.ReadOperationAsync(command.Id, token);
            if (command.Status is "Blocked" or "Superseded") return await BlockAsync("Command could not run: " + command.FailureCode);
            if (command.Status != "Completed") return Wait("Waiting for the Hello World service to start.");
            if (command.Result?.Command is not { ExitCode: 0, TimedOut: false, ErrorCode: null } || command.Result.ErrorCode is not null)
                return await BlockAsync("The Hello World command failed or its outcome is unknown. I will not repeat it automatically.");
            var publication = await context.Platform.Compute.PublishPortAsync(new(environment.Id, 2, prefix + ":publish", 8080), token);
            publication = await context.Platform.Compute.ReadOperationAsync(publication.Id, token);
            if (publication.Status is "Blocked" or "Superseded") return await BlockAsync("Port publishing could not proceed: " + publication.FailureCode);
            if (publication.Status != "Completed") return Wait("Waiting for the app health check and test link.");
            if (publication.Result is not { ErrorCode: null, Url: { } url, UrlExpiresAt: { } expiry } || expiry <= DateTimeOffset.UtcNow ||
                !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "http" || address.Host != "127.0.0.1")
                return await BlockAsync("The provider did not return a current, verified test link.");
            var summary = $"Hello World is running: {url}\nOpen this link on the compute host machine. " + (expiry == DateTimeOffset.MaxValue ? "Available until the instance is released or access is revoked. " : $"It expires at {expiry:O}. ") + $"Environment: {environment.Id:D}. Source: /var/lib/csweet-compute/work/hello/app.py inside the VM.";
            if (item.SourceConversationId is { } chatId)
                await context.Platform.Communication.SendMessageAsync(chatId, summary, prefix + ":ready", token);
            return PersonalTodoResult.Completed(summary);

            PersonalTodoResult Wait(string reason) => PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(5), reason + " Compute events advance the task; this is a reconnect recovery deadline.");
            async Task<PersonalTodoResult> BlockAsync(string reason)
            {
                if (item.SourceConversationId is { } chatId)
                    await context.Platform.Communication.SendMessageAsync(chatId, reason, prefix + ":blocked", token);
                return PersonalTodoResult.Blocked(reason);
            }
        }
        catch (PlatformCapabilityException error)
        {
            var reason = $"Linux test-instance capability {error.Capability} is unavailable ({error.Code}). C-Sweet could not authorize this test environment.";
            if (item.SourceConversationId is { } chatId)
                await context.Platform.Communication.SendMessageAsync(chatId, reason, $"{DemoMarker}:{item.Id:N}:authority-blocked", token);
            return PersonalTodoResult.Blocked(reason);
        }
        catch (JsonException) { return PersonalTodoResult.Blocked("Test-instance state was invalid."); }
    }

    internal static string BuildHelloScript(Guid id)
    {
        const string source = """
from http.server import BaseHTTPRequestHandler, HTTPServer
class App(BaseHTTPRequestHandler):
    def do_GET(self):
        body = b'<!doctype html><html><head><title>Hello World</title></head><body><h1>Hello World!</h1><p>Running inside an isolated Linux test instance.</p></body></html>'
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
HTTPServer(('127.0.0.1', 8080), App).serve_forever()
""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        return $"set -eu\nmkdir -p /var/lib/csweet-compute/work/hello\nprintf '%s' '{encoded}' | /usr/bin/base64 -d > /var/lib/csweet-compute/work/hello/app.py\n" +
            $"/usr/bin/systemd-run --quiet --service-type=exec --unit=csweet-hello-{id:N} -- /usr/bin/python3 /var/lib/csweet-compute/work/hello/app.py\n" +
            "/usr/bin/python3 - <<'PY'\nimport time, urllib.request\nfor attempt in range(40):\n try:\n  response=urllib.request.urlopen('http://127.0.0.1:8080/', timeout=1)\n  assert b'Hello World' in response.read(4096)\n  print('Hello World HTTP health check passed')\n  break\n except Exception:\n  if attempt == 39: raise\n  time.sleep(.25)\nPY\n";
    }

    private sealed record DemoTerms(string Kind, Guid WorkstreamId, string TemplateId);
}
