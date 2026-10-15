namespace SSMSPlus.Features.Blocking
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>One session involved in blocking (blocked, blocking, or idle with an open transaction).</summary>
    public sealed class BlockingSession
    {
        public int SessionId { get; set; }
        public int BlockingSessionId { get; set; }
        public string LoginName { get; set; }
        public string HostName { get; set; }
        public string ProgramName { get; set; }
        public string DatabaseName { get; set; }
        public string SessionStatus { get; set; }
        public string RequestStatus { get; set; }
        public string Command { get; set; }
        public string WaitType { get; set; }
        public long WaitTimeMs { get; set; }
        public string WaitResource { get; set; }
        public DateTime? RequestStart { get; set; }
        public DateTime? LastRequestStart { get; set; }
        public DateTime? LastRequestEnd { get; set; }
        public int OpenTransactionCount { get; set; }
        public DateTime? TransactionBegin { get; set; }
        public string CurrentStatement { get; set; }
        public string BatchText { get; set; }
        public bool IsMe { get; set; }
        public bool IsUserProcess { get; set; }
        public long CpuTimeMs { get; set; }
        public long LogicalReads { get; set; }
        public byte[] PlanHandle { get; set; }
        public int StatementStart { get; set; }
        public int StatementEnd { get; set; }
        public DateTime ServerTime { get; set; }

        // ---- tree
        public BlockingSession Parent { get; set; }
        public List<BlockingSession> Blocked { get; } = new List<BlockingSession>();
        public int Level { get; set; }

        public bool HasRequest => !string.IsNullOrEmpty(RequestStatus);
        public bool IsBlocked => BlockingSessionId > 0;
        public bool IsHeadBlocker => !IsBlocked && Blocked.Count > 0;
        public bool IsIdleWithOpenTransaction => !HasRequest && OpenTransactionCount > 0;

        /// <summary>Number of sessions waiting on this one, directly or through others.</summary>
        public int TotalBlocked => Blocked.Sum(b => 1 + b.TotalBlocked);

        public TimeSpan? TransactionAge => TransactionBegin.HasValue ? ServerTime - TransactionBegin.Value : (TimeSpan?)null;

        public TimeSpan? IdleFor => !HasRequest && LastRequestEnd.HasValue ? ServerTime - LastRequestEnd.Value : (TimeSpan?)null;

        /// <summary>Last batch as the client sent it (sys.dm_exec_input_buffer, SQL Server 2016+).</summary>
        public string InputBufferText { get; set; }

        /// <summary>"Language Event" (ad hoc text, with the literal values) or "RPC Event" (sp_executesql, procedure call).</summary>
        public string InputBufferType { get; set; }

        /// <summary>
        /// The batch text starts with a parameter declaration such as "(@1 int)" or "(@P1 nvarchar(50))":
        /// SQL Server auto-parameterized the query, or the client used sp_executesql. Such text cannot be run as is.
        /// </summary>
        public bool IsParameterized => ParameterizedSql.TryParse(BatchText, out _, out _);

        /// <summary>SQL to show and copy: the text the client sent when available, otherwise the statement.</summary>
        public string Sql
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(InputBufferText) && string.Equals(InputBufferType, "Language Event", StringComparison.OrdinalIgnoreCase))
                    return InputBufferText;
                return !string.IsNullOrWhiteSpace(CurrentStatement) ? CurrentStatement : BatchText;
            }
        }

        /// <summary>True when <see cref="Sql"/> uses parameters that are not declared in it.</summary>
        public bool SqlNeedsParameters
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(InputBufferText) && string.Equals(InputBufferType, "Language Event", StringComparison.OrdinalIgnoreCase))
                    return false;
                return IsParameterized;
            }
        }

        public string SqlPreview
        {
            get
            {
                var t = (Sql ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                while (t.Contains("  "))
                    t = t.Replace("  ", " ");
                return t.Length > 160 ? t.Substring(0, 160) + "..." : t;
            }
        }

        public string Role
        {
            get
            {
                if (IsHeadBlocker)
                    return IsIdleWithOpenTransaction ? "Head blocker (idle, open transaction)" : "Head blocker";
                if (IsBlocked && Blocked.Count > 0)
                    return "Blocked and blocking";
                if (IsBlocked)
                    return "Blocked";
                if (IsIdleWithOpenTransaction)
                    return "Idle with open transaction";
                return string.Empty;
            }
        }

        public string SessionLabel
        {
            get
            {
                string icon = IsHeadBlocker ? "⛔ " : IsBlocked ? "⏳ " : IsIdleWithOpenTransaction ? "💤 " : string.Empty;
                string indent = new string(' ', Level * 4) + (Level > 0 ? "└ " : string.Empty);
                return indent + icon + SessionId.ToString(CultureInfo.InvariantCulture) + (IsMe ? " (you)" : string.Empty);
            }
        }

        public string StatusText
        {
            get
            {
                if (HasRequest)
                    return RequestStatus + (string.IsNullOrEmpty(Command) ? string.Empty : " · " + Command);
                return SessionStatus;
            }
        }

        public string WaitText => WaitTimeMs > 0 ? BlockingFormat.Duration(TimeSpan.FromMilliseconds(WaitTimeMs)) : string.Empty;

        public string TransactionText
        {
            get
            {
                if (OpenTransactionCount <= 0)
                    return string.Empty;
                var age = TransactionAge;
                return OpenTransactionCount.ToString(CultureInfo.InvariantCulture) + (age.HasValue ? " · " + BlockingFormat.Duration(age.Value) : string.Empty);
            }
        }

        public string BlockingCountText => Blocked.Count == 0 ? string.Empty : TotalBlocked.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class BlockingSnapshot
    {
        public List<BlockingSession> Roots { get; } = new List<BlockingSession>();
        public List<BlockingSession> Flat { get; } = new List<BlockingSession>();
        public DateTime ServerTime { get; set; }
        public string ServerName { get; set; }
        public string ProductVersion { get; set; }
        public bool CanSeeAllSessions { get; set; }
        public bool IsAzureSqlDatabase { get; set; }
        public int BlockedCount => Flat.Count(s => s.IsBlocked);
        public long LongestWaitMs => Flat.Where(s => s.IsBlocked).Select(s => s.WaitTimeMs).DefaultIfEmpty(0).Max();
    }

    internal static class BlockingFormat
    {
        public static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
                t = TimeSpan.Zero;
            if (t.TotalSeconds < 1)
                return t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
            if (t.TotalMinutes < 1)
                return t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (t.TotalHours < 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s", (int)t.TotalMinutes, t.Seconds);
            if (t.TotalDays < 1)
                return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", (int)t.TotalHours, t.Minutes);
            return string.Format(CultureInfo.InvariantCulture, "{0}d {1}h", (int)t.TotalDays, t.Hours);
        }
    }

    /// <summary>Reads the current blocking situation from the DMVs (read-only, no objects created on the server).</summary>
    internal static class BlockingReader
    {
        private const string InfoQuery = @"
SELECT
    CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS server_name,
    CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS product_version,
    CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
    ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'), 0) AS view_server_state,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE'), 0) AS view_database_state,
    SYSDATETIME() AS server_time;";

        // Sessions that are blocked, the sessions blocking them, and (optionally) idle sessions with an open
        // transaction. Statement text comes from the running request; for idle sessions the last batch from
        // sys.dm_exec_connections.most_recent_sql_handle is used.
        private const string SessionsQuery = @"
