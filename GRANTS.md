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
| `work.item.comments.read` | Work item | Consume platform-linked Architect guidance on the next attempt | Read-only correlated comments |
| `work.orchestration.read.v1` | Work item and sprint execution | Verify the exact stage and immutable assignment revision | Read-only execution snapshot |
| `communication.coordination.start-work.v1` | Work item and assigned team | Ask the designated Architect one bounded technical question | Six-turn assignment-pinned session |
| `communication.coordination.read.v1` / `communication.coordination.respond.v1` | Coordination session | Consume guidance and finalize the support outcome | No autonomous acknowledgement loop |
| `work.orchestration.retry.v1` | Exact blocked stage | Ask the platform to retry after guidance | Fails closed on stale assignment, exhausted attempts, or team loss |
| `git.workspace.prepare.v2` | Work item | Materialize the Core-resolved credential-free snapshot | No caller-selected repository, ref, branch, `.git`, or credentials |
| `git.workspace.refresh.v2` | Work item | Refresh against the Core-authorized base | Structured bounded conflicts only |
| `git.workspace.inspect.v2` | Work item | Read bounded diff metadata | No credential values or Git metadata |
| `git.workspace.publish.v2` | Work item | Submit the change artifact to trusted GitHost | No direct push, merge, or protected-branch write |
| `git.workspace.cleanup.v2` | Work item | Remove successful work or retain a failure for recovery | Assignment-directory-only cleanup |

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

The implementation harness requests no memory, generic planning chat, secret-read, deployment, release, merge,
repository administration, access-control, protected-branch write, or organization-management
authority. Its communication authority is confined to linked work-item support with the assigned
team Architect. Its local file and shell access exists only inside the approved developer runtime
and assignment workspace.

The separate web-preview.manage.v1 callback requests scoped private preview lifecycle, browser testing and certified build capabilities. Installing or upgrading the agent does not activate hosting: owners must review the new declarations and approve the project hosting grant. Public/production deployment remains unsupported.
