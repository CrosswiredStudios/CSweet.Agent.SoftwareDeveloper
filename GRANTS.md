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

The implementation harness requests no memory, secret-read, production deployment, release, merge,
repository administration, access-control, protected-branch write, or organization-management
authority. Its assigned-work support authority is confined to linked work-item support with the assigned
team Architect. Its local file and shell access exists only inside the approved developer runtime
and assignment workspace.


## Direct personal development

`source-control.personal-work.prepare.v1` creates one deterministic private internal repository for the installation's own live, claimed personal ticket under the business repository policy. It accepts a ticket ID and stable key, not a repository, URL, credential or branch. Existing Git workspace operations remain constrained to that ticket's repository and active team policy.

`platform.user-input.request.v1` asks the originating human who will make tickets. Operating-state read/write retains this decision and deployment progress across restarts. Chat read/send and personal-todo APIs retain their installation and conversation ownership checks.

Compute provisioning and execution are bounded grants. `network.inbound.v1` and `network.publish-port.v1` must be explicitly granted before exposing a local application link; installation approval alone creates neither. Core's owner action scopes both to one instance, port 8080 and the remaining lease. Outbound, private-network and public-endpoint access are separate actions and are not requested by this workflow. The local Hyper-V provider currently rejects those network modes.
