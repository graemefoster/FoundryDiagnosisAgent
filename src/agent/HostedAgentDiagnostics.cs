using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace FoundryDiagnosisAgent.Agent;

public enum DiagnosticCommand
{
    All,
    Version,
    Network,
    Rbac,
    RbacManagement,
    Environment,
    Llm,
    Http,
}

public sealed class HostedAgentDiagnostics(
    DefaultAzureCredential credential,
    IOptions<CopilotHostedAgentOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<HostedAgentDiagnostics> logger)
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] SecretMarkers =
    [
        "SECRET",
        "TOKEN",
        "PASSWORD",
        "KEY",
        "CONNECTIONSTRING",
        "CONNECTION_STRING",
        "CLIENTSECRET",
        "CLIENT_SECRET",
    ];

    private static readonly TokenRequestContext ByokTokenRequestContext = new(["https://ai.azure.com/.default"]);
    private static readonly TokenRequestContext ArmTokenRequestContext = new(["https://management.azure.com/.default"]);
    private const string AuthorizationApiVersion = "2022-04-01";

    private readonly CopilotHostedAgentOptions _options = options.Value;

    public static bool TryParseCommand(string prompt, out DiagnosticCommand command, out string? commandArgument)
    {
        command = DiagnosticCommand.All;
        commandArgument = null;
        string normalized = prompt.Trim();
        if (!normalized.StartsWith("/diag", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/diagnose", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            command = DiagnosticCommand.All;
            return true;
        }

        string scope = parts[1];
        if (IgnoreCase.Equals(scope, "all"))
        {
            command = DiagnosticCommand.All;
            return true;
        }

        if (IgnoreCase.Equals(scope, "version") || IgnoreCase.Equals(scope, "ver") || IgnoreCase.Equals(scope, "build"))
        {
            command = DiagnosticCommand.Version;
            return true;
        }

        if (IgnoreCase.Equals(scope, "network"))
        {
            command = DiagnosticCommand.Network;
            if (parts.Length >= 3)
            {
                commandArgument = parts[2];
            }
            return true;
        }

        if (IgnoreCase.Equals(scope, "rbac") || IgnoreCase.Equals(scope, "auth"))
        {
            command = DiagnosticCommand.Rbac;
            return true;
        }

        if (IgnoreCase.Equals(scope, "rbac2") || IgnoreCase.Equals(scope, "arm"))
        {
            command = DiagnosticCommand.RbacManagement;
            return true;
        }

        if (IgnoreCase.Equals(scope, "env") || IgnoreCase.Equals(scope, "environment"))
        {
            command = DiagnosticCommand.Environment;
            return true;
        }

        if (IgnoreCase.Equals(scope, "llm") || IgnoreCase.Equals(scope, "model"))
        {
            command = DiagnosticCommand.Llm;
            if (parts.Length >= 3)
            {
                commandArgument = parts[2];
            }
            return true;
        }

        if (IgnoreCase.Equals(scope, "http") || IgnoreCase.Equals(scope, "headers"))
        {
            command = DiagnosticCommand.Http;
            return true;
        }

        command = DiagnosticCommand.All;
        return true;
    }

    public async Task<string> RunPromptCommandAsync(DiagnosticCommand command, CancellationToken cancellationToken, string? commandArgument = null)
    {
        StringBuilder report = new();
        report.AppendLine("## 🔍 Diagnostics Report");
        report.AppendLine();

        AppendAgentVersionSection(report);

        await AppendReportForCommandAsync(report, command, commandArgument, cancellationToken);
        return report.ToString().TrimEnd();
    }

    public bool IsAuthOrRbacFailure(Exception ex)
    {
        if (TryGetStatusCode(ex, out int statusCode) && (statusCode == 401 || statusCode == 403))
        {
            return true;
        }

        string message = ex.Message;
        string[] signals =
        [
            "permissiondenied",
            "lacks the required data action",
            "principal does not have access",
            "authentication failed with provider",
            "agents/write",
            "forbidden",
            "401",
            "403",
        ];

        return signals.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> BuildAuthFailureGuideWithDiagOptionsAsync(CancellationToken cancellationToken)
    {
        StringBuilder report = new();
        report.AppendLine("## Authentication or Access Denied");
        report.AppendLine();
        report.AppendLine("> **Your request failed due to authentication or permission issues.**");
        report.AppendLine();

        string clientId = await ResolveAgentClientIdAsync(cancellationToken);
        report.AppendLine($"**This agent's identity (client ID):** `{clientId}`");
        report.AppendLine();
        report.AppendLine("### What to do");
        report.AppendLine();
        report.AppendLine($"> Assign the `Foundry User` role to this identity (`{clientId}`) on your Foundry project.");
        report.AppendLine();
        report.AppendLine("> **Note:** RBAC role assignments can take up to 5 minutes to propagate. If you have just assigned the role, please wait and try again.");
        report.AppendLine();
        report.AppendLine("### Diagnostic Commands");
        report.AppendLine();
        report.AppendLine("| Command | Description |");
        report.AppendLine("|---------|-------------|");
        report.AppendLine("| `/diag rbac` | Check RBAC/authentication for data-plane access |");
        report.AppendLine("| `/diag arm` | Check management-plane role assignments |");
        report.AppendLine("| `/diag llm` | Test a simple /responses call against the model |");
        report.AppendLine("| `/diag network [hostname]` | Check network connectivity to Foundry endpoint (or optional hostname) |");
        report.AppendLine("| `/diag env` | Show environment variables and configuration |");
        report.AppendLine("| `/diag all` | Run all diagnostics |");

        return report.ToString().TrimEnd();
    }

    private async Task AppendReportForCommandAsync(StringBuilder report, DiagnosticCommand command, string? commandArgument, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case DiagnosticCommand.Version:
                break;

            case DiagnosticCommand.Network:
                await AppendNetworkSectionAsync(report, cancellationToken, commandArgument);
                break;

            case DiagnosticCommand.Rbac:
                await AppendRbacSectionAsync(report, cancellationToken);
                await AppendRbacManagementSectionAsync(report, cancellationToken);
                break;

            case DiagnosticCommand.RbacManagement:
                await AppendRbacManagementSectionAsync(report, cancellationToken);
                break;

            case DiagnosticCommand.Environment:
                AppendEnvironmentSection(report);
                break;

            case DiagnosticCommand.Llm:
                await AppendLlmSectionAsync(report, commandArgument, cancellationToken);
                break;

            default:
                await AppendNetworkSectionAsync(report, cancellationToken, null);
                await AppendRbacSectionAsync(report, cancellationToken);
                await AppendRbacManagementSectionAsync(report, cancellationToken);
                AppendEnvironmentSection(report);
                break;
        }
    }

    private static void AppendAgentVersionSection(StringBuilder report)
    {
        Assembly assembly = typeof(HostedAgentDiagnostics).Assembly;
        string informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "<unknown>";

        string buildVersion = Environment.GetEnvironmentVariable("DIAGNOSTICS_AGENT_BUILD_VERSION") ?? "<not set>";
        string gitSha = Environment.GetEnvironmentVariable("DIAGNOSTICS_AGENT_GIT_SHA") ?? "<not set>";
        string foundryAgentVersion = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_VERSION") ?? "<not set>";
        string foundryAgentName = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME") ?? "<not set>";

        report.AppendLine("### Agent Version");
        report.AppendLine();
        report.AppendLine($"> **{foundryAgentName}** v{foundryAgentVersion} — build {buildVersion} ({gitSha})");
        report.AppendLine();
    }

    private async Task AppendNetworkSectionAsync(StringBuilder report, CancellationToken cancellationToken, string? hostname)
    {
        string endpoint = hostname != null
            ? (hostname.Contains("://") ? hostname : $"https://{hostname}")
            : GetFoundryProjectEndpoint();

        report.AppendLine("---");
        report.AppendLine("### Network Connectivity");
        report.AppendLine();
        report.AppendLine($"Endpoint: `{endpoint}`");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            report.AppendLine();
            report.AppendLine("❌ **Failed** — Could not parse endpoint URL");
            report.AppendLine();
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpointUri.Host, cancellationToken);
            if (addresses.Length == 0)
            {
                report.AppendLine();
                report.AppendLine($"❌ **DNS** — `{endpointUri.Host}` resolved but returned no IP addresses");
                report.AppendLine();
                return;
            }

            report.AppendLine($" → `{endpointUri.Host}` → {string.Join(", ", addresses.Select(a => $"`{a}`"))}");
        }
        catch (Exception ex)
        {
            report.AppendLine();
            report.AppendLine($"❌ **DNS failed** — `{ex.Message}`");
            report.AppendLine();
            return;
        }

        bool tcpReachable = await ProbeTcpAsync(endpointUri.Host, 443, cancellationToken);
        string sslResult = await ProbeSslAsync(endpointUri.Host, cancellationToken);
        string pingResult = await ProbeHttpsAsync(endpointUri, cancellationToken);

        report.AppendLine();
        report.AppendLine("| Check | Result |");
        report.AppendLine("|-------|--------|");
        report.AppendLine($"| TCP :443 | {(tcpReachable ? "✅ reachable" : "❌ unreachable")} |");
        report.AppendLine($"| TLS/SSL | {sslResult} |");
        report.AppendLine($"| HTTPS probe | {pingResult} |");
        report.AppendLine();
    }

    private async Task AppendRbacManagementSectionAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        string scopeResourceId = GetRbacScopeResourceId();

        report.AppendLine("---");
        report.AppendLine("### Management-Plane RBAC");
        report.AppendLine();
        report.AppendLine("Checking ARM role assignments for this identity.");
        report.AppendLine();

        if (scopeResourceId.StartsWith("<", StringComparison.Ordinal))
        {
            report.AppendLine("⚠️ **Skipped** — no scope configured");
            report.AppendLine();
            report.AppendLine("> Set one of: `RBAC_SCOPE_RESOURCE_ID`, `FOUNDRY_PROJECT_ARM_ID`, `FOUNDRY_PROJECT_RESOURCE_ID`, or `AZURE_AI_PROJECT_RESOURCE_ID`");
            report.AppendLine();
            return;
        }

        report.AppendLine($"Scope: `{ShortenResourceId(scopeResourceId)}`");
        report.AppendLine();

        AccessToken armToken;
        try
        {
            armToken = await credential.GetTokenAsync(ArmTokenRequestContext, cancellationToken);
            report.AppendLine($"✅ ARM token acquired");
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ **ARM token failed** — `{ex.GetType().Name}: {ex.Message}`");
            report.AppendLine();
            return;
        }

        string principalObjectId = TryGetJwtClaim(armToken.Token, "oid") ?? "<unknown>";
        string tenantId = TryGetJwtClaim(armToken.Token, "tid") ?? "<unknown>";
        report.AppendLine($"  Principal: `{principalObjectId}` / Tenant: `{tenantId}`");
        report.AppendLine();

        using HttpClient client = httpClientFactory.CreateClient();

        string normalizedScope = NormalizeResourceId(scopeResourceId);
        string permissionsUrl = $"https://management.azure.com{normalizedScope}/providers/Microsoft.Authorization/permissions?api-version={AuthorizationApiVersion}";
        using HttpRequestMessage permissionsRequest = new(HttpMethod.Get, permissionsUrl);
        permissionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken.Token);

        try
        {
            using HttpResponseMessage permissionsResponse = await client.SendAsync(permissionsRequest, cancellationToken);
            if (permissionsResponse.IsSuccessStatusCode)
            {
                string body = await permissionsResponse.Content.ReadAsStringAsync(cancellationToken);
                (int actions, int notActions) = SummarizePermissions(body);
                report.AppendLine($"✅ Permissions: {actions} actions, {notActions} notActions");
            }
            else if (permissionsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                report.AppendLine("⚠️ Cannot read permissions (need `Microsoft.Authorization/permissions/read`)");
            }
            else
            {
                report.AppendLine($"⚠️ Permissions API: `{(int)permissionsResponse.StatusCode} {permissionsResponse.StatusCode}`");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ Permissions API failed: `{ex.GetType().Name}: {ex.Message}`");
        }

        string roleAssignmentsUrl = $"https://management.azure.com{normalizedScope}/providers/Microsoft.Authorization/roleAssignments?api-version={AuthorizationApiVersion}";

        try
        {
            using HttpRequestMessage roleAssignmentsRequest = new(HttpMethod.Get, roleAssignmentsUrl);
            roleAssignmentsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken.Token);

            using HttpResponseMessage assignmentsResponse = await client.SendAsync(roleAssignmentsRequest, cancellationToken);
            HttpStatusCode assignmentsStatusCode = assignmentsResponse.StatusCode;

            if (assignmentsStatusCode == HttpStatusCode.Forbidden)
            {
                report.AppendLine("❌ Cannot read role assignments (need `Microsoft.Authorization/roleAssignments/read`)");
                report.AppendLine();
                report.AppendLine("> **Action needed:** Assign `Owner`, `User Access Administrator`, or `Access Control Reader` at this scope.");
                report.AppendLine();
                return;
            }

            if (!assignmentsResponse.IsSuccessStatusCode)
            {
                report.AppendLine($"⚠️ Role assignments API: `{(int)assignmentsStatusCode} {assignmentsStatusCode}`");
                report.AppendLine();
                return;
            }

            string assignmentsBody = await assignmentsResponse.Content.ReadAsStringAsync(cancellationToken);

            List<(string Scope, string RoleDefinitionId, string PrincipalId)> assignments = ParseRoleAssignments(assignmentsBody);
            if (!string.Equals(principalObjectId, "<unknown>", StringComparison.Ordinal))
            {
                assignments = assignments
                    .Where(a => string.Equals(a.PrincipalId, principalObjectId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (assignments.Count == 0)
            {
                report.AppendLine();
                report.AppendLine("❌ **No role assignments found** for this principal at this scope.");
                report.AppendLine();
                report.AppendLine("> **Action needed:** Assign the `Foundry User` role to this identity on the Foundry project.");
                report.AppendLine();
                return;
            }

            report.AppendLine();
            report.AppendLine($"✅ **{assignments.Count} role assignment(s) found:**");
            report.AppendLine();
            report.AppendLine("| Role | Scope |");
            report.AppendLine("|------|-------|");
            int take = Math.Min(5, assignments.Count);
            for (int i = 0; i < take; i++)
            {
                (string assignmentScope, string roleDefinitionId, string _) = assignments[i];
                string roleName = await ResolveRoleNameAsync(client, armToken.Token, roleDefinitionId, cancellationToken);
                report.AppendLine($"| {roleName} | `{ShortenResourceId(assignmentScope)}` |");
            }

            if (assignments.Count > 5)
            {
                report.AppendLine($"| ... | +{assignments.Count - 5} more |");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ Role assignments call failed: `{ex.GetType().Name}: {ex.Message}`");
        }

        report.AppendLine();
    }

    private async Task AppendRbacSectionAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        string endpoint = GetFoundryProjectEndpoint();
        string model = GetModelDeploymentName();

        report.AppendLine("---");
        report.AppendLine("### Data-Plane RBAC");
        report.AppendLine();
        report.AppendLine($"Testing whether this identity can call Foundry model APIs (`{model}`)");
        report.AppendLine();

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(ByokTokenRequestContext, cancellationToken);
            report.AppendLine($"✅ Token acquired (scope: `https://ai.azure.com/.default`, expires `{token.ExpiresOn.UtcDateTime:HH:mm:ss UTC}`)");
            report.AppendLine();
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ **Token acquisition failed** — `{ex.GetType().Name}: {ex.Message}`");
            report.AppendLine();
            string clientId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_INSTANCE_CLIENT_ID") ?? "<unknown>";
            report.AppendLine($"> **Action needed:** Assign the `Foundry User` role to this agent's identity (`{clientId}`) on the Foundry project.");
            report.AppendLine();
            return;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            report.AppendLine($"❌ **Endpoint invalid** — cannot parse `{endpoint}`");
            report.AppendLine();
            return;
        }

        Uri modelsUri = new($"{endpointUri.AbsoluteUri.TrimEnd('/')}/openai/v1/responses");
        using HttpClient client = httpClientFactory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, modelsUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { model, input = "hi", max_output_tokens = 16 }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            int statusCode = (int)response.StatusCode;

            if (statusCode >= 200 && statusCode < 300)
            {
                report.AppendLine($"✅ **Models API** — `{statusCode} {response.StatusCode}`");
                report.AppendLine();
                report.AppendLine("> Identity has data-plane access. No action needed.");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                report.AppendLine($"❌ **Models API** — `401 Unauthorized`");
                report.AppendLine();
                report.AppendLine("> **Action needed:** Assign the `Foundry User` role to this identity on the Foundry project.");
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                report.AppendLine($"❌ **Models API** — `403 Forbidden`");
                report.AppendLine();
                report.AppendLine("> The identity is known but access is denied. Could be:");
                report.AppendLine(">");
                report.AppendLine("> 1. Missing role — assign `Foundry User` on the Foundry project");
                report.AppendLine("> 2. Network restriction — hitting a public endpoint when private networking is configured");
                report.AppendLine("> 3. IP firewall — source IP is not in the allow-list");
            }
            else
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                string errorMessage = TryExtractErrorMessage(errorBody) ?? $"{statusCode} {response.StatusCode}";
                report.AppendLine($"⚠️ **Responses API** — `{statusCode} {response.StatusCode}`");
                report.AppendLine();
                report.AppendLine($"> {errorMessage}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ **Models API call failed** — `{ex.GetType().Name}: {ex.Message}`");
        }

        report.AppendLine();
        report.AppendLine($"Tested: `GET {modelsUri}`");
        report.AppendLine();
    }

    private async Task AppendLlmSectionAsync(StringBuilder report, string? modelOverride, CancellationToken cancellationToken)
    {
        string endpoint = GetFoundryProjectEndpoint();
        string model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : GetModelDeploymentName();

        report.AppendLine("---");
        report.AppendLine("### LLM Invocation Test");
        report.AppendLine();
        report.AppendLine($"Testing model `{model}` at `{endpoint}`");
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            report.AppendLine($"_(using explicit deployment: `{modelOverride}`)_");
        }
        report.AppendLine();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
        {
            report.AppendLine($"❌ **Endpoint invalid** — cannot parse `{endpoint}`");
            report.AppendLine();
            return;
        }

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(ByokTokenRequestContext, cancellationToken);
            report.AppendLine($"✅ Token acquired (expires `{token.ExpiresOn.UtcDateTime:HH:mm:ss UTC}`)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"❌ **Token acquisition failed** — `{ex.GetType().Name}: {ex.Message}`");
            report.AppendLine();
            return;
        }

        string baseUrl = endpointUri.AbsoluteUri.TrimEnd('/');
        using HttpClient client = httpClientFactory.CreateClient();

        // --- /responses endpoint ---
        report.AppendLine();
        report.AppendLine("#### Responses API (`/openai/v1/responses`)");
        report.AppendLine();
        await TestLlmEndpointAsync(
            report, client, token.Token,
            new Uri($"{baseUrl}/openai/v1/responses"),
            model,
            JsonSerializer.Serialize(new { model, input = "Reply with the single word: hello", max_output_tokens = 16 }),
            TryExtractResponseOutput,
            cancellationToken);

        // --- /chat/completions endpoint ---
        report.AppendLine("#### Chat Completions API (`/openai/v1/chat/completions`)");
        report.AppendLine();
        await TestLlmEndpointAsync(
            report, client, token.Token,
            new Uri($"{baseUrl}/openai/v1/chat/completions"),
            model,
            JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "Reply with the single word: hello" } },
                max_completion_tokens = 16,
            }),
            TryExtractChatCompletionOutput,
            cancellationToken);
    }

    private async Task TestLlmEndpointAsync(
        StringBuilder report,
        HttpClient client,
        string bearerToken,
        Uri endpointUri,
        string model,
        string requestBody,
        Func<string, string?> outputExtractor,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            int statusCode = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            report.AppendLine("| Field | Value |");
            report.AppendLine("|-------|-------|");
            report.AppendLine($"| Endpoint | `{endpointUri}` |");
            report.AppendLine($"| Model | `{model}` |");
            report.AppendLine($"| Status | `{statusCode} {response.StatusCode}` |");
            report.AppendLine($"| Latency | `{stopwatch.ElapsedMilliseconds} ms` |");

            if (statusCode >= 200 && statusCode < 300)
            {
                string? outputText = outputExtractor(body);
                report.AppendLine($"| Output | `{outputText ?? "<could not parse>"}` |");
                report.AppendLine();
                report.AppendLine("✅ **Success.**");
            }
            else
            {
                string errorMessage = TryExtractErrorMessage(body) ?? body;
                report.AppendLine($"| Error | `{Truncate(errorMessage, 200)}` |");
                report.AppendLine();
                report.AppendLine($"❌ **Failed with `{statusCode}`.**");

                if (statusCode == 401 || statusCode == 403)
                {
                    report.AppendLine();
                    report.AppendLine("> **Action needed:** Assign the `Foundry User` role to this identity on the Foundry project.");
                }
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            report.AppendLine($"❌ **Request failed** — `{ex.GetType().Name}: {ex.Message}` ({stopwatch.ElapsedMilliseconds} ms)");
        }

        report.AppendLine();
    }

    private static string? TryExtractResponseOutput(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement contentItem in content.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty("text", out JsonElement text))
                            {
                                return text.GetString();
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Not parseable
        }

        return null;
    }

    private static string? TryExtractChatCompletionOutput(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out JsonElement message) &&
                        message.TryGetProperty("content", out JsonElement content))
                    {
                        return content.GetString();
                    }
                }
            }
        }
        catch
        {
            // Not parseable
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private void AppendEnvironmentSection(StringBuilder report)
    {
        report.AppendLine("---");
        report.AppendLine("### Environment");
        report.AppendLine();

        string[] importantVars =
        [
            "FOUNDRY_PROJECT_ENDPOINT",
            "AZURE_AI_PROJECT_ENDPOINT",
            "MODEL_DEPLOYMENT_NAME",
            "AZURE_AI_MODEL_DEPLOYMENT_NAME",
            "DIAGNOSTICS_AGENT_BUILD_VERSION",
            "DIAGNOSTICS_AGENT_GIT_SHA",
            "RBAC_SCOPE_RESOURCE_ID",
            "FOUNDRY_PROJECT_ARM_ID",
            "FOUNDRY_PROJECT_RESOURCE_ID",
            "AZURE_AI_PROJECT_RESOURCE_ID",
            "AZURE_CLIENT_ID",
            "AZURE_TENANT_ID",
            "AZURE_AUTHORITY_HOST",
            "MSI_ENDPOINT",
            "IDENTITY_ENDPOINT",
            "IDENTITY_HEADER",
            "WEBSITE_HOSTNAME",
            "HOSTNAME",
        ];

        report.AppendLine("| Variable | Value |");
        report.AppendLine("|----------|-------|");
        foreach (string key in importantVars)
        {
            string value = SanitizeEnvironmentValue(key, Environment.GetEnvironmentVariable(key));
            report.AppendLine($"| `{key}` | `{value}` |");
        }

        report.AppendLine();
        report.AppendLine("#### Full environment snapshot");
        report.AppendLine();

        IDictionary env = Environment.GetEnvironmentVariables();
        List<DictionaryEntry> entries = [];
        foreach (DictionaryEntry entry in env)
        {
            entries.Add(entry);
        }

        report.AppendLine("| Variable | Value |");
        report.AppendLine("|----------|-------|");
        foreach (DictionaryEntry entry in entries.OrderBy(e => e.Key?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            string key = entry.Key?.ToString() ?? string.Empty;
            string? value = entry.Value?.ToString();
            report.AppendLine($"| `{key}` | `{SanitizeEnvironmentValue(key, value)}` |");
        }

        report.AppendLine();
    }

    private static string? TryExtractErrorMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString();
            }
        }
        catch
        {
            // Not JSON or unexpected shape
        }

        return null;
    }

    private static string ShortenResourceId(string resourceId)
    {
        // Shorten long ARM resource IDs to just the meaningful tail
        // e.g. /subscriptions/.../resourceGroups/RG/providers/Microsoft.CognitiveServices/accounts/Acct/projects/Proj
        //   -> ...RG / Acct / Proj
        string[] parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return resourceId;
        }

        // Extract just the resource names (skip the type segments)
        List<string> names = [];
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "subscriptions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[i], "providers", StringComparison.OrdinalIgnoreCase))
            {
                i++; // skip value after these
                if (string.Equals(parts[i - 1], "resourceGroups", StringComparison.OrdinalIgnoreCase) && i < parts.Length)
                {
                    names.Add(parts[i]);
                }
            }
            else if (i > 0 && !parts[i].Contains('.'))
            {
                // This is likely a resource name (not a type like Microsoft.CognitiveServices)
                names.Add(parts[i]);
            }
        }

        return names.Count > 0 ? $"...{string.Join(" / ", names)}" : resourceId;
    }

    private Task<string> ResolveAgentClientIdAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken; // reserved for future use
        string clientId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_INSTANCE_CLIENT_ID")
                          ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID")
                          ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
                          ?? "<could not determine>";
        return Task.FromResult(clientId);
    }

    private static async Task<bool> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ProbeSslAsync(string host, CancellationToken cancellationToken)
    {
        using TcpClient tcp = new();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            await tcp.ConnectAsync(host, 443, timeoutCts.Token);

            using System.Net.Security.SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, timeoutCts.Token);

            var cert = ssl.RemoteCertificate as System.Security.Cryptography.X509Certificates.X509Certificate2
                       ?? new System.Security.Cryptography.X509Certificates.X509Certificate2(ssl.RemoteCertificate!);

            string subject = cert.Subject;
            string issuer = cert.Issuer;
            DateTime notAfter = cert.NotAfter;
            DateTime notBefore = cert.NotBefore;
            TimeSpan remaining = notAfter - DateTime.UtcNow;

            string status = remaining.TotalDays switch
            {
                <= 0 => "❌ **EXPIRED**",
                <= 30 => "⚠️ expiring soon",
                _ => "✅ valid",
            };

            return $"{status} — `{subject}` (issuer: `{issuer}`, expires `{notAfter:yyyy-MM-dd}`, {(int)remaining.TotalDays}d remaining)";
        }
        catch (Exception ex)
        {
            return $"❌ failed (`{ex.GetType().Name}: {ex.Message}`)";
        }
    }

    private async Task<string> ProbeHttpsAsync(Uri endpointUri, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, endpointUri);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, timeoutCts.Token);
            return $"`{(int)response.StatusCode} {response.StatusCode}`";
        }
        catch (Exception ex)
        {
            return $"failed (`{ex.GetType().Name}: {ex.Message}`)";
        }
    }

    private string GetFoundryProjectEndpoint()
    {
        string? endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        endpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (!string.IsNullOrWhiteSpace(_options.FoundryProjectEndpoint))
        {
            return _options.FoundryProjectEndpoint;
        }

        return "<not configured>";
    }

    private string GetModelDeploymentName()
    {
        string? model = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME");
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        model = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME");
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        if (!string.IsNullOrWhiteSpace(_options.ModelDeploymentName))
        {
            return _options.ModelDeploymentName;
        }

        return "gpt-5.4";
    }

    private static string NormalizeResourceId(string resourceId)
    {
        string normalized = resourceId.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = $"/{normalized}";
        }

        return normalized.TrimEnd('/');
    }

    private string GetRbacScopeResourceId()
    {
        string? scope = Environment.GetEnvironmentVariable("RBAC_SCOPE_RESOURCE_ID");
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        scope = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_RESOURCE_ID");
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        scope = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID");
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        scope = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_RESOURCE_ID");
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope;
        }

        return "<not configured>";
    }

    private static (int Actions, int NotActions) SummarizePermissions(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            {
                return (0, 0);
            }

            int actions = 0;
            int notActions = 0;

            foreach (JsonElement permission in value.EnumerateArray())
            {
                if (permission.TryGetProperty("actions", out JsonElement actionElement) && actionElement.ValueKind == JsonValueKind.Array)
                {
                    actions += actionElement.GetArrayLength();
                }

                if (permission.TryGetProperty("notActions", out JsonElement notActionElement) && notActionElement.ValueKind == JsonValueKind.Array)
                {
                    notActions += notActionElement.GetArrayLength();
                }
            }

            return (actions, notActions);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static List<(string Scope, string RoleDefinitionId, string PrincipalId)> ParseRoleAssignments(string json)
    {
        List<(string Scope, string RoleDefinitionId, string PrincipalId)> assignments = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            {
                return assignments;
            }

            foreach (JsonElement assignment in value.EnumerateArray())
            {
                if (!assignment.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string scope = properties.TryGetProperty("scope", out JsonElement scopeElement)
                    ? scopeElement.GetString() ?? "<unknown>"
                    : "<unknown>";

                string roleDefinitionId = properties.TryGetProperty("roleDefinitionId", out JsonElement roleDefinitionElement)
                    ? roleDefinitionElement.GetString() ?? string.Empty
                    : string.Empty;

                string principalId = properties.TryGetProperty("principalId", out JsonElement principalElement)
                    ? principalElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(roleDefinitionId))
                {
                    continue;
                }

                assignments.Add((scope, roleDefinitionId, principalId));
            }
        }
        catch
        {
            return assignments;
        }

        return assignments;
    }

    private static async Task<string> ResolveRoleNameAsync(
        HttpClient client,
        string armToken,
        string roleDefinitionId,
        CancellationToken cancellationToken)
    {
        string roleDefinitionPath = roleDefinitionId.StartsWith("/", StringComparison.Ordinal)
            ? roleDefinitionId
            : $"/{roleDefinitionId}";
        string url = $"https://management.azure.com{roleDefinitionPath}?api-version={AuthorizationApiVersion}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return roleDefinitionId;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
            {
                return roleDefinitionId;
            }

            if (!properties.TryGetProperty("roleName", out JsonElement roleNameElement))
            {
                return roleDefinitionId;
            }

            return roleNameElement.GetString() ?? roleDefinitionId;
        }
        catch
        {
            return roleDefinitionId;
        }
    }

    private static string? TryGetJwtClaim(string jwt, string claimName)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        string payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = 4 - (payload.Length % 4);
        if (padding is > 0 and < 4)
        {
            payload = payload.PadRight(payload.Length + padding, '=');
        }

        try
        {
            byte[] payloadBytes = Convert.FromBase64String(payload);
            using JsonDocument doc = JsonDocument.Parse(payloadBytes);
            if (!doc.RootElement.TryGetProperty(claimName, out JsonElement claim))
            {
                return null;
            }

            return claim.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeEnvironmentValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        bool looksSecret = SecretMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!looksSecret)
        {
            return value;
        }

        if (value.Length <= 8)
        {
            return "<redacted>";
        }

        return $"{value[..4]}...{value[^4..]}";
    }

    private bool TryGetStatusCode(Exception ex, out int statusCode)
    {
        statusCode = 0;

        if (ex is RequestFailedException requestFailed)
        {
            statusCode = requestFailed.Status;
            return true;
        }

        object? statusCodeValue = ex.GetType().GetProperty("StatusCode")?.GetValue(ex) ??
                                  ex.GetType().GetProperty("Status")?.GetValue(ex) ??
                                  ex.GetType().GetProperty("HttpStatusCode")?.GetValue(ex);

        if (statusCodeValue is int code)
        {
            statusCode = code;
            return true;
        }

        if (statusCodeValue is HttpStatusCode httpStatusCode)
        {
            statusCode = (int)httpStatusCode;
            return true;
        }

        return false;
    }
}
