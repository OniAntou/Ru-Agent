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
            return Fail("missing_subcommand", "Missing session subcommand. Expected list|rewind|delete.");

        var sub = args[startIndex];
        return sub switch
        {
            "list" => ParseSessionList(args, startIndex + 1),
            "rewind" => ParseSessionRewind(args, startIndex + 1),
            "delete" => ParseSessionDelete(args, startIndex + 1),
            "export" => ParseSessionExport(args, startIndex + 1),
            "import" => ParseSessionImport(args, startIndex + 1),
            _ => Fail("unknown_subcommand", $"Unknown session subcommand '{sub}'. Expected list|rewind|delete|export|import.")
        };
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
            return Fail("unknown_switch", $"Unknown switch '{token}' for session list.");
        }
        return Success(new SessionListCommand(json));
    }

    private static AgentCliParseResult ParseSessionExport(string[] args, int startIndex)
    {
        string? sessionId = null;
        string? harness = null;
        string? outputDirectory = null;
        bool json = false;

        for (var index = startIndex; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--json":
                    json = true;
                    continue;
                case "--harness":
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --harness.");
                    harness = args[index];
                    continue;
                case "--out":
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --out.");
                    outputDirectory = args[index];
                    continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session export.");
            if (sessionId is not null)
                return Fail("unexpected_argument", "session export takes one <session-id>.");
            sessionId = token;
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session export.");
        if (string.IsNullOrWhiteSpace(harness))
            return Fail("missing_harness", "Missing required --harness for session export.");

        return Success(new SessionExportCommand(sessionId, harness, outputDirectory, json));
    }

    private static AgentCliParseResult ParseSessionImport(string[] args, int startIndex)
    {
        string? filePath = null;
        string? harness = null;
        string? sessionId = null;
        bool json = false;

        for (var index = startIndex; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--json":
                    json = true;
                    continue;
                case "--harness":
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --harness.");
                    harness = args[index];
                    continue;
                case "--session":
                    if (++index >= args.Length)
                        return Fail("missing_value", "Missing value for --session.");
                    sessionId = args[index];
                    continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session import.");
            if (filePath is not null)
                return Fail("unexpected_argument", "session import takes one <file>.");
            filePath = token;
        }

        if (filePath is null)
            return Fail("missing_file", "Missing required <file> for session import.");

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
