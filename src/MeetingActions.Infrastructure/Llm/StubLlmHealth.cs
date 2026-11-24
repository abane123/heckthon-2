using MeetingActions.Core.Abstractions;

namespace MeetingActions.Infrastructure.Llm;

/// <summary>Reports the built-in rule extractor as available without external services.</summary>
public sealed class StubLlmHealth : ILlmHealth
{
    /// <summary>Returns the always-available built-in analysis engine status.</summary>
    public Task<LlmHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LlmHealthResult(true, true, "Built-in rules"));
    }
}
