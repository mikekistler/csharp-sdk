using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.AspNetCore;

/// <summary>
/// Stores the state of long-running tool calls for HTTP polling.
/// </summary>
public sealed class ToolCallStateService
{
    private readonly ConcurrentDictionary<string, ToolCallState> _toolCalls = new();
    private readonly TimeSpan _resultRetention;

    /// <summary>
    /// Initializes a new instance of <see cref="ToolCallStateService"/>.
    /// </summary>
    /// <param name="resultRetention">How long to retain completed results. Default is 1 hour.</param>
    public ToolCallStateService(TimeSpan? resultRetention = null)
    {
        _resultRetention = resultRetention ?? TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Creates a new ToolCallState, records it in _toolCalls.
    /// </summary>
    public void CreateToolCallState(string requestId, Task<Stream> task)
    {
        var state = new ToolCallState(task);
        _toolCalls[requestId] = state;
    }

    /// <summary>
    /// Tries to get the result of a tool call.
    /// Returns null if still in progress, or the result if complete.
    /// </summary>
    public async Task<Stream?> TryGetResult(string requestId)
    {
        if (_toolCalls.TryGetValue(requestId, out var state))
        {
            if (state.Task.IsCompleted)
            {
                state.CompletedAt ??= DateTimeOffset.UtcNow;
            }
            return await state.Task;
        }
        return null;
    }

    /// <summary>
    /// Checks if a tool call exists and is still valid.
    /// </summary>
    public bool ToolCallExists(string requestId)
    {
        return _toolCalls.ContainsKey(requestId);
    }

    /// <summary>
    /// Removes old completed tool calls.
    /// </summary>
    public void CleanupOldToolCalls()
    {
        var now = DateTimeOffset.UtcNow;
        var toRemove = _toolCalls
            .Where(kvp => kvp.Value.CompletedAt.HasValue &&
                         (now - kvp.Value.CompletedAt.Value) > _resultRetention)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _toolCalls.TryRemove(id, out _);
        }
    }

    private sealed class ToolCallState(Task<Stream> task)
    {
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public Task<Stream> Task { get; } = task;
    }
}
