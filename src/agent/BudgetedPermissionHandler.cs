using GitHub.Copilot.SDK;
using PermissionRequestResult = GitHub.Copilot.SDK.PermissionRequestResult;

namespace FoundryDiagnosisAgent.Agent;

/// <summary>
/// A permission handler that enforces a maximum number of tool calls per user message.
/// Once the budget is exhausted, further tool calls are rejected with feedback asking
/// the model to stop and report what it has found so far.
/// </summary>
public sealed class BudgetedPermissionHandler
{
    private readonly int _maxToolCalls;
    private readonly ILogger _logger;
    private int _toolCallCount;

    public BudgetedPermissionHandler(int maxToolCalls, ILogger logger)
    {
        _maxToolCalls = maxToolCalls;
        _logger = logger;
    }

    /// <summary>
    /// Resets the tool call counter. Call this when a new user message is sent.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _toolCallCount, 0);
    }

    public Task<PermissionRequestResult> HandleAsync(PermissionRequest request, PermissionInvocation invocation)
    {
        if (_maxToolCalls <= 0)
        {
            return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });
        }

        int current = Interlocked.Increment(ref _toolCallCount);

        if (current > _maxToolCalls)
        {
            _logger.LogWarning(
                "Tool call budget exhausted ({Current}/{Max}). Rejecting tool call.",
                current, _maxToolCalls);

            return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Rejected, Rules = [
                $"Tool call budget exhausted ({_maxToolCalls} calls used). " +
                "You MUST stop calling tools now. Summarize your findings so far, " +
                "state what you were unable to complete, and provide your best diagnosis " +
                "based on the evidence gathered."]});
        }

        return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });
    }
}
