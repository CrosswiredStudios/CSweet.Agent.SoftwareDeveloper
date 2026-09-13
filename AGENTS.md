# C-Sweet Software Developer repository instructions

This repository contains one standalone C-Sweet protocol-v2 agent. Its purpose is:

> Implements production software from approved requirements while keeping changes reviewable, tested, and aligned with the product plan.

## Invariants

- Keep `com.csweet.software-developer` and version `1.1.1` synchronized between agent code,
  `csweet-plugin.json`, tests, and releases.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events,
  configuration, credentials, web access, and UI contributions synchronized with implementation
  and tests.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access
  runtime/workload/lease tokens, connect directly to databases or Docker, or handle provider
  credentials.
- Agent work is delivered at least once. Honor cancellation and use stable domain idempotency keys
  for external mutations.
- Unknown capabilities and events must fail or be ignored safely without leaking sensitive data.
- Follow the canonical `AGENT_AUTHORING.md` distributed with `CSweet.Agent.SDK`; this repository
  must remain independently buildable and must not add a source-tree reference to the SDK checkout.
- Use the Microsoft Agent Framework harness only with `context.CreateChatClient(...)`.
- Root harness file access at the assignment directory with `FileSystemAgentFileStore`. Enable
  `LocalShellExecutor` only with `ConfineWorkingDirectory = true` and the unattended deny policy.
  Keep file memory, hosted web search, filesystem skill discovery, background agents, and broad
  automatic tool approval disabled.
- The container, egress gateway, repository connection grant, and scoped credential are the
  security boundary. Never request or expose credential values to the model.
- Do not add merge, release, deployment, secret, access-control, repository-deletion, or history
  rewrite authority to `software-development.implement.v1`.

## Verification

Run from the repository root:

```powershell
dotnet test CSweet.Agents.SoftwareDeveloper.slnx
dotnet run --project src/CSweet.Agents.SoftwareDeveloper -- --self-test
```

Any new capability, grant, event, configuration field, credential, or network rule requires a
manifest update, a README explanation, and tests.

## Release-note ordering

- Bump the agent version FIRST, synchronizing the root `csweet-plugin.json`, implementation identity, project/package version, and version assertions as required by this repository.
- Only AFTER the version bump, read the final `version` back from `csweet-plugin.json` and write `releases/<version>.md` for that exact version (no `v` prefix). Never write the new release's notes under the previous version.
- If the version changes again during the task, retarget the unpublished notes to the final version. Preserve already published historical notes.
- Before handoff or publishing, verify that the manifest, implementation/package version, release-note filename, and release-note heading all match. A version bump is incomplete without its matching release notes.
