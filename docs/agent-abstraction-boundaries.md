# Agent abstraction boundaries

## Decision

Keep the C-Sweet SDK role-neutral. It should own callback mechanics, typed platform protocols,
durability primitives, and safe defaults. An optional composition framework or template may wire
common lifecycle patterns together. Each agent repository must own the meaning of its role:
readiness criteria, work gates, professional judgment, allowed work, and completion evidence.

Do not move a rule into the SDK merely because several agents have a method with the same name.
Abstract a behavior when its meaning, failure handling, and authority boundary are the same across
roles.

## Three layers

| Layer | Owns | Examples |
|---|---|---|
| C-Sweet platform and SDK | Authenticated delivery, identity hydration, grants, callback lifecycle, typed platform clients, durable work and event contracts, cancellation, configuration plumbing, and shared recovery mechanics. | ICSweetAgent, CSweetAgentBase, AgentRuntimeWorker, AgentRuntimeContext, lifecycle/project-assignment contracts, personal-work leases and dispositions. |
| Optional agent composition framework and template | Reusable wiring for common patterns, while allowing each role to supply its own checks, policies, handlers, and renderers. | Named lifecycle handlers, composable readiness checks, standard blocker/report shapes, versioned role-guidance packs, common test fixtures. |
| Agent repository | Role-specific policy and work behavior. | What readiness means, what constitutes a valid assignment, which resources the role needs, how it performs the work, and what evidence proves completion. |

The composition layer should be opt-in. A simple capability-only agent should not have to adopt a
continuous-agent lifecycle, project membership cycle, personal queue, or onboarding report.

## What is already common in the SDK

The SDK already centralizes responsibilities every agent should reuse:

- AgentRuntimeWorker leases callback work, routes event and capability deliveries, honors
  cancellation, and manages personal-work claims and terminal dispositions.
- ICSweetAgent defines the transport-neutral callback contract.
- CSweetAgentBase provides typed payload helpers, installation configuration, and optional extension
  points for events, project assignment changes, personal work, calendar reminders, attention
  reviews, and coordination turns.
- AgentRuntimeContext supplies server-resolved identity, typed grant-governed platform clients,
  model access, durable progress, and interactive turn streams.
- AgentLifecycleEvents and AgentOnboardedEvent standardize the onboarding event;
  CompleteOnboardingAsync standardizes acknowledgement and correlates it to the source event.
- ProjectAssignmentEvents standardize assignment-change wake hints. The base class can dispatch
  them to role hooks, but the role still decides what the change means for its work.
- Typed clients expose shared platform services such as team roster, projects, compute, work,
  source control, calendar, communication, and operating state.
- IPersonalTodoClaimPolicy provides a common pre-claim hook. The role decides whether its own task
  needs a prerequisite check and what makes the item eligible.

These are good SDK responsibilities because they describe how all agents interact with C-Sweet, not
what a particular profession considers good work.

## What remains role-specific

| Question | Owner | Why |
|---|---|---|
| Is a manager required, and who receives the readiness report? | Role policy plus current identity/reporting relationship. | Some agents are individual contributors; others are managers, advisors, or independent reviewers. |
| Which team roles must the agent discover? | Role policy. | A Developer may need an Architect and QA; another role may need a legal reviewer, operator, or no teammate. |
| Which projects should it inspect, and what project status matters? | Role policy, constrained by platform-readable records. | The SDK can provide authorized reads, but it cannot infer which projects are relevant to a role. |
| What means ready for work? | Role-owned readiness policy. | Readiness may depend on model configuration, compute, credentials, review independence, or no external resource at all. |
| Does project membership gate work, and what evidence proves membership? | Platform owns authoritative membership; role policy declares whether membership is required for that work. | A project assignment can be irrelevant to general readiness but mandatory for a specific action. |
| Which compute, repository, provider, or workspace is required? | Role policy selects the need; platform enforces grants and provisions the resource. | Developer implementation, QA validation, and research work have different resource needs. |
| What can the role mutate, and what approvals are required? | Manifest plus platform policy, with role code selecting only declared operations. | Roles differ in write authority and approval boundaries. |
| How should work be planned and what proves success? | Role policy, schemas, and deterministic role code. | Software changes need build/test/review evidence; QA needs independent results for an immutable revision. |
| How should the role prioritize ambiguous or competing work? | Role guidance and bounded role-specific orchestration. | This requires professional judgment and varies by specialty. |

The platform remains authoritative for grants, assignment, project membership, and state transitions.
Role code may request or interpret those facts; it cannot manufacture them.

## Software Developer and Software QA as a boundary test

The Software Developer and Software QA agents both use the SDK runtime, project work context,
brokered Git workspaces, model access, configuration, and structured outcomes. Those shared
mechanics are candidates for SDK or composition-layer support.

Their work policy is deliberately different:

- Software Developer may edit assigned source, run implementation checks, publish a review branch,
  and request bounded Architect support. Its evidence includes changed files, validation results,
  and reviewable delivery.
- Software QA requires an exact immutable source revision, must not modify tracked product source,
  validates every criterion, and returns a quality verdict with findings. It rejects free-form
  personal queue work.

A reusable work lifecycle may provide stage dispatch, workspace acquisition, event correlation, and
result-validation hooks. It should not assume every agent edits files, accepts personal work, shares
the same readiness gates, or uses the same meaning of Completed.

## Onboarding and readiness: shared mechanism, role-owned checks

