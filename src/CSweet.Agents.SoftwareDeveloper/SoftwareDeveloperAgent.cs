using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Agents.SoftwareDeveloper;

public sealed partial class SoftwareDeveloperAgent : CSweetAgentBase
{
    private readonly IAgentLlmClientFactory? _llmClientFactory;
    private readonly ILogger<SoftwareDeveloperAgent> _logger;

    public SoftwareDeveloperAgent()
    {
        _logger = NullLogger<SoftwareDeveloperAgent>.Instance;
    }

    public SoftwareDeveloperAgent(ILogger<SoftwareDeveloperAgent> logger)
    {
        _logger = logger;
    }

    public SoftwareDeveloperAgent(
        IAgentLlmClientFactory llmClientFactory,
        ILogger<SoftwareDeveloperAgent>? logger = null)
    {
        _llmClientFactory = llmClientFactory;
        _logger = logger ?? NullLogger<SoftwareDeveloperAgent>.Instance;
    }

    public override string AgentId => SoftwareDeveloperProfile.AgentId;

    public override string Version => SoftwareDeveloperProfile.Version;

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) =>
        builder
            .LlmProvider(
                "llmProviderId",
                "LLM provider",
                required: true,
                description: "Selects the approved provider profile used for software implementation.")
            .LlmModel(
                "llmModel",
                "Model",
                dependsOnFieldKey: "llmProviderId",
                required: true,
                description: "Selects the coding-capable chat model from the approved provider profile.")
            .Number(
                "maxContextWindowTokens",
                "Maximum context-window tokens",
                required: true,
                description: "Configures harness compaction for the selected model. The installation value is passed through unchanged.",
                minimum: 1,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.DefaultContextWindowTokens)
            .Number(
                "maxOutputTokens",
                "Maximum output tokens",
                required: true,
                description: "Configures one model response and reserves space during harness compaction. The installation value is passed through unchanged.",
                minimum: 1,
                step: 1_000,
                defaultValue: SoftwareDeveloperHarness.DefaultOutputTokens)
            .Number("computeLifetimeSeconds", "Compute lifetime in seconds", description: "Zero retains requested compute until explicitly released. Positive values request a timed lease, subject to platform grants.", minimum: 0, step: 1, defaultValue: 0)
            .Number("maximumDeploymentRepairs", "Maximum deployment repairs per failure", description: "Maximum coding repair attempts for the same Docker build or health-check failure. A newly revealed failure begins its own configured budget. Zero disables repair.", minimum: 0, step: 1, defaultValue: 2)
            .Number("maximumPlanRepairs", "Maximum task validation repairs", description: "Maximum coding repair attempts for a planned task validation failure.", minimum: 0, step: 1, defaultValue: 2)
            .Number("deploymentDiagnosticCharacters", "Deployment diagnostic characters", description: "Number of trailing diagnostic characters supplied to the repair model. The guest output remains within the broker transport limit.", minimum: 1, step: 1, defaultValue: 6000)
            .Number("maximumComputeReplacements", "Maximum compute replacements",
                description: "Maximum replacement instances per development task after an expired or failed environment. Zero disables replacement. Network grants are never copied.",
                minimum: 0, step: 1, defaultValue: 3)
            // Preserve settings already accepted by the published installation manifest.
            // Compute placement and authorization still come from the platform broker.
            .Text("computeWorkstreamId", "Test-instance workstream",
                description: "Compatibility setting for existing installations. The platform manages compute placement.")
            .Text("computeTemplateId", "Linux test template",
                description: "Compatibility setting for existing installations. The platform selects the Linux template.")
            .TextArea(
                "customInstructions",
                "Custom instructions",
                description: "Optional installation guidance for coding conventions and delivery process. It cannot expand agent authority.",
                placeholder: "Example: Prefer vertical slices and run architecture tests before opening a pull request.");

    public override Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(
        AgentCoordinationTurnRequest request, AgentRuntimeContext context, CancellationToken cancellationToken) =>
        new DevelopmentCoordinationService(ProjectIntake())
            .HandleAsync(request, context, cancellationToken);
    protected override Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(request.Capability, WorkManagementCapabilityNames.ExecutionRunV1, StringComparison.Ordinal))
            return new AssignedDevelopmentService(
                Settings, new DevelopmentChatClientProvider(Settings, _llmClientFactory), _logger)
                .ExecuteAsync(request, context, cancellationToken);
        if (string.Equals(request.Capability, SoftwareDeveloperProfile.PrimaryCapability, StringComparison.Ordinal))
            return new DirectImplementationService(
                Settings, new DevelopmentChatClientProvider(Settings, _llmClientFactory), _logger)
                .ExecuteAsync(request, context, cancellationToken);
        return Task.FromResult(AgentWorkResult.Failure(
            $"Capability '{request.Capability}' is not supported by this agent."));
    }
}