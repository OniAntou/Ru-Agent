namespace RuAgent.Cli;

public static partial class AgentCliParser
{
    private static AgentCliParseResult ParseRpc(string[] args)
    {
        // agent rpc — the process carries state on stdin/stdout, so no other arguments exist.
        if (args.Length != 2)
        {
            return Fail("unknown_switch", "agent rpc accepts no additional arguments.");
        }

        return Success(new RpcCommand());
    }

    private static AgentCliParseResult ParseSession(string[] args, int startIndex)
    {
        if (args.Length <= startIndex)
            return Fail("missing_subcommand", "Missing session subcommand. Expected list|history|search|rewind|delete.");

        var sub = args[startIndex];
        return sub switch
        {
            "list" => ParseSessionList(args, startIndex + 1),
            "history" => ParseSessionHistory(args, startIndex + 1),
            "search" => ParseSessionSearch(args, startIndex + 1),
            "rewind" => ParseSessionRewind(args, startIndex + 1),
            "delete" => ParseSessionDelete(args, startIndex + 1),
            "export" => ParseSessionExport(args, startIndex + 1),
            "import" => ParseSessionImport(args, startIndex + 1),
            _ => Fail("unknown_subcommand", $"Unknown session subcommand '{sub}'. Expected list|history|search|rewind|delete|export|import.")
        };
    }

    private static AgentCliParseResult ParseSessionHistory(string[] args, int startIndex)
    {
        string? sessionId = null;
        string? cursor = null;
        var limit = 200;
        var json = false;
        var cursorSeen = false;
        var limitSeen = false;
        var jsonSeen = false;
        for (var i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--json":
                    if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                    json = true;
                    jsonSeen = true;
                    continue;
                case "--cursor":
                    if (cursorSeen) return Fail("duplicate_switch", "Duplicate --cursor.");
                    if (++i >= args.Length) return Fail("missing_value", "Missing value for --cursor.");
                    cursor = args[i];
                    cursorSeen = true;
                    if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 || cursor.Any(char.IsControl))
                        return Fail("invalid_cursor", "History cursor must be 1..2048 printable characters.");
                    continue;
                case "--limit":
                    if (limitSeen) return Fail("duplicate_switch", "Duplicate --limit.");
                    if (++i >= args.Length || !int.TryParse(args[i], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out limit)
                        || limit is < 1 or > 200)
                        return Fail("invalid_limit", "--limit must be an integer from 1 to 200.");
                    limitSeen = true;
                    continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session history.");
            if (sessionId is not null)
                return Fail("too_many_arguments", "session history takes one <session-id>.");
            if (!SessionIdLooseRegex.IsMatch(token))
                return Fail("invalid_session_id", "Session id contains invalid characters.");
            sessionId = token;
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session history.");
        return Success(new SessionHistoryCommand(sessionId, cursor, limit, json));
    }

    private static AgentCliParseResult ParseSessionDelete(string[] args, int startIndex)
    {
        string? sessionId = null;
        bool yes = false;
        bool yesSeen = false;

        for (var i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--yes")
            {
                if (yesSeen) return Fail("duplicate_switch", "Duplicate --yes.");
                yes = true;
                yesSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session delete.");

            if (sessionId is not null)
                return Fail("too_many_arguments", $"Unexpected argument '{token}' for session delete.");
            if (!SessionIdLooseRegex.IsMatch(token))
                return Fail("invalid_session_id", "Session id contains invalid characters.");
            sessionId = token;
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session delete.");

        return Success(new SessionDeleteCommand(sessionId, yes));
    }

    private static AgentCliParseResult ParseSessionList(string[] args, int startIndex)
    {
        bool json = false;
        bool jsonSeen = false;
        bool cursorSeen = false;
        bool limitSeen = false;
        bool querySeen = false;
        string? cursor = null;
        string? query = null;
        var limit = 100;
        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--json":
                    if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                    json = true;
                    jsonSeen = true;
                    continue;
                case "--cursor":
                    if (cursorSeen) return Fail("duplicate_switch", "Duplicate --cursor.");
                    if (++i >= args.Length) return Fail("missing_value", "Missing value for --cursor.");
                    cursor = args[i];
                    cursorSeen = true;
                    if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 || cursor.Any(char.IsControl))
                        return Fail("invalid_cursor", "Session cursor must be 1..2048 printable characters.");
                    continue;
                case "--limit":
                    if (limitSeen) return Fail("duplicate_switch", "Duplicate --limit.");
                    if (++i >= args.Length || !int.TryParse(args[i], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out limit))
                        return Fail("invalid_limit", "--limit must be an integer from 1 to 200.");
                    limitSeen = true;
                    if (limit is < 1 or > 200) return Fail("invalid_limit", "--limit must be an integer from 1 to 200.");
                    continue;
                case "--query":
                    if (querySeen) return Fail("duplicate_switch", "Duplicate --query.");
                    if (++i >= args.Length) return Fail("missing_value", "Missing value for --query.");
                    query = args[i].Trim();
                    querySeen = true;
                    if (query.Length > 512 || query.Any(char.IsControl))
                        return Fail("invalid_query", "Session query must be at most 512 printable characters.");
                    if (query.Length == 0) query = null;
                    continue;
            }
            return Fail("unknown_switch", $"Unknown switch '{token}' for session list.");
        }
        return Success(new SessionListCommand(json, cursor, limit, query));
    }

    private static AgentCliParseResult ParseSessionSearch(string[] args, int startIndex)
    {
        var json = false;
        var jsonSeen = false;
        var limitSeen = false;
        var limit = 50;
        var queryParts = new List<string>();
        for (var i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--limit")
            {
                if (limitSeen) return Fail("duplicate_switch", "Duplicate --limit.");
                if (++i >= args.Length || !int.TryParse(args[i], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out limit)
                    || limit is < 1 or > 200)
                    return Fail("invalid_limit", "--limit must be an integer from 1 to 200.");
                limitSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session search.");
            queryParts.Add(token);
        }

        var query = string.Join(" ", queryParts).Trim();
        if (query.Length == 0) return Fail("missing_query", "Missing required <query> for session search.");
        if (query.Length > 512 || query.Any(char.IsControl))
            return Fail("invalid_query", "Session query must be at most 512 printable characters.");
        return Success(new SessionSearchCommand(query, limit, json));
    }

    private static AgentCliParseResult ParseSessionExport(string[] args, int startIndex)
    {
        string? sessionId = null;
        string? harness = null;
        string? outputDirectory = null;
        bool json = false;
        bool jsonSeen = false;
        bool harnessSeen = false;
        bool outSeen = false;

        for (var index = startIndex; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--json":
                    if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                    json = true;
                    jsonSeen = true;
                    continue;
                case "--harness":
                    if (harnessSeen) return Fail("duplicate_switch", "Duplicate --harness.");
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --harness.");
                    harness = args[index];
                    harnessSeen = true;
                    continue;
                case "--out":
                    if (outSeen) return Fail("duplicate_switch", "Duplicate --out.");
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --out.");
                    outputDirectory = args[index];
                    outSeen = true;
                    continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session export.");
            if (sessionId is not null)
                return Fail("unexpected_argument", "session export takes one <session-id>.");
            if (!SessionIdLooseRegex.IsMatch(token))
                return Fail("invalid_session_id", "Session id contains invalid characters.");
            sessionId = token;
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session export.");
        if (string.IsNullOrWhiteSpace(harness))
            return Fail("missing_harness", "Missing required --harness for session export.");
        if (harness.Length > MaxHarnessLength)
            return Fail("invalid_harness", $"Harness name must be 1..{MaxHarnessLength} characters.");
        if (outputDirectory is { } directory
            && (directory.Length == 0 || directory.Length > MaxFilePathLength || directory.Contains('\0')))
            return Fail("invalid_out", "Output directory path is not a bounded path.");

        return Success(new SessionExportCommand(sessionId, harness, outputDirectory, json));
    }

    private static AgentCliParseResult ParseSessionImport(string[] args, int startIndex)
    {
        string? filePath = null;
        string? harness = null;
        string? sessionId = null;
        bool json = false;
        bool jsonSeen = false;
        bool harnessSeen = false;
        bool sessionSeen = false;

        for (var index = startIndex; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--json":
                    if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                    json = true;
                    jsonSeen = true;
                    continue;
                case "--harness":
                    if (harnessSeen) return Fail("duplicate_switch", "Duplicate --harness.");
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --harness.");
                    harness = args[index];
                    harnessSeen = true;
                    continue;
                case "--session":
                    if (sessionSeen) return Fail("duplicate_switch", "Duplicate --session.");
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --session.");
                    sessionId = args[index];
                    sessionSeen = true;
                    continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session import.");
            if (filePath is not null)
                return Fail("unexpected_argument", "session import takes one <file>.");
            if (token.Length == 0 || token.Length > MaxFilePathLength || token.Contains('\0'))
                return Fail("invalid_file_path", "File path is not a bounded path.");
            filePath = token;
        }

        if (filePath is null)
            return Fail("missing_file", "Missing required <file> for session import.");
        if (harness is not null && (harness.Length == 0 || harness.Length > MaxHarnessLength))
            return Fail("invalid_harness", $"Harness name must be 1..{MaxHarnessLength} characters.");
        if (sessionId is not null && !SessionIdLooseRegex.IsMatch(sessionId))
            return Fail("invalid_session_id", "Session id contains invalid characters.");

        return Success(new SessionImportCommand(filePath, harness, sessionId, json));
    }

    private static AgentCliParseResult ParseSessionRewind(string[] args, int startIndex)
    {
        string? sessionId = null;
        int toIndex = -1;
        bool json = false;
        bool jsonSeen = false;
        bool indexSeen = false;

        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--index")
            {
                if (indexSeen) return Fail("duplicate_switch", "Duplicate --index.");
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out toIndex))
                    return Fail("missing_value", "Missing integer value for --index.");
                indexSeen = true;
                i++;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session rewind.");

            if (sessionId is null)
            {
                if (!SessionIdLooseRegex.IsMatch(token))
                    return Fail("invalid_session_id", "Session id contains invalid characters.");
                sessionId = token;
                continue;
            }
            return Fail("too_many_arguments", $"Unexpected argument '{token}' for session rewind.");
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session rewind.");
        if (!indexSeen)
            return Fail("missing_index", "Missing required --index for session rewind.");

        return Success(new SessionRewindCommand(sessionId, toIndex, json));
    }
}