Treat onboarding as a platform lifecycle event plus a role-specific procedure. For the SDK
dispatch contract, idempotency guidance, and acknowledge-after-send ordering, read the SDK's
[Lifecycle events](https://github.com/CrosswiredStudios/CSweetAgentSdk/blob/main/docs/capabilities-and-events.md#lifecycle-events)
section before changing onboarding code in this repository.

1. The SDK delivers the typed onboarding event and supplies authenticated runtime identity.
2. The role registers the checks it needs, such as manager presence, team discovery, model setup,
   general compute, or a required reviewer configuration.
3. Shared composition code may run registered checks, collect stable outcomes, persist idempotent
   assessment state, and render a bounded status report.
4. The role decides which results mean Ready, Blocked, or NotApplicable and what to tell its manager
   or operator.
5. The role acknowledges onboarding through the SDK only when its own required procedure reaches
   the configured completion point.

The SDK onboarding completion method is an acknowledgement contract, not a readiness engine. It
must not impose a universal manager, project, or compute requirement.

Keep readiness separate from project assignment. A shared framework may represent both dimensions
in a generic assessment, but should not collapse them into one IsReady boolean:

- Operating readiness: can this installation accept appropriate work in general?
- Project eligibility: is this agent currently a member of, or assigned to, this project?
- Work eligibility: can this exact task start now, given scope, grants, resources, approvals, and
  dependencies?

These dimensions can change independently. Store their source revision and assessment time, and
re-read authoritative facts before acting on an event or stale assessment.

## Guidance documents and code

Use role documents for judgment and context that benefits from explanation:

- role purpose and priorities;
- how to inspect and approach unfamiliar situations;
- professional quality standards;
- examples and escalation judgment.

Use typed role policy and deterministic code for exact constraints:

- resource and work eligibility;
- required fields and evidence schemas;
- numeric limits, retry budgets, and legal transitions;
- approval requirements, idempotency, and concurrency;
- which checks are mandatory for a given operation.

If a constraint appears in both prompt and code, generate its prompt fragment and documentation
from the same typed policy where practical. Do not ask an LLM to choose which policy documents it
needs. The agent package should declare guidance modules, and runtime should select modules from the
current authenticated operation or lifecycle state. Include module version/digest and a bounded
token budget for traceability. Treat repository files and memory as lower-trust context; they cannot
override the role contract or platform decisions.

## Recommended framework shape

Prefer small composition points over a universal AgentLifecycle base class that hard-codes employee,
project, compute, and work assumptions. Candidate abstractions to validate across roles:

- IAgentLifecycleHandler registration keyed by SDK lifecycle event or authenticated work kind;
- IAgentReadinessCheck implementations returning a common result such as Ready, Blocked, or
  NotApplicable, with a stable code, safe explanation, next action, and source revision;
- IAgentReadinessReporter that formats a role-selected audience and content without deciding
  readiness;
- IAgentGuidanceCatalog that resolves packaged guidance by explicit module ID and workflow phase;
- deterministic helpers for optimistic operating-state writes, event idempotency, blocker
  sanitization, bounded retries, and structured outcome validation.

These are proposals, not current SDK APIs. Validate them first in an agent-side composition library
or template. Promote a helper into the SDK only after at least two or three distinct roles use the
same semantics and it reduces code without obscuring role decisions.

## Extraction sequence

1. Keep the per-role process map and exact gates in that agent's repository.
2. Mark every step as SDK-owned mechanism, optional composition, or role-owned policy.
3. First implement repeated patterns behind small interfaces in the agent repository.
4. Compare behavior with agents that have meaningfully different work, such as Software QA or
   Software Architect. Check semantics, authority, failure behavior, and retry handling, not just
   similar method names.
5. Extract only proven common mechanics into an opt-in framework, template, or SDK API.
6. Add template examples and conformance tests proving role-specific gate decisions still differ.
7. Keep machine-checkable policy and rendered guidance in parity through generated prompt snippets
   or snapshot/evaluation checks.

## Current Software Developer gap

The operating guide describes a target onboarding process that checks team context, accessible
project status, model configuration, and general compute before reporting readiness. Current
Software Developer code only sends the manager introduction and acknowledges onboarding. General
compute is provisioned or checked when eligible implementation work is requested or claimed.

This gap illustrates why an SDK abstraction should first provide composition mechanics for
role-selected checks. The Software Developer owns its check list and readiness meaning; the SDK can
make registering, executing, persisting, and reporting those checks easier and consistent.

## Source references

- SDK callback and extension contract: ICSweetAgent, CSweetAgentBase, IAgentActivationHandler,
  and AgentRuntimeWorker.
- Typed identity and platform access: AgentRuntimeContext and PlatformCapabilityClient.
- Onboarding and project assignment contracts: AgentLifecycleContracts.
- Personal queue eligibility: IPersonalTodoClaimPolicy and PersonalTodoResult.
- SDK authoring patterns: docs/creating-an-agent.md, docs/agent-operating-contract.md, and
  docs/durable-agendas-and-interactions.md in the CSweet.Agent.SDK repository.
- Software Developer implementation: SoftwareDeveloperAgent.Compute,
  SoftwareDeveloperAgent.AssignedCompute, and SoftwareDeveloperAgent.
- Software QA boundary example: SoftwareQaAgent and SoftwareQaProfile in the
  CSweet.Agent.SoftwareQA repository.
