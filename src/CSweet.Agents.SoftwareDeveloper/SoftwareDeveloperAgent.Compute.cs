using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    internal const string DemoTitle = "Create a Hello World application and return its running test link";
    internal const string DemoMarker = "csweet-linux-hello-v1";
    internal static readonly string[] ComputeCapabilities = ["compute.provision.v1", "compute.read.v1", "compute.list.v1",
        "compute.execute.v1", "compute.stop.v1", "compute.destroy.v1", "network.inbound.v1", "network.publish-port.v1"];

    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
    {
        if (message.EventType == ComputeEvents.Available)
        {
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            foreach (var item in directory.Boards.Where(b => b.OwnerOrganizationUserId == directory.CurrentOrganizationUserId)
                         .SelectMany(b => b.Items).Where(x => x.Title == DemoTitle && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null).Take(10))
                await WakeDemoAsync(item, context, token);
            return;
        }
        if (message.EventType == ComputeEvents.Changed)
        {
            var change = message.Data.Deserialize<ComputeChangedEvent>(SerializerOptions) ?? throw new JsonException("Compute event is missing.");
            var environment = await context.Platform.Compute.ReadAsync(change.EnvironmentId, token);
            if (environment.DesiredEnvironmentKey is not { } key || !key.StartsWith(DemoMarker + ":", StringComparison.Ordinal) ||
                !Guid.TryParseExact(key[(DemoMarker.Length + 1)..], "N", out var itemId)) return;
            // Wake hints are not snapshots or grants. Re-read both the environment and current queue.
            var directory = await context.Platform.PersonalTodo.ListAsync(token);
            var item = directory.Boards.Where(b => b.OwnerOrganizationUserId == directory.CurrentOrganizationUserId)
                .SelectMany(b => b.Items).SingleOrDefault(x => x.Id == itemId && x.ArchivedAt is null && x.Status == "Running" && x.Wait is not null);
            if (item is not null) await WakeDemoAsync(item, context, token);
            return;
        }

        Guid chatId; Guid messageId; CommunicationMessageReceivedEvent? received = null;
        if (message.EventType == CommunicationEvents.MessageMentioned)
        {
            var hint = message.Data.Deserialize<CommunicationMessageMentionedEvent>(SerializerOptions);
            if (hint is null) return;
            chatId = hint.ChatId; messageId = hint.MessageId;
        }
        else if (message.EventType == CommunicationEvents.MessageReceived)
        {
            var hint = received = message.Data.Deserialize<CommunicationMessageReceivedEvent>(SerializerOptions);
            if (hint is null || !Guid.TryParse(hint.ConversationId, out chatId)) return;
            messageId = hint.MessageId;
        }
        else return;
        if (chatId == Guid.Empty || messageId == Guid.Empty) return;
        var chat = await context.Platform.Communication.ReadChatAsync(chatId, token);
        var source = chat.Messages.SingleOrDefault(x => x.Id == messageId && x.ChatId == chatId);
        if (source is null || source.SenderEmployeeType != "Human") return;
        if (!IsHelloRequest(source.Content))
        {
            if (received is { TurnId: var turnId } && turnId != Guid.Empty)
                await ReplyAsync("I can create a Linux Hello World test instance, or implement software through an assigned work item.", "hello-help");
            return;
        }
        // Retain the request even while C-Sweet is preparing its first Linux image/provider.
        // The first provisioning attempt resolves the platform-owned defaults.
        var terms = JsonSerializer.Serialize(new DemoTerms(DemoMarker, Guid.Empty, ""), SerializerOptions);
        await context.Platform.PersonalTodo.AddAsync(new(DemoTitle, terms, "Normal", null, $"hello-request:{messageId:N}",
            SourceConversationId: chatId, SourceMessageId: messageId), token);
        await ReplyAsync(
            "I’m creating a Hello World app in an isolated Linux test instance. I’ll return the browser link after the app passes its health check. The link will work on the compute host machine and expire with the test instance.",
            "hello-accepted");

        async Task ReplyAsync(string content, string key)
        {
            if (received is { TurnId: var turnId } && turnId != Guid.Empty)
            {
                await using var stream = context.CreateTurnStream(received.ConversationId, turnId, received.Attempt);
                await stream.CommitAsync(content, token);
            }
            else
                await context.Platform.Communication.SendMessageAsync(chatId, content, $"{key}:{messageId:N}", token);
        }
    }

    internal static bool IsHelloRequest(string content) => content.Length <= 8000 &&
        !content.Contains("do not", StringComparison.OrdinalIgnoreCase) && !content.Contains("don't", StringComparison.OrdinalIgnoreCase) &&
        !content.Contains("cancel", StringComparison.OrdinalIgnoreCase) &&
        content.Contains("hello world", StringComparison.OrdinalIgnoreCase) &&
        (content.Contains("create", StringComparison.OrdinalIgnoreCase) || content.Contains("build", StringComparison.OrdinalIgnoreCase)) &&
        (content.Contains("link", StringComparison.OrdinalIgnoreCase) || content.Contains("test instance", StringComparison.OrdinalIgnoreCase));

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        if (item.Title != DemoTitle) return PersonalTodoResult.Blocked("Repository implementation requires an approved work assignment. Standalone compute currently supports the Hello World test-instance request.");
        return await AdvanceDemoAsync(item, context, token);
    }

    private static Task<PersonalTodoItem> WakeDemoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token) =>
        context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision,
            $"{DemoMarker}:{item.Id:N}:wake:{item.Revision}"), token);
    private async Task<PersonalTodoResult> AdvanceDemoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var terms = JsonSerializer.Deserialize<DemoTerms>(item.Description, SerializerOptions);
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
                new ComputeSpecification("linux", "x64", terms.TemplateId, new(1, 1024, 20480), 3600)), token);
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
            var summary = $"Hello World is running: {url}\nOpen this link on the compute host machine. It expires at {expiry:O}. Environment: {environment.Id:D}. Source: /var/lib/csweet-compute/work/hello/app.py inside the VM.";
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
            $"/usr/bin/systemd-run --quiet --service-type=exec --unit=csweet-hello-{id:N} --property=RuntimeMaxSec=3600 -- /usr/bin/python3 /var/lib/csweet-compute/work/hello/app.py\n" +
            "/usr/bin/python3 - <<'PY'\nimport time, urllib.request\nfor attempt in range(40):\n try:\n  response=urllib.request.urlopen('http://127.0.0.1:8080/', timeout=1)\n  assert b'Hello World' in response.read(4096)\n  print('Hello World HTTP health check passed')\n  break\n except Exception:\n  if attempt == 39: raise\n  time.sleep(.25)\nPY\n";
    }

    private sealed record DemoTerms(string Kind, Guid WorkstreamId, string TemplateId);
}