WITH req AS (
    SELECT r.session_id, r.request_id, r.blocking_session_id, r.status, r.command, r.wait_type, r.wait_time, r.wait_resource,
           r.start_time, r.database_id, r.sql_handle, r.plan_handle, r.statement_start_offset, r.statement_end_offset,
           r.cpu_time, r.logical_reads
    FROM sys.dm_exec_requests AS r
),
involved AS (
    SELECT session_id FROM req WHERE blocking_session_id > 0
    UNION
    SELECT blocking_session_id FROM req WHERE blocking_session_id > 0
    UNION
    SELECT s.session_id FROM sys.dm_exec_sessions AS s
    WHERE @include_idle = 1 AND s.is_user_process = 1 AND s.open_transaction_count > 0
      AND NOT EXISTS (SELECT 1 FROM req WHERE req.session_id = s.session_id)
)
SELECT
    s.session_id,
    ISNULL(r.blocking_session_id, 0) AS blocking_session_id,
    s.login_name, s.host_name, s.program_name,
    DB_NAME(COALESCE(r.database_id, s.database_id)) AS database_name,
    s.status AS session_status,
    r.status AS request_status,
    r.command,
    r.wait_type,
    CAST(ISNULL(r.wait_time, 0) AS bigint) AS wait_time_ms,
    r.wait_resource,
    r.start_time,
    s.last_request_start_time,
    s.last_request_end_time,
    s.open_transaction_count,
    tx.transaction_begin_time,
    CASE WHEN r.sql_handle IS NOT NULL AND st.text IS NOT NULL THEN
        SUBSTRING(st.text, r.statement_start_offset / 2 + 1,
            (CASE WHEN r.statement_end_offset = -1 THEN DATALENGTH(st.text) ELSE r.statement_end_offset END
             - r.statement_start_offset) / 2 + 1)
    END AS current_statement,
    COALESCE(st.text, lt.text) AS batch_text,
    {INPUT_BUFFER_COLUMNS}
    CASE WHEN s.session_id = @@SPID THEN 1 ELSE 0 END AS is_me,
    s.is_user_process,
    CAST(ISNULL(r.cpu_time, 0) AS bigint) AS cpu_time,
    CAST(ISNULL(r.logical_reads, 0) AS bigint) AS logical_reads,
    r.plan_handle,
    ISNULL(r.statement_start_offset, 0) AS statement_start_offset,
    ISNULL(r.statement_end_offset, -1) AS statement_end_offset
