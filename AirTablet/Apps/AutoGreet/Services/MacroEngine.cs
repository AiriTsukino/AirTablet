using System.Globalization;
using System.Text.RegularExpressions;
using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class MacroSyntaxException : Exception
{
    public MacroSyntaxException(string message) : base(message)
    {
    }
}

public sealed record MacroExecutionResult(bool HasEmote, bool QueuedTargetedEmote, bool TargetedPlayer);

public sealed class MacroEngine
{
    private static readonly TimeSpan TargetedEmoteTargetHold = TimeSpan.FromSeconds(1.75);
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MacroTokenRegex = new(@"<(?<token>[^<>]+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Configuration config;
    private readonly GreetingService greetings;
    private readonly ChatCommandService chatCommands;
    private readonly TargetingService targeting;
    private readonly PendingEmoteQueueService pendingEmotes;
    private readonly DiagnosticLogService logs;

    public MacroEngine(Configuration config, GreetingService greetings, ChatCommandService chatCommands, TargetingService targeting, PendingEmoteQueueService pendingEmotes, DiagnosticLogService logs)
    {
        this.config = config;
        this.greetings = greetings;
        this.chatCommands = chatCommands;
        this.targeting = targeting;
        this.pendingEmotes = pendingEmotes;
        this.logs = logs;
    }

    public async Task<MacroExecutionResult> ExecuteAsync(VisitorKey target, GreetingMacro macro, Func<bool> stillPresent, CancellationToken token, bool markVisitorGreeted = true)
    {
        var parsedLines = ValidateAndParse(macro);
        var hasEmote = parsedLines.Any(p => p.Kind == MacroLineKind.Emote);
        var queuedTargetedEmote = false;
        var targetedPlayer = false;
        DateTimeOffset? lastTargetedEmoteSentUtc = null;

        logs.Info("Macro execution started", $"Running macro '{macro.Name}' for {target.Display}. Parsed lines: {parsedLines.Count}.");

        try
        {
            foreach (var parsed in parsedLines)
            {
                token.ThrowIfCancellationRequested();

                if (!stillPresent())
                {
                    logs.Warning("Macro target not present", $"Macro '{macro.Name}' stopped before sending the next command because {target.Display} was no longer present in the required detection area.");
                    throw new OperationCanceledException("Target left before greeting completed.", token);
                }

                if (parsed.WaitOnlySeconds is not null)
                {
                    await DelaySecondsAsync(parsed.WaitOnlySeconds.Value, token).ConfigureAwait(false);
                    continue;
                }

                var command = BuildCommand(parsed.Line, target, macro.Name, out var tellText);
                if (tellText is not null)
                    greetings.RegisterExpectedTell(target, tellText, markVisitorGreeted);

                if (parsed.Kind == MacroLineKind.Emote && EmoteCommandRegistry.RequiresVisibleTarget(parsed.Line))
                {
                    if (config.WaitForVisibleTargetBeforeEmote)
                    {
                        pendingEmotes.Enqueue(target, command, macro.Name);
                        queuedTargetedEmote = true;

                        if (parsed.InlineWaitSeconds is not null)
                            await DelaySecondsAsync(parsed.InlineWaitSeconds.Value, token).ConfigureAwait(false);
                        else
                            await Task.Delay(250, token).ConfigureAwait(false);

                        continue;
                    }

                    var targetedForEmote = await targeting.TargetAndVerifyAsync(target, token).ConfigureAwait(false);
                    if (!targetedForEmote)
                        throw new InvalidOperationException($"Could not target visible player {target.Display} for emote command: {parsed.Line}");

                    targetedPlayer = true;
                    await Task.Delay(300, token).ConfigureAwait(false);
                }

                logs.Info("Sending macro command", $"Macro '{macro.Name}' sending command for {target.Display}: {command}");
                await SendCommandAsync(command, token).ConfigureAwait(false);
                if (parsed.Kind == MacroLineKind.Emote && EmoteCommandRegistry.RequiresVisibleTarget(parsed.Line))
                    lastTargetedEmoteSentUtc = DateTimeOffset.UtcNow;

                logs.Info("Macro command sent", $"Macro '{macro.Name}' sent command for {target.Display}: {command}");
                if (tellText is not null)
                    greetings.ConfirmTellCommandSent(target, tellText, markVisitorGreeted);

                if (parsed.InlineWaitSeconds is not null)
                    await DelaySecondsAsync(parsed.InlineWaitSeconds.Value, token).ConfigureAwait(false);
                else
                    await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (targetedPlayer && config.UntargetAfterGreeting)
            {
                await HoldTargetAfterTargetedEmoteAsync(lastTargetedEmoteSentUtc, CancellationToken.None).ConfigureAwait(false);
                await targeting.ClearTargetAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new MacroExecutionResult(hasEmote, queuedTargetedEmote, targetedPlayer);
    }

    public bool MacroHasEmote(GreetingMacro macro)
        => ValidateAndParse(macro).Any(p => p.Kind == MacroLineKind.Emote);

    public IReadOnlyList<MacroSyntaxIssue> Validate(GreetingMacro macro)
    {
        var issues = new List<MacroSyntaxIssue>();
        var lineNumber = 0;
        var hasContent = false;

        foreach (var raw in macro.Script.Replace("\r", string.Empty).Split('\n'))
        {
            lineNumber++;
            var original = raw.Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;
            hasContent = true;

            var preparationIssue = GetPreparationIssue(original);
            if (preparationIssue is not null)
            {
                issues.Add(new MacroSyntaxIssue(lineNumber, original, preparationIssue));
                continue;
            }

            var line = original;
            var inlineWaitSeconds = ExtractInlineWaitSeconds(ref line);
            if (inlineWaitSeconds is null && original.Contains("<wait.", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new MacroSyntaxIssue(lineNumber, original, "Inline wait was not recognized. Use <wait.1>, <wait.2>, or <wait.02>."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (TryParseWaitCommand(line, out _))
                continue;

            var placeholderIssue = GetPlaceholderIssue(line);
            if (placeholderIssue is not null)
            {
                issues.Add(new MacroSyntaxIssue(lineNumber, original, placeholderIssue));
                continue;
            }

            string? chatIssue = null;
            if (IsTellToTarget(line) || IsSupportedChatLine(line, out chatIssue) || EmoteCommandRegistry.IsSupportedEmoteLine(line))
            {
                if (chatIssue is not null)
                    issues.Add(new MacroSyntaxIssue(lineNumber, original, chatIssue));
                continue;
            }

            issues.Add(new MacroSyntaxIssue(lineNumber, original, GetUnsupportedLineSuggestion(line)));
        }

        if (!hasContent)
            issues.Add(new MacroSyntaxIssue(0, string.Empty, "Add at least one supported command to the macro script."));

        return issues;
    }

    private List<ParsedMacroLine> ValidateAndParse(GreetingMacro macro)
    {
        var parsed = new List<ParsedMacroLine>();
        var lineNumber = 0;

        foreach (var raw in macro.Script.Replace("\r", string.Empty).Split('\n'))
        {
            lineNumber++;
            var original = raw.Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            var preparationIssue = GetPreparationIssue(original);
            if (preparationIssue is not null)
                ThrowSyntax(macro, lineNumber, original, preparationIssue);

            var line = original;
            var inlineWaitSeconds = ExtractInlineWaitSeconds(ref line);
            if (inlineWaitSeconds is null && original.Contains("<wait.", StringComparison.OrdinalIgnoreCase))
                ThrowSyntax(macro, lineNumber, original, "Inline wait was not recognized. Use <wait.1>, <wait.2>, or <wait.02>.");

            if (string.IsNullOrWhiteSpace(line))
            {
                if (inlineWaitSeconds is not null)
                    parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, inlineWaitSeconds.Value, null));
                continue;
            }

            if (TryParseWaitCommand(line, out var waitSeconds))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, waitSeconds, null));
                if (inlineWaitSeconds is not null)
                    parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, inlineWaitSeconds.Value, null));
                continue;
            }

            var placeholderIssue = GetPlaceholderIssue(line);
            if (placeholderIssue is not null)
                ThrowSyntax(macro, lineNumber, original, placeholderIssue);

            if (IsTellToTarget(line))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Tell, line, null, inlineWaitSeconds));
                continue;
            }

            if (IsSupportedChatLine(line, out var chatIssue))
            {
                if (chatIssue is not null)
                    ThrowSyntax(macro, lineNumber, original, chatIssue);

                parsed.Add(new ParsedMacroLine(MacroLineKind.Chat, line, null, inlineWaitSeconds));
                continue;
            }

            if (EmoteCommandRegistry.IsSupportedEmoteLine(line))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Emote, line, null, inlineWaitSeconds));
                continue;
            }

            ThrowSyntax(macro, lineNumber, original, GetUnsupportedLineSuggestion(line));
        }

        if (parsed.Count == 0)
            ThrowSyntax(macro, 0, string.Empty, "Add at least one supported command to the macro script.");

        return parsed;
    }

    private void ThrowSyntax(GreetingMacro macro, int lineNumber, string line, string message)
    {
        logs.MacroSyntaxError(macro.Name, lineNumber, line, message + "\n\n" + DiagnosticLogService.SupportedSyntaxText);
        throw new MacroSyntaxException($"Macro syntax error in '{macro.Name}', line {lineNumber}.");
    }

    private async Task SendCommandAsync(string command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        greetings.LastCommandText = command;
        var sent = await chatCommands.SendAsync(command, token).ConfigureAwait(false);
        if (!sent)
            throw new InvalidOperationException($"Chat command was not sent: {chatCommands.LastError}");
    }

    private string BuildCommand(string line, VisitorKey target, string macroName, out string? tellText)
    {
        tellText = null;
        var tellTarget = BuildPlayerNameToken(target);

        if (TryGetTellText(line, out tellText))
        {
            var originalTellText = tellText ?? string.Empty;
            tellText = ReplaceMacroVariables(originalTellText, target);
            if (line.Contains("<playername>", StringComparison.OrdinalIgnoreCase) || originalTellText.Contains("<playername>", StringComparison.OrdinalIgnoreCase))
                logs.Info("Macro variable replaced", $"Macro '{macroName}' resolved <playername> to '{tellTarget}' for {target.Display}.");

            var command = $"/tell {tellTarget} {tellText}";
            logs.Info("Tell target resolved", $"Macro '{macroName}' will send tell to '{tellTarget}' for {target.Display}.");
            return command;
        }

        if (IsDote(line))
            return "/dote <t>";

        var commandLine = ReplaceMacroVariables(line, target);
        if (line.Contains("<playername>", StringComparison.OrdinalIgnoreCase))
            logs.Info("Macro variable replaced", $"Macro '{macroName}' resolved <playername> to '{tellTarget}' for {target.Display}.");

        return commandLine;
    }

    private static string BuildPlayerNameToken(VisitorKey target)
    {
        var name = NormalizePlayerName(target.Name, target.World);
        var world = NormalizeWorldName(target.World);
        return string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}";
    }

    private static string NormalizePlayerName(string name, string world)
    {
        var normalized = name.Trim();
        var normalizedWorld = NormalizeWorldName(world);

        if (!string.IsNullOrWhiteSpace(normalizedWorld))
        {
            var parenthesizedWorld = $"({normalizedWorld})";
            if (normalized.EndsWith(parenthesizedWorld, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^parenthesizedWorld.Length].TrimEnd();
        }

        var atIndex = normalized.LastIndexOf('@');
        if (atIndex > 0)
            normalized = normalized[..atIndex].TrimEnd();

        return normalized;
    }

    private static string NormalizeWorldName(string world)
    {
        var normalized = world.Trim();
        if (normalized.StartsWith("(", StringComparison.Ordinal) && normalized.EndsWith(")", StringComparison.Ordinal) && normalized.Length > 2)
            normalized = normalized[1..^1].Trim();

        var atIndex = normalized.LastIndexOf('@');
        if (atIndex >= 0 && atIndex < normalized.Length - 1)
            normalized = normalized[(atIndex + 1)..].Trim();

        return normalized.Trim('(', ')', ' ');
    }

    private static string ReplaceMacroVariables(string line, VisitorKey target)
        => line.Replace("<playername>", BuildPlayerNameToken(target), StringComparison.OrdinalIgnoreCase);

    private static bool IsTellToTarget(string line) => TryGetTellText(line, out _);

    private static bool TryGetTellText(string line, out string? tellText)
    {
        tellText = null;

        if (TryReadLine(line, "/tell <t>", 9, out tellText))
            return true;

        if (TryReadLine(line, "/tell <playername>", 18, out tellText))
            return true;

        if (TryReadLine(line, "/t <t>", 6, out tellText))
            return true;

        if (TryReadLine(line, "/t <playername>", 15, out tellText))
            return true;

        return false;
    }

    private static bool IsSupportedChatLine(string line, out string? issue)
    {
        issue = null;
        var supported = TryReadLine(line, "/say", 4, out _)
                        || TryReadLine(line, "/s", 2, out _)
                        || TryReadLine(line, "/shout", 6, out _)
                        || TryReadLine(line, "/sh", 3, out _)
                        || TryReadLine(line, "/yell", 5, out _)
                        || TryReadLine(line, "/y", 2, out _);

        if (!supported)
            return false;

        if (line.Contains("<t>", StringComparison.OrdinalIgnoreCase))
            issue = "Use <playername> instead of <t> in /say, /shout or /yell lines in the macro script. AutoGreet does not target players for non-tell chat channels.";

        return true;
    }

    private static string? GetPreparationIssue(string line)
    {
        var inlineWaits = InlineWaitRegex.Matches(line);
        if (inlineWaits.Count > 1)
            return "Use only one inline wait on each macro line.";

        foreach (Match match in inlineWaits)
        {
            if (double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 10)
            {
                return "Wait values must be between 0 and 10 seconds.";
            }
        }

        if (line.Contains("<wait", StringComparison.OrdinalIgnoreCase) && inlineWaits.Count == 0)
            return "Inline wait was not recognized. Use <wait.1>, <wait.2>, or <wait.02>.";

        if (line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line[5..].TrimStart('.', ' ');
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 1)
                return "Use only one numeric value after /wait, between 0 and 10 seconds.";
            if (tokens.Length == 1 &&
                double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var waitSeconds) &&
                (waitSeconds < 0 || waitSeconds > 10))
            {
                return "Wait values must be between 0 and 10 seconds.";
            }
        }

        return null;
    }

    private static string? GetPlaceholderIssue(string line)
    {
        foreach (Match match in MacroTokenRegex.Matches(line))
        {
            var token = match.Groups["token"].Value.Trim();
            if (token.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("playername", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return $"The <{token}> placeholder is not supported. Use <t> for a target or <playername> for the visitor's Name@World.";
        }

        if (EmoteCommandRegistry.IsSupportedEmoteLine(line) &&
            line.Contains("<playername>", StringComparison.OrdinalIgnoreCase))
        {
            return "Use <t> instead of <playername> on targeted emote lines. AutoGreet targets the visitor before sending the emote.";
        }
        return null;
    }

    private static string GetUnsupportedLineSuggestion(string line)
    {
        if (!line.StartsWith("/", StringComparison.Ordinal))
            return "Start the line with a supported slash command, such as /tell, /say, /shout, /yell, /wait, or a supported emote.";

        if (StartsWithCommand(line, "/tell") || StartsWithCommand(line, "/t"))
            return "Tell lines must include a target and message. Use /tell <t> message or /tell <playername> message.";

        if (StartsWithCommand(line, "/say") || StartsWithCommand(line, "/s") ||
            StartsWithCommand(line, "/shout") || StartsWithCommand(line, "/sh") ||
            StartsWithCommand(line, "/yell") || StartsWithCommand(line, "/y"))
        {
            return "Add message text after the chat command.";
        }

        if (line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase))
            return "Wait syntax was not recognized. Use /wait 1, /wait.1, /wait1, or an inline wait such as <wait.02>.";

        if (EmoteCommandRegistry.TryGetCommandName(line, out var command))
            return $"The /{command} command is not supported by AutoGreet. Check Settings > Help for the supported emote list.";

        return "This line does not match AutoGreet's supported macro syntax.";
    }

    private static bool StartsWithCommand(string line, string command) =>
        line.StartsWith(command, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == command.Length || char.IsWhiteSpace(line[command.Length]));

    private static bool TryReadLine(string line, string prefix, int messageStartIndex, out string? message)
    {
        message = null;
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (line.Length > prefix.Length && !char.IsWhiteSpace(line[prefix.Length]))
            return false;

        message = line.Length > messageStartIndex ? line[messageStartIndex..].Trim() : string.Empty;
        return !string.IsNullOrWhiteSpace(message);
    }

    private static bool IsDote(string line)
        => line.Equals("/dote", StringComparison.OrdinalIgnoreCase)
           || line.Equals("/dote <t>", StringComparison.OrdinalIgnoreCase);

    private static double? ExtractInlineWaitSeconds(ref string line)
    {
        var matches = InlineWaitRegex.Matches(line);
        if (matches.Count == 0) return null;

        double? seconds = null;
        foreach (Match match in matches)
        {
            if (double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                seconds = Math.Clamp(parsed, 0, 10);
        }

        line = InlineWaitRegex.Replace(line, string.Empty).Trim();
        return seconds;
    }

    private static bool TryParseWaitCommand(string line, out double seconds)
    {
        seconds = 1;

        if (!line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = line[5..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != '.' && !char.IsDigit(rest[0]))
            return false;

        rest = rest.Trim();
        if (rest.StartsWith(".", StringComparison.Ordinal))
            rest = rest[1..].Trim();

        if (string.IsNullOrWhiteSpace(rest))
            return true;

        var firstToken = rest.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return false;

        seconds = Math.Clamp(parsed, 0, 10);
        return true;
    }

    private static async Task HoldTargetAfterTargetedEmoteAsync(DateTimeOffset? sentUtc, CancellationToken token)
    {
        if (sentUtc is null)
        {
            await Task.Delay(TargetedEmoteTargetHold, token).ConfigureAwait(false);
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - sentUtc.Value;
        var remaining = TargetedEmoteTargetHold - elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, token).ConfigureAwait(false);
    }

    private static Task DelaySecondsAsync(double seconds, CancellationToken token)
        => Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10)), token);

    private enum MacroLineKind
    {
        Tell,
        Chat,
        Emote,
        Wait,
    }

    private sealed record ParsedMacroLine(MacroLineKind Kind, string Line, double? WaitOnlySeconds, double? InlineWaitSeconds);
}

public sealed record MacroSyntaxIssue(int LineNumber, string LineText, string Message);
