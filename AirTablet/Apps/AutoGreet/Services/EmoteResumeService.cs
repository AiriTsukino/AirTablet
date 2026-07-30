using AutoGreet.Models;

namespace AutoGreet.Services;

/// <summary>
/// Runs a user-configured slash command after AutoGreet finishes greeting and emote queues.
/// </summary>
public sealed class EmoteResumeService
{
    private readonly Configuration config;
    private readonly ChatCommandService chatCommands;
    private readonly object sync = new();
    private bool pendingResume;
    private string? pendingResumeCommand;
    private string pendingResumeReason = string.Empty;
    private DateTimeOffset pendingResumeNotBeforeUtc;

    public string LastStatus { get; private set; } = "Resume emote has not run yet.";
    public string LastCapturedCommand { get; private set; } = string.Empty;
    public string LastResumedCommand { get; private set; } = string.Empty;

    public EmoteResumeService(Configuration config, ChatCommandService chatCommands)
    {
        this.config = config;
        this.chatCommands = chatCommands;
    }

    public bool HasPendingResume
    {
        get
        {
            lock (sync)
                return pendingResume;
        }
    }

    public Task<string?> CaptureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (!config.ResumePreviousEmoteEnabled)
        {
            LastStatus = "Resume emote is disabled.";
            LastCapturedCommand = string.Empty;
            return Task.FromResult<string?>(null);
        }

        var command = NormalizeConfiguredCommand(config.ResumeEmoteCommand);
        LastCapturedCommand = command ?? string.Empty;
        LastStatus = command is null
            ? "Resume emote is enabled, but no slash command is configured."
            : $"Configured resume emote command: {command}";
        return Task.FromResult(command);
    }

    public async Task RequestResumeAfterQueueAsync(string macroName, VisitorKey visitor, string reason, CancellationToken token)
    {
        var command = await CaptureAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var delaySeconds = Math.Clamp(config.ResumeEmoteDelaySeconds, 0.5f, 15.0f);

        lock (sync)
        {
            pendingResume = true;
            pendingResumeCommand = command;
            pendingResumeReason = string.IsNullOrWhiteSpace(reason)
                ? $"Macro '{macroName}' for {visitor.Display} contained an emote."
                : $"Macro '{macroName}' for {visitor.Display}: {reason}.";
            pendingResumeNotBeforeUtc = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        }

        LastStatus = $"Resume emote queued until AutoGreet queues are clear and the emote buffer has passed: {command}";
    }

    public async Task<bool> RunPendingResumeIfReadyAsync(bool queueIdle, bool emoteQueueIdle, CancellationToken token)
    {
        if (!config.ResumePreviousEmoteEnabled)
        {
            lock (sync)
            {
                pendingResume = false;
                pendingResumeCommand = null;
                pendingResumeReason = string.Empty;
                pendingResumeNotBeforeUtc = default;
            }

            return false;
        }

        if (!queueIdle || !emoteQueueIdle)
            return false;

        string? command;
        string reason;
        lock (sync)
        {
            if (!pendingResume || string.IsNullOrWhiteSpace(pendingResumeCommand))
                return false;

            var now = DateTimeOffset.UtcNow;
            if (pendingResumeNotBeforeUtc > now)
            {
                var remaining = pendingResumeNotBeforeUtc - now;
                LastStatus = $"Resume emote is waiting for the current emote to finish. About {remaining.TotalSeconds:0.0}s remaining.";
                return false;
            }

            command = pendingResumeCommand;
            reason = pendingResumeReason;
            pendingResume = false;
            pendingResumeCommand = null;
            pendingResumeReason = string.Empty;
            pendingResumeNotBeforeUtc = default;
        }

        LastStatus = string.IsNullOrWhiteSpace(reason)
            ? $"Running queued resume emote: {command}"
            : $"Running queued resume emote after queues cleared. {reason}";

        await ResumeAsync(command, token).ConfigureAwait(false);
        return true;
    }

    public async Task ResumeAsync(string? command, CancellationToken token)
    {
        if (!config.ResumePreviousEmoteEnabled || string.IsNullOrWhiteSpace(command)) return;
        if (token.IsCancellationRequested) return;

        try
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            var sent = await chatCommands.SendAsync(command, token).ConfigureAwait(false);
            if (sent)
            {
                LastResumedCommand = command;
                LastStatus = $"Resumed configured emote: {command}";
            }
            else
            {
                LastStatus = $"Could not resume configured emote: {chatCommands.LastError}";
            }
        }
        catch (OperationCanceledException)
        {
            LastStatus = "Resume emote was cancelled.";
        }
        catch (Exception ex)
        {
            LastStatus = $"Could not resume configured emote: {ex.Message}";
            DalamudServices.Log.Warning(ex, "AutoGreet could not resume the configured emote.");
        }
    }

    private static string? NormalizeConfiguredCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = command.Trim();
        if (!command.StartsWith('/'))
            command = "/" + command;

        return command.Length <= 1 ? null : command;
    }
}