FROM sys.dm_exec_sessions AS s
JOIN involved AS i ON i.session_id = s.session_id
LEFT JOIN req AS r ON r.session_id = s.session_id
OUTER APPLY (SELECT TOP (1) c.most_recent_sql_handle FROM sys.dm_exec_connections AS c
             WHERE c.session_id = s.session_id ORDER BY c.connect_time) AS c
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS lt
{INPUT_BUFFER_APPLY}
OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS transaction_begin_time
             FROM sys.dm_tran_session_transactions AS stx
             JOIN sys.dm_tran_active_transactions AS at ON at.transaction_id = stx.transaction_id
             WHERE stx.session_id = s.session_id) AS tx
ORDER BY s.session_id
OPTION (MAXDOP 1);";

        private const string LivePlanQuery = @"
SELECT CONVERT(nvarchar(max), query_plan) FROM sys.dm_exec_query_statistics_xml(@session_id);";

        private const string PlanQuery = @"
SELECT query_plan FROM sys.dm_exec_text_query_plan(@plan_handle, @start, @end);";

        public static async Task<BlockingSnapshot> ReadAsync(string connectionString, bool includeIdle, CancellationToken cancellationToken)
        {
            var snapshot = new BlockingSnapshot();
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new SqlCommand("SET LOCK_TIMEOUT 5000; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", connection))
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new SqlCommand(InfoQuery, connection) { CommandTimeout = 15 })
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        snapshot.ServerName = reader["server_name"] as string;
                        snapshot.ProductVersion = reader["product_version"] as string;
                        int edition = reader["engine_edition"] is int e ? e : 0;
                        snapshot.IsAzureSqlDatabase = edition == 5;
                        bool serverState = Convert.ToInt32(reader["view_server_state"], CultureInfo.InvariantCulture) == 1;
                        bool databaseState = Convert.ToInt32(reader["view_database_state"], CultureInfo.InvariantCulture) == 1;
                        snapshot.CanSeeAllSessions = snapshot.IsAzureSqlDatabase ? databaseState : serverState;
                        snapshot.ServerTime = (DateTime)reader["server_time"];
                    }
                }

                // SQL Server 2022+: VIEW SERVER PERFORMANCE STATE is enough to see all sessions.
                if (!snapshot.CanSeeAllSessions && !snapshot.IsAzureSqlDatabase && MajorVersion(snapshot.ProductVersion) >= 16)
                {
                    try
                    {
                        using (var command = new SqlCommand("SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE'), 0);", connection) { CommandTimeout = 15 })
                            snapshot.CanSeeAllSessions = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
                    }
                    catch (SqlException)
                    {
                    }
                }

                // sys.dm_exec_input_buffer: SQL Server 2016+ and Azure.
                bool inputBuffer = snapshot.IsAzureSqlDatabase || MajorVersion(snapshot.ProductVersion) >= 13;
                string sessionsQuery = SessionsQuery
                    .Replace("{INPUT_BUFFER_COLUMNS}", inputBuffer ? "ib.event_type AS input_buffer_type, ib.event_info AS input_buffer_text," : "CAST(NULL AS nvarchar(256)) AS input_buffer_type, CAST(NULL AS nvarchar(max)) AS input_buffer_text,")
                    .Replace("{INPUT_BUFFER_APPLY}", inputBuffer ? "OUTER APPLY sys.dm_exec_input_buffer(s.session_id, r.request_id) AS ib" : string.Empty);

                using (var command = new SqlCommand(sessionsQuery, connection) { CommandTimeout = 30 })
                {
                    command.Parameters.Add("@include_idle", SqlDbType.Bit).Value = includeIdle;
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            snapshot.Flat.Add(new BlockingSession
                            {
                                SessionId = Convert.ToInt32(reader["session_id"], CultureInfo.InvariantCulture),
                                BlockingSessionId = Convert.ToInt32(reader["blocking_session_id"], CultureInfo.InvariantCulture),
                                LoginName = reader["login_name"] as string,
                                HostName = reader["host_name"] as string,
                                ProgramName = reader["program_name"] as string,
                                DatabaseName = reader["database_name"] as string,
                                SessionStatus = reader["session_status"] as string,
                                RequestStatus = reader["request_status"] as string,
                                Command = reader["command"] as string,
                                WaitType = reader["wait_type"] as string,
                                WaitTimeMs = Convert.ToInt64(reader["wait_time_ms"], CultureInfo.InvariantCulture),
                                WaitResource = reader["wait_resource"] as string,
                                RequestStart = reader["start_time"] as DateTime?,
                                LastRequestStart = reader["last_request_start_time"] as DateTime?,
                                LastRequestEnd = reader["last_request_end_time"] as DateTime?,
                                OpenTransactionCount = Convert.ToInt32(reader["open_transaction_count"], CultureInfo.InvariantCulture),
                                TransactionBegin = reader["transaction_begin_time"] as DateTime?,
                                CurrentStatement = reader["current_statement"] as string,
                                BatchText = reader["batch_text"] as string,
                                InputBufferType = reader["input_buffer_type"] as string,
                                InputBufferText = reader["input_buffer_text"] as string,
                                IsMe = Convert.ToInt32(reader["is_me"], CultureInfo.InvariantCulture) == 1,
                                IsUserProcess = reader["is_user_process"] is bool u && u,
                                CpuTimeMs = Convert.ToInt64(reader["cpu_time"], CultureInfo.InvariantCulture),
                                LogicalReads = Convert.ToInt64(reader["logical_reads"], CultureInfo.InvariantCulture),
                                PlanHandle = reader["plan_handle"] as byte[],
                                StatementStart = Convert.ToInt32(reader["statement_start_offset"], CultureInfo.InvariantCulture),
                                StatementEnd = Convert.ToInt32(reader["statement_end_offset"], CultureInfo.InvariantCulture),
                                ServerTime = snapshot.ServerTime,
                            });
                        }
                    }
                }
            }

            BuildTree(snapshot);
            return snapshot;
        }

        private static int MajorVersion(string productVersion)
        {
            if (string.IsNullOrEmpty(productVersion))
                return 0;
            int dot = productVersion.IndexOf('.');
            int.TryParse(dot < 0 ? productVersion : productVersion.Substring(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out int major);
            return major;
        }

        /// <summary>Links every blocked session to its blocker and orders the list as a tree (head blockers first).</summary>
        internal static void BuildTree(BlockingSnapshot snapshot)
        {
            var byId = snapshot.Flat.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First());
            foreach (var session in byId.Values)
            {
                session.Blocked.Clear();
                session.Parent = null;
            }

            foreach (var session in byId.Values)
            {
                if (session.IsBlocked && session.BlockingSessionId != session.SessionId && byId.TryGetValue(session.BlockingSessionId, out var blocker))
                {
                    session.Parent = blocker;
                    blocker.Blocked.Add(session);
                }
            }

            // Roots: no parent. Sessions in a cycle (a deadlock being resolved) have parents all around:
            // break the cycle at the lowest session id so nothing disappears.
            var roots = byId.Values.Where(s => s.Parent == null).ToList();
            var reachable = new HashSet<int>();
            foreach (var r in roots)
                Mark(r, reachable);
            foreach (var s in byId.Values.OrderBy(s => s.SessionId))
            {
                if (reachable.Contains(s.SessionId))
                    continue;
                s.Parent?.Blocked.Remove(s);
                s.Parent = null;
                roots.Add(s);
                Mark(s, reachable);
            }

            snapshot.Roots.Clear();
            snapshot.Roots.AddRange(roots
                .OrderByDescending(r => r.TotalBlocked)
                .ThenByDescending(r => r.Blocked.Select(b => b.WaitTimeMs).DefaultIfEmpty(0).Max())
                .ThenBy(r => r.SessionId));

            snapshot.Flat.Clear();
            var visited = new HashSet<int>();
            foreach (var root in snapshot.Roots)
                Flatten(root, 0, snapshot.Flat, visited);
        }

        private static void Mark(BlockingSession s, HashSet<int> reachable)
        {
            if (!reachable.Add(s.SessionId))
                return;
            foreach (var b in s.Blocked)
                Mark(b, reachable);
        }

        private static void Flatten(BlockingSession s, int level, List<BlockingSession> flat, HashSet<int> visited)
        {
            if (!visited.Add(s.SessionId))
                return;
            s.Level = level;
            flat.Add(s);
            foreach (var b in s.Blocked.OrderByDescending(x => x.WaitTimeMs).ThenBy(x => x.SessionId))
                Flatten(b, level + 1, flat, visited);
        }

        /// <summary>
        /// A script that can be run in a query window: for parameterized text, DECLARE statements with the values
        /// the request is running with (live plan), else the values the cached plan was compiled with, else NULL.
        /// </summary>
        public static async Task<string> BuildRunnableSqlAsync(string connectionString, BlockingSession session, CancellationToken cancellationToken)
        {
            if (session == null)
                return null;
            if (!session.SqlNeedsParameters || !ParameterizedSql.TryParse(session.BatchText, out var parameters, out var body))
                return session.Sql;

            string statement = !string.IsNullOrWhiteSpace(session.CurrentStatement) ? session.CurrentStatement : body;
            Dictionary<string, string> values = null;
            string source = null;

            if (session.HasRequest)
            {
                try
                {
                    var live = await ReadLivePlanAsync(connectionString, session.SessionId, cancellationToken).ConfigureAwait(false);
                    values = ParameterizedSql.ReadParameterValues(live, runtime: true);
                    if (values.Count > 0)
                        source = "runtime values (live plan)";
                }
                catch (SqlException)
                {
                }
            }

            if ((values == null || values.Count == 0) && session.PlanHandle != null)
            {
                try
                {
                    var cached = await ReadPlanAsync(connectionString, session, cancellationToken).ConfigureAwait(false);
                    values = ParameterizedSql.ReadParameterValues(cached, runtime: false);
                    if (values.Count > 0)
                        source = "values the cached plan was compiled with (they may differ from the current ones)";
                }
                catch (SqlException)
                {
                }
            }

            return ParameterizedSql.BuildScript(parameters, values, source, statement);
        }

        private static async Task<string> ReadLivePlanAsync(string connectionString, int sessionId, CancellationToken cancellationToken)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = new SqlCommand(LivePlanQuery, connection) { CommandTimeout = 30 })
                {
                    command.Parameters.Add("@session_id", SqlDbType.Int).Value = sessionId;
                    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                }
            }
        }

        /// <summary>Cached plan of the statement a session is running (null when not available).</summary>
        public static async Task<string> ReadPlanAsync(string connectionString, BlockingSession session, CancellationToken cancellationToken)
        {
            if (session?.PlanHandle == null)
                return null;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = new SqlCommand(PlanQuery, connection) { CommandTimeout = 30 })
                {
                    command.Parameters.Add("@plan_handle", SqlDbType.VarBinary, 64).Value = session.PlanHandle;
                    command.Parameters.Add("@start", SqlDbType.Int).Value = session.StatementStart;
                    command.Parameters.Add("@end", SqlDbType.Int).Value = session.StatementEnd;
                    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                }
            }
        }
    }
}

