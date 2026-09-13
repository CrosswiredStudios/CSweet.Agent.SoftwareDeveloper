# C-Sweet Software Developer

First-party C-Sweet engineering agent that implements approved software requirements while keeping
changes reviewable, tested, and aligned with the product plan.

The package ID is `com.csweet.software-developer`; this implementation is version `1.4.1`
and uses C-Sweet manifest protocol v2.

## What it does

The primary workflow begins with an exact-installation `work.item.assigned.v1` event. The agent
re-reads the authoritative ticket, moves it to the board's first In Progress column, prepares the
granted repository workspace, implements and validates the brief, publishes the deterministic
`csweet/{workItemId}-{slug}` branch, creates a GitHub pull request, attaches evidence, and moves the
ticket to Done. Failed work remains In Progress with a bounded blocker.

`software-development.implement.v1` remains available for an already prepared assignment
workspace. It never accepts a free-form remote clone URL.

The assigned-work capability does not merge pull requests, deploy, publish releases, manage credentials, or infer
authority from repository content. Those actions remain outside this capability unless a future,
separately reviewed contract explicitly adds them.

When a genuine implementation failure is technical rather than operational, the Developer opens
one assignment-pinned support session with the team's Software Architect. The request contains
sanitized diagnostics, attempted steps, failed validations, and one explicit question. Linked
guidance is included in the next confined implementation attempt, after which the Developer may
request a governed retry of the exact blocked stage. Credential, grant, platform, provider,
repository-authorization, and availability failures follow operational escalation instead.

## Why Microsoft Agent Framework Harness

This agent uses `Microsoft.Agents.AI.Harness` 1.15.0 because software implementation is typically a
long, tool-driven task. The harness supplies the function-invocation loop, bounded iterations,
context compaction, per-service-call history, todo tracking, and OpenTelemetry integration.

C-Sweet remains the security and durability boundary:

- the chat client comes from `AgentRuntimeContext.CreateChatClient` and never from a provider key;
- file access is rooted at the assignment directory with `FileSystemAgentFileStore`;
- `LocalShellExecutor` is confined to that directory and uses an explicit unattended deny policy;
- harness file memory, filesystem skill discovery, hosted web search, agent-mode interaction,
  background agents, and broad automatic tool approval are disabled;
- C-Sweet owns work leasing, retries, progress, cancellation, grants, and provider bindings.

The shell policy is defense in depth. The non-root, read-only-root container, isolated writable
volume, resource limits, egress gateway, repository connection grant, branch protection, and
repository-scoped credential are the actual security boundary.

