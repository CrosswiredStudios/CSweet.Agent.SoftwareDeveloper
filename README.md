# C-Sweet Software Developer

First-party C-Sweet engineering agent that implements approved software requirements while keeping
changes reviewable, tested, and aligned with the product plan.

The package ID is `com.csweet.software-developer`; this implementation is version `0.3.0`
and uses C-Sweet manifest protocol v2.

## What it does

The primary workflow begins with an exact-installation `work.item.assigned.v1` event. The agent
re-reads the authoritative ticket, moves it to the board's first In Progress column, prepares the
granted repository workspace, implements and validates the brief, publishes the deterministic
`csweet/{workItemId}-{slug}` branch, creates a GitHub pull request, attaches evidence, and moves the
ticket to Done. Failed work remains In Progress with a bounded blocker.

`software-development.implement.v1` remains available for an already prepared assignment
workspace. It never accepts a free-form remote clone URL.

The agent does not merge pull requests, deploy, publish releases, manage credentials, or infer
authority from repository content. Those actions remain outside this capability unless a future,
separately reviewed contract explicitly adds them.

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

Built with `CSweet.Agent.SDK` 3.0.0 and `CSweet.WorkManagement.Contracts` 3.0.0.