namespace SSMSPlus.Features.Blocking
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Xml.Linq;

    /// <summary>Handles parameterized statement text such as "(@1 int)SELECT ... WHERE id = @1".</summary>
    public static class ParameterizedSql
    {
        public sealed class Parameter
        {
            public string Name { get; set; }
            public string Type { get; set; }
        }

        /// <summary>Splits "(@a int,@b decimal(18,2))body" into its parameters and the body.</summary>
        public static bool TryParse(string text, out List<Parameter> parameters, out string body)
        {
            parameters = new List<Parameter>();
            body = null;
            if (string.IsNullOrEmpty(text))
                return false;

            int start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
            if (start + 1 >= text.Length || text[start] != '(' || text[start + 1] != '@')
                return false;

            int depth = 0;
            int end = -1;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
                return false;

            string declaration = text.Substring(start + 1, end - start - 1);
            foreach (var part in SplitTopLevel(declaration))
            {
                var item = part.Trim();
                if (item.Length == 0)
                    continue;
                int space = item.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                if (!item.StartsWith("@", StringComparison.Ordinal) || space < 0)
                    return false;
                string type = item.Substring(space + 1).Trim();
                foreach (var suffix in new[] { " output", " out", " readonly" })
                {
                    if (type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        type = type.Substring(0, type.Length - suffix.Length).Trim();
                }
                parameters.Add(new Parameter { Name = item.Substring(0, space), Type = type });
            }

            body = text.Substring(end + 1).TrimStart();
            return parameters.Count > 0;
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0;
            int last = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(')
                    depth++;
                else if (text[i] == ')')
                    depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    yield return text.Substring(last, i - last);
                    last = i + 1;
                }
            }
            yield return text.Substring(last);
        }

        /// <summary>Reads ParameterList values from showplan XML: runtime values when asked and present, else compiled values.</summary>
        public static Dictionary<string, string> ReadParameterValues(string showPlanXml, bool runtime)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(showPlanXml))
                return values;

            XDocument doc;
            try
            {
                doc = XDocument.Parse(showPlanXml);
            }
            catch
            {
                return values;
            }

            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            foreach (var list in doc.Descendants(ns + "ParameterList"))
            {
                foreach (var column in list.Elements(ns + "ColumnReference"))
                {
                    var name = (string)column.Attribute("Column");
                    var value = runtime ? (string)column.Attribute("ParameterRuntimeValue") : (string)column.Attribute("ParameterCompiledValue");
                    if (string.IsNullOrEmpty(name) || value == null || values.ContainsKey(name))
                        continue;
                    values[name] = StripOuterParentheses(value);
                }
            }

            return values;
        }

        /// <summary>Showplan writes values as "(74390)" or "(N'abc')"; remove one pair of outer parentheses.</summary>
        public static string StripOuterParentheses(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '(' && value[value.Length - 1] == ')')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        public static string BuildScript(List<Parameter> parameters, Dictionary<string, string> values, string source, string statement)
        {
            var sb = new StringBuilder();
            bool allKnown = values != null && parameters.All(p => values.ContainsKey(p.Name));
            if (values != null && values.Count > 0)
                sb.Append("-- Parameter values: ").Append(source).Append("\r\n");
            if (!allKnown)
                sb.Append("-- Values not available from SQL Server are NULL: replace them before running.\r\n");

            foreach (var p in parameters)
            {
                string value = values != null && values.TryGetValue(p.Name, out var v) ? v : "NULL";
                sb.Append("DECLARE ").Append(p.Name).Append(' ').Append(p.Type).Append(" = ").Append(value).Append(";\r\n");
            }

            sb.Append("\r\n").Append(statement ?? string.Empty);
            return sb.ToString();
        }
    }
}