Microsoft reference:
[Agent harnesses](https://learn.microsoft.com/en-us/agent-framework/agents/harness?pivots=programming-language-csharp).

## Configuration

Each installation must select:

- `llmProviderId`: an approved C-Sweet provider profile;
- `llmModel`: a coding-capable model exposed by that profile;
- `maxContextWindowTokens`: the selected model's context-window size, used for compaction;
- `maxOutputTokens`: the selected model's maximum output size and compaction reserve;
- `customInstructions` (optional): repository-independent style or delivery guidance that cannot
  expand authority.

The manifest provides `agent.configuration.describe.v1`,
`agent.configuration.update.v1`, `software-development.implement.v1`, and
`work.execution.run.v1`, plus a settings form contribution. C-Sweet owns every automated
stage transition; the agent returns only a structured outcome and evidence.

## Required grants

The installation requests:

- `platform.llm.chat-stream.v1`;
- `platform.team-roster.read.v1` for the assigned employee's bounded team roster only;
- work-item-scoped `work.item.read` and `work.item.comment`;
- work-item-scoped comment/orchestration reads, `communication.coordination.start-work.v1`, and
  governed exact-stage retry for the bounded Architect support loop;
- work-item-scoped `git.workspace.prepare.v2`, `git.workspace.refresh.v2`,
  `git.workspace.inspect.v2`, `git.workspace.publish.v2`, and `git.workspace.cleanup.v2`.

The durable orchestration attempt creates the least-privilege work-item grants. The repository connection and its
credentials are separately granted to the exact installation. See [GRANTS.md](GRANTS.md).

## Example request

```json
{
  "repository": "/workspace/5ea770e9f5d048339852b8278124b8c2/1",
  "baseBranch": "main",
  "objective": "Add the approved behavior.",
  "requirements": [
    "Preserve the existing public API."
  ],
  "acceptanceCriteria": [
    "Focused tests pass."
  ],
  "constraints": [
    "Do not merge the pull request."
  ]
}
```

For assignment events the harness also writes `.csweet/outcome.json`. C-Sweet removes this
control file before inspecting and publishing the repository diff.

## Build and test

Requirements: .NET 10 and access to the public NuGet packages pinned by the projects.

```powershell
dotnet restore CSweet.Agents.SoftwareDeveloper.slnx
dotnet test CSweet.Agents.SoftwareDeveloper.slnx --no-restore
dotnet run --project src/CSweet.Agents.SoftwareDeveloper -- --self-test
```

The tests require no C-Sweet instance, repository credential, GitHub account, or network access
after restore.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the complete manifest, especially its model and repository grants, before approving installation.

Built with `CSweet.Agent.SDK` 3.44.0 and `CSweet.WorkManagement.Contracts` 3.20.0.

## Release notes

See [versioned release notes](releases/README.md). Add the matching note with every agent version change.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.

## Infrastructure

The WebHost proof of concept and its private-preview callbacks have been retired. Ordinary software implementation and delivery-build authority remain separately scoped.

## Direct development and Docker test instances (1.4.1)

Ask Daniel to build an application, for example: "Build a Tetris clone and deploy it. Create your own tickets."
Without an explicit ticket preference, Daniel asks whether you will create tickets or he should create his own. The question and source request survive restarts and remain bound to the original sender. A manager-created assignment continues through the existing assigned-work flow.

With permission to make his own tickets, Daniel creates a personal task, prepares a private C-Sweet repository through the typed SDK, uses the configured coding model to implement and test custom code, publishes the source commit, and deploys its Dockerfile inside isolated Linux compute. An instance identified in the conversation can be reused if it remains owned, authorized and compatible. Code is retained in Source Control even if deployment is blocked.

C-Sweet automatically prepares Docker and cached Python/Node base images. Upgrading the old Hello World image waits for confirmed resource teardown, preserves provider identity and journals, and requests UAC when necessary. No user scripts are required. Existing VMs are not retrofitted with Docker.

Compute starts with networking disabled. The business owner explicitly grants a local test link from the business Compute page for one instance, port 8080, and its remaining lease. Granting resumes waiting work automatically. Revocation closes access through the broker. No outbound, private-network or public-internet authority is implied by approving the installation or this local link.

After the Docker build and HTTP health check succeed, Daniel returns an **Open application** link (opening in a new tab), source link, commit and expiry. The application URL works only on the compute host machine. Known build failures return to the coding model for up to two repair attempts. Unknown command outcomes block instead of submitting another potentially duplicate command.

Current limits: one-hour ephemeral Linux VM, cached `csweet/python:3.12` and `csweet/node:22` bases, 16 MiB source bundle, 30 seconds per guest command and 8 KiB command output. Guest outbound internet, arbitrary dependency downloads, public deployment and long builds are not supported by this local provider yet. Required unavailable dependencies are reported as blockers.

The chat and personal-work callbacks use `context.Platform`, including SDK 3.44.0's `Git.PreparePersonalAsync`. Added declarations are `source-control.personal-work.prepare.v1`, `platform.agent-operating-state.read.v1`, `platform.agent-operating-state.write.v1` and `platform.user-input.request.v1`. Repository policy, live personal-task claim, installation, owner and team permissions are checked by Core. These declarations do not grant arbitrary repository creation or production deployment authority.

Compute events are wake hints; the SDK claims the owning personal task before advancing it, and all operations re-read authorized state. A five-minute scheduled recovery deadline covers missed notifications. Work displays its current stage or blocking reason while the model or compute is idle.

## Workspace transfer recovery (1.4.1)

Uses SDK 3.44.0 and the added `git.workspace.sync.v1` declaration. The SDK downloads the authorized Core snapshot into the isolated runtime's writable temporary workspace before coding and uploads edited source before publication. It never mounts a host path or exposes Git credentials. Existing local edits survive repeated calls; a replacement runtime restores the latest uploaded snapshot. A requeued 1.4.0 task automatically replaces its old broker-only workspace path without creating another repository.

Review the added workspace-sync declaration during the normal update. Source transfer currently supports 512 KiB compressed snapshots, 16 MiB content and 4,096 files; local `.csweet` control files are omitted. Application code must fit these bounds. Network grants and Docker compute limits remain separate.
