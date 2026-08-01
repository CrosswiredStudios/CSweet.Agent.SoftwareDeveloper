# Software Developer grant reference

Manifest declarations request authority; they do not grant it. C-Sweet must authorize each
capability for the installation, and custom provider capabilities also require an approved
same-organization binding.

| Capability | Scope | Why it is required | Expected effect |
|---|---|---|---|
| `platform.llm.chat-stream.v1` | Organization | Run the Microsoft Agent Framework harness with the selected approved model | Model inference only; no provider credential is exposed |
| `platform.team-roster.read.v1` | Team | Read only this developer employee's approved teammates and team-specific roles | No chat, board, tool, memory, installation, credential, or agent-to-agent authority |
| `work.execution.run.v1` | Orchestration attempt | Execute the exact assigned Development stage | Return a structured outcome; never transition the card |
| `work.item.read` | Work item | Verify the authoritative assignment and brief | Read only the assigned ticket |
| `work.item.comment` | Work item | Attach evidence or a sanitized blocker | Bounded ticket comment |
| `git.workspace.prepare.v1` | Work item | Clone/fetch and resume the assignment checkout | Deterministic installation workspace and ticket branch |
| `git.workspace.inspect.v1` | Work item | Read sanitized diff and commit metadata | No credential values |
| `git.workspace.publish.v1` | Work item | Commit, push the ticket branch, and create the configured PR | No force push, merge, or protected-branch write |
| `git.workspace.cleanup.v1` | Work item | Remove successful work or retain a failure for recovery | Assignment-directory-only cleanup |

## Platform broker expectations

The C-Sweet Git workspace broker:

- resolve repository and organization identity server-side;
- enforce repository and branch scope from the installation grant;
- materializes GitHub App, HTTPS token, or SSH authentication only for a running installation;
- verifies granted host, port, repository path, operation, and SSH host fingerprints;
- confines Git commands to the installation volume and assignment directory;
- return bounded, redacted tool output;
- make writes and pull-request creation idempotent with stable domain keys;
- accept the C-Sweet work ID, or a deterministic derivative of it, as the idempotency root for
  mutation operations;
- never expose access tokens, signing keys, runner credentials, or host paths;
- reject merges, releases, deployments, secret changes, access-control changes, and repository
  deletion through these contracts.

Generic Git can clone and push, but cannot complete a ticket until a compatible review provider
is configured. GitHub is the initial automated pull-request provider.

## Intentionally absent

This version requests no memory, direct communication, secret-read, deployment, release, merge,
repository administration, access-control, protected-branch write, or organization-management
authority. Its local file and shell access exists only inside the approved developer runtime and
assignment workspace.
