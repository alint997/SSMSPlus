namespace SSMSPlus.Features.ExecutionPlan
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Gets showplan XML from SQL Server on a separate connection.
    /// Estimated: SET SHOWPLAN_XML ON (nothing is executed).
    /// Actual: SET STATISTICS XML ON inside a transaction that is always rolled back.
    /// </summary>
    internal static class PlanRunner
    {
        private const string ShowPlanColumn = "Microsoft SQL Server 2005 XML Showplan";

        private static readonly Regex GoSeparator = new Regex(@"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<string> SplitBatches(string script)
        {
            var batches = new List<string>();
            int start = 0;
            foreach (Match m in GoSeparator.Matches(script ?? string.Empty))
            {
                batches.Add(script.Substring(start, m.Index - start));
                start = m.Index + m.Length;
            }
            batches.Add((script ?? string.Empty).Substring(start));
            return batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        }

        public static async Task<List<string>> GetPlansAsync(string connectionString, string script, bool actual, CancellationToken cancellationToken)
        {
            var plans = new List<string>();
            var batches = SplitBatches(script);
            if (batches.Count == 0)
                return plans;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                if (!actual)
                {
                    await ExecuteAsync(connection, "SET SHOWPLAN_XML ON;", cancellationToken).ConfigureAwait(false);
                    try
                    {
                        foreach (var batch in batches)
                            await ReadPlansAsync(connection, batch, plans, true, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await ExecuteAsync(connection, "SET SHOWPLAN_XML OFF;", CancellationToken.None).ConfigureAwait(false);
                    }

                    return plans;
                }

                await ExecuteAsync(connection, "SET STATISTICS XML ON; BEGIN TRANSACTION;", cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (var batch in batches)
                        await ReadPlansAsync(connection, batch, plans, false, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await ExecuteAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; SET STATISTICS XML OFF;", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The connection is closed (not pooled) right after, which rolls back anyway.
                    }
                }
            }

            return plans;
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
        {
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 30 })
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task ReadPlansAsync(SqlConnection connection, string batch, List<string> plans, bool everyResultIsPlan, CancellationToken cancellationToken)
        {
            using (var command = new SqlCommand(batch, connection) { CommandTimeout = 0 })
            using (var registration = cancellationToken.Register(() => { try { command.Cancel(); } catch { } }))
            using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
            {
                do
                {
                    bool isPlan = reader.FieldCount == 1 && (everyResultIsPlan || string.Equals(reader.GetName(0), ShowPlanColumn, StringComparison.OrdinalIgnoreCase));
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (isPlan && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                        {
                            var xml = reader.GetValue(0) as string ?? Convert.ToString(reader.GetValue(0));
                            if (ShowPlanParser.LooksLikeShowPlan(xml))
                                plans.Add(xml);
                        }
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            }
        }
    }
}
