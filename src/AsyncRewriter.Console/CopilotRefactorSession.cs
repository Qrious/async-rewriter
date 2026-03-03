using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncRewriter.Console;

/// <summary>
/// Persists progress for a <c>copilot-refactor</c> run so that the command can be
/// resumed after interruption without re-processing already-completed methods.
///
/// The session file is a JSON document written to the path supplied by the caller.
/// Each successful method completion is flushed to disk immediately so that even a
/// hard crash loses at most the in-flight parallel batch.
/// </summary>
public sealed class CopilotRefactorSession
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // ── Persisted state ────────────────────────────────────────────────────
    public string CallGraphId { get; }
    public DateTimeOffset StartedAt { get; }
    public HashSet<string> CompletedMethodIds { get; }

    private CopilotRefactorSession(string filePath, string callGraphId, DateTimeOffset startedAt, HashSet<string> completed)
    {
        _filePath = filePath;
        CallGraphId = callGraphId;
        StartedAt = startedAt;
        CompletedMethodIds = completed;
    }

    /// <summary>
    /// Opens an existing session file or creates a fresh one for <paramref name="callGraphId"/>.
    /// </summary>
    public static async Task<CopilotRefactorSession> OpenOrCreateAsync(string filePath, string callGraphId)
    {
        if (File.Exists(filePath))
        {
            var json = await File.ReadAllTextAsync(filePath);
            var dto = JsonSerializer.Deserialize<SessionDto>(json, _jsonOptions);
            if (dto != null)
                return new CopilotRefactorSession(
                    filePath,
                    dto.CallGraphId ?? callGraphId,
                    dto.StartedAt,
                    new HashSet<string>(dto.CompletedMethodIds ?? []));
        }

        return new CopilotRefactorSession(
            filePath,
            callGraphId,
            DateTimeOffset.UtcNow,
            []);
    }

    /// <summary>Returns true when the method has already been successfully processed.</summary>
    public bool IsCompleted(string methodId) => CompletedMethodIds.Contains(methodId);

    /// <summary>
    /// Records <paramref name="methodId"/> as complete and flushes the session to disk.
    /// Thread-safe — may be called concurrently from multiple tasks.
    /// </summary>
    public async Task MarkCompletedAsync(string methodId)
    {
        await _writeLock.WaitAsync();
        try
        {
            CompletedMethodIds.Add(methodId);
            await FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task FlushAsync()
    {
        var dto = new SessionDto
        {
            CallGraphId = CallGraphId,
            StartedAt = StartedAt,
            CompletedMethodIds = [.. CompletedMethodIds]
        };

        var tmp = _filePath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(dto, _jsonOptions));
        File.Move(tmp, _filePath, overwrite: true);
    }

    // ── DTO ────────────────────────────────────────────────────────────────

    private sealed class SessionDto
    {
        public string? CallGraphId { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("completedMethodIds")]
        public List<string>? CompletedMethodIds { get; set; }
    }
}
