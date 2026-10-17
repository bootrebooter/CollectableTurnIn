// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;

namespace CollectableTurnIn;

// Framework-thread-owned operation identity and terminal event outbox.
public sealed class OperationTracker
{
    public const int ApiVersion = 1;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string? Id { get; private set; }
    public string? Operation { get; private set; }
    public bool Active => Id != null;
    public Completion? LastResult { get; private set; }
    private readonly Queue<string> completions = new();

    public string Begin(string operation)
    {
        if (Active) throw new InvalidOperationException("An operation is already active.");
        Id = Guid.NewGuid().ToString("N");
        Operation = operation;
        return Id;
    }

    public void Finish(string code, string message, int completedCount)
    {
        if (Id == null) return;
        LastResult = new Completion(ApiVersion, Id, Operation!, code == "Completed", code,
            Operation == "TurnIn" ? completedCount : 0, message);
        // Clear before notification so subscribers can safely schedule another operation.
        Id = null;
        Operation = null;
        completions.Enqueue(JsonSerializer.Serialize(LastResult, JsonOptions));
    }

    public string[] TakeCompletions()
    {
        var result = completions.ToArray();
        completions.Clear();
        return result;
    }

    public static string Reply(bool accepted, string code, string? operationId = null) =>
        JsonSerializer.Serialize(new { apiVersion = ApiVersion, accepted, code, operationId }, JsonOptions);

    public sealed record Completion(int ApiVersion, string OperationId, string Operation,
        bool Success, string Code, int CompletedCount, string Message);
}
