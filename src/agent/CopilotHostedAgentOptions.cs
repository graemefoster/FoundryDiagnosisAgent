namespace FoundryDiagnosisAgent.Agent;

public sealed class CopilotHostedAgentOptions
{
    public const string SectionName = "HostedAgent";

    public string? FoundryProjectEndpoint { get; set; }

    public string? ModelDeploymentName { get; set; }

    public string? InstructionsFile { get; set; }

    public string? WorkingDirectory { get; set; }

    public string[] SkillDirectories { get; set; } = [];

    /// <summary>
    /// Maximum number of tool calls (e.g. bash commands) allowed per user message.
    /// Once exceeded, additional tool calls are rejected with feedback to the model.
    /// Set to 0 or negative to disable the limit. Default is 10.
    /// </summary>
    public int MaxToolCallsPerMessage { get; set; } = 10;
}
