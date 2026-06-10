using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.AI.AgentServer.Invocations;
using GitHub.Copilot.SDK;

namespace FoundryDiagnosisAgent.Agent;

public sealed class GitHubCopilotInvocationHandler(
    CopilotSessionManager sessionManager,
    HostedAgentDiagnostics diagnostics,
    ILogger<GitHubCopilotInvocationHandler> logger) : InvocationHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public override async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        CopilotInvocationRequest? body = await ReadInvocationRequestAsync(request, cancellationToken);

        string prompt = body?.Input ?? body?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsJsonAsync(
                new
                {
                    error = "invalid_request",
                    message = "Request body must contain a non-empty \"input\" string.",
                },
                JsonOptions,
                cancellationToken);
            return;
        }

        if (HostedAgentDiagnostics.TryParseCommand(prompt, out DiagnosticCommand command, out string? commandArgument))
        {
            string diagnosticReport;
            if (command == DiagnosticCommand.Http)
            {
                diagnosticReport = BuildHttpHeadersReport(request);
            }
            else
            {
                diagnosticReport = await diagnostics.RunPromptCommandAsync(command, cancellationToken, commandArgument);
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers.Append("X-Accel-Buffering", "no");

            await WriteDoneAsync(response, context, diagnosticReport, string.Empty, cancellationToken);
            return;
        }

        CopilotSession session = await sessionManager.GetSessionAsync(context.SessionId, cancellationToken);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");

        Channel<SessionEvent> events = Channel.CreateUnbounded<SessionEvent>();
        using IDisposable subscription = session.On(sessionEvent => events.Writer.TryWrite(sessionEvent));

        string? completeMessage = null;
        StringBuilder deltaText = new();
        MessageOptions message = new()
        {
            Prompt = prompt,
            RequestHeaders = new Dictionary<string, string>
            {
                ["x-agent-invocation-id"] = context.InvocationId,
            },
        };

        sessionManager.ResetToolCallBudget(context.SessionId);
        Task<string> sendTask = session.SendAsync(message, cancellationToken);

        try
        {
            while (await events.Reader.WaitToReadAsync(cancellationToken))
            {
                while (events.Reader.TryRead(out SessionEvent? sessionEvent))
                {
                    switch (sessionEvent)
                    {
                        case AssistantMessageDeltaEvent deltaEvent when !string.IsNullOrEmpty(deltaEvent.Data?.DeltaContent):
                            deltaText.Append(deltaEvent.Data.DeltaContent);
                            await WriteEventAsync(response, deltaEvent, cancellationToken);
                            break;

                        case AssistantMessageEvent messageEvent when !string.IsNullOrEmpty(messageEvent.Data?.Content):
                            completeMessage = messageEvent.Data.Content;
                            await WriteEventAsync(response, messageEvent, cancellationToken);
                            break;

                        case SessionErrorEvent errorEvent:
                            logger.LogError("Copilot session {SessionId} reported an error: {EventType}", context.SessionId, errorEvent.Type);
                            string errorPayload = JsonSerializer.Serialize(errorEvent, errorEvent.GetType(), JsonOptions);
                            
                            if (IsAuthOrRbacErrorPayload(errorPayload))
                            {
                                logger.LogWarning("Session error detected as auth/RBAC failure. Showing diagnostic options.");
                                string guide = await diagnostics.BuildAuthFailureGuideWithDiagOptionsAsync(cancellationToken);
                                await WriteDoneAsync(response, context, guide, string.Empty, cancellationToken);
                                return;
                            }
                            
                            await WritePayloadAsync(
                                response,
                                new
                                {
                                    type = "error",
                                    invocationId = context.InvocationId,
                                    sessionId = context.SessionId,
                                    detail = errorEvent,
                                },
                                cancellationToken);
                            await WriteDoneAsync(response, context, completeMessage, deltaText.ToString(), cancellationToken);
                            return;

                        case SessionIdleEvent idleEvent:
                            await WriteEventAsync(response, idleEvent, cancellationToken);
                            await sendTask;
                            await WriteDoneAsync(response, context, completeMessage, deltaText.ToString(), cancellationToken);
                            return;

                        default:
                            await WriteEventAsync(response, sessionEvent, cancellationToken);
                            break;
                    }
                }
            }

            await sendTask;
            await WriteDoneAsync(response, context, completeMessage, deltaText.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            if (diagnostics.IsAuthOrRbacFailure(ex))
            {
                logger.LogWarning(ex, "Copilot invocation {InvocationId} failed with auth/RBAC error. Showing diagnostic options.", context.InvocationId);
                string guide = await diagnostics.BuildAuthFailureGuideWithDiagOptionsAsync(cancellationToken);
                await WriteDoneAsync(response, context, guide, string.Empty, cancellationToken);
                return;
            }

            logger.LogError(ex, "Copilot invocation {InvocationId} failed.", context.InvocationId);
            await WritePayloadAsync(
                response,
                new
                {
                    type = "error",
                    invocationId = context.InvocationId,
                    sessionId = context.SessionId,
                    message = ex.Message,
                },
                cancellationToken);
            await WriteDoneAsync(response, context, completeMessage, deltaText.ToString(), cancellationToken);
        }
    }

    private static bool IsAuthOrRbacErrorPayload(string errorPayload)
    {
        if (string.IsNullOrWhiteSpace(errorPayload))
        {
            return false;
        }

        string[] signals =
        [
            "permissiondenied",
            "lacks the required data action",
            "principal does not have access",
            "authentication failed with provider",
            "agents/write",
            "http 401",
            "401",
            "forbidden",
            "403",
        ];

        return signals.Any(signal => errorPayload.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static Task WriteEventAsync(HttpResponse response, SessionEvent sessionEvent, CancellationToken cancellationToken) =>
        WriteRawPayloadAsync(response, JsonSerializer.Serialize(sessionEvent, sessionEvent.GetType(), JsonOptions), cancellationToken);

    private static Task WritePayloadAsync(HttpResponse response, object payload, CancellationToken cancellationToken) =>
        WriteRawPayloadAsync(response, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

    private static async Task WriteDoneAsync(
        HttpResponse response,
        InvocationContext context,
        string? completeMessage,
        string deltaText,
        CancellationToken cancellationToken)
    {
        await WritePayloadAsync(
            response,
            new
            {
                type = "done",
                invocationId = context.InvocationId,
                sessionId = context.SessionId,
                fullText = string.IsNullOrEmpty(completeMessage) ? deltaText : completeMessage,
            },
            cancellationToken);
    }

    private static async Task WriteRawPayloadAsync(
        HttpResponse response,
        string jsonPayload,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"data: {jsonPayload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<CopilotInvocationRequest?> ReadInvocationRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasJsonContentType())
        {
            try
            {
                return await request.ReadFromJsonAsync<CopilotInvocationRequest>(JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        using StreamReader reader = new(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string rawBody = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            CopilotInvocationRequest? structured = JsonSerializer.Deserialize<CopilotInvocationRequest>(rawBody, JsonOptions);
            if (structured is not null)
            {
                return structured;
            }
        }
        catch (JsonException)
        {
            // Treat non-JSON body as a direct prompt.
        }

        return new CopilotInvocationRequest(rawBody, null);
    }

    private sealed record CopilotInvocationRequest(string? Input, string? Message);

    private static string BuildHttpHeadersReport(HttpRequest request)
    {
        StringBuilder report = new();
        report.AppendLine("## 🌐 HTTP Request Headers");
        report.AppendLine();
        report.AppendLine($"**Method:** {request.Method}");
        report.AppendLine($"**Path:** {request.Path}");
        report.AppendLine($"**Scheme:** {request.Scheme}");
        report.AppendLine($"**Host:** {request.Host}");
        report.AppendLine($"**Content-Type:** {request.ContentType}");
        report.AppendLine($"**Content-Length:** {request.ContentLength}");
        report.AppendLine();
        report.AppendLine("### Headers");
        report.AppendLine();
        report.AppendLine("| Header | Value |");
        report.AppendLine("|--------|-------|");
        foreach (var header in request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            string value = header.Key.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                           header.Key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                           header.Key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                           header.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                ? "***REDACTED***"
                : header.Value.ToString();
            report.AppendLine($"| {header.Key} | {value} |");
        }

        return report.ToString().TrimEnd();
    }
}
