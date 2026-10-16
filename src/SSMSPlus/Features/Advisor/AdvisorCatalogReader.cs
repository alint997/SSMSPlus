namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads, for the tables of a plan, the row count, the statistics (sys.stats + sys.dm_db_stats_properties)
    /// and the index definitions. Read-only catalog queries; errors are collected, not thrown.
    /// </summary>
    internal static class AdvisorCatalogReader
    {
        private const string TablesQuery = @"
SELECT o.object_id, OBJECT_SCHEMA_NAME(o.object_id) AS schema_name, o.name,
       (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS row_count
FROM sys.objects AS o
WHERE o.object_id IN ({0});";

        private const string StatisticsQuery = @"
SELECT s.object_id, s.name, s.stats_id, s.auto_created, s.user_created, s.no_recompute, s.has_filter,
       sp.last_updated, sp.rows, sp.rows_sampled, sp.modification_counter
FROM sys.stats AS s
OUTER APPLY sys.dm_db_stats_properties(s.object_id, s.stats_id) AS sp
WHERE s.object_id IN ({0});
SELECT sc.object_id, sc.stats_id, c.name
FROM sys.stats_columns AS sc
JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
WHERE sc.object_id IN ({0})
ORDER BY sc.object_id, sc.stats_id, sc.stats_column_id;";

        private const string IndexesQuery = @"
SELECT i.object_id, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled, i.filter_definition
FROM sys.indexes AS i
WHERE i.object_id IN ({0}) AND i.type > 0;
SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
FROM sys.index_columns AS ic
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE ic.object_id IN ({0})
ORDER BY ic.object_id, ic.index_id, ic.key_ordinal, ic.index_column_id;";

        public static async Task<AdvisorServerInfo> ReadAsync(string connectionString, string server, PlanDocument document, CancellationToken cancellationToken)
        {
            var result = new AdvisorServerInfo { Server = server };
            var tables = PlanTable.From(document).Where(t => !t.IsTemporary).ToList();
            if (tables.Count == 0)
                return result;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                string defaultDatabase = connection.Database;

                foreach (var group in tables.GroupBy(t => PlanNode.Clean(t.Database) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string database = string.IsNullOrEmpty(group.Key) ? defaultDatabase : group.Key;
                    try
                    {
                        if (!string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase))
                            connection.ChangeDatabase(database);
                        await ReadDatabaseAsync(connection, group.Key, group.ToList(), result, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add("Database " + database + ": " + ex.Message);
                    }
                }
            }

            return result;
        }

        private static async Task ReadDatabaseAsync(SqlConnection connection, string database, List<PlanTable> tables, AdvisorServerInfo result, CancellationToken cancellationToken)
        {
            // Resolve the names to object ids first (parameters, no string concatenation of names).
            var byId = new Dictionary<int, ServerTableInfo>();
            using (var command = connection.CreateCommand())
            {
                var sql = new StringBuilder();
                for (int i = 0; i < tables.Count; i++)
                {
                    sql.AppendFormat("SELECT {0} AS ix, OBJECT_ID(@p{0}) AS object_id;", i);
                    command.Parameters.AddWithValue("@p" + i, tables[i].TwoPartName);
                }
                command.CommandText = sql.ToString();
                command.CommandTimeout = 30;
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    do
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
                                continue;
                            var t = tables[reader.GetInt32(0)];
                            byId[reader.GetInt32(1)] = new ServerTableInfo
                            {
                                Database = string.IsNullOrEmpty(database) ? t.Database : database,
                                Schema = PlanNode.Clean(t.Schema),
                                Table = PlanNode.Clean(t.Table),
                                ObjectId = reader.GetInt32(1),
                            };
                        }
                    }
                    while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                }
            }

            if (byId.Count == 0)
                return;

            string ids = string.Join(", ", byId.Keys);

            using (var command = new SqlCommand(string.Format(TablesQuery, ids), connection) { CommandTimeout = 30 })
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (byId.TryGetValue(reader.GetInt32(0), out var t) && !(await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)))
                        t.Rows = Convert.ToInt64(reader.GetValue(3));
                }
            }

            using (var command = new SqlCommand(string.Format(StatisticsQuery, ids), connection) { CommandTimeout = 30 })
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                var stats = new Dictionary<string, ServerStatistics>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!byId.TryGetValue(reader.GetInt32(0), out var t))
                        continue;
                    var s = new ServerStatistics
                    {
                        Name = reader.GetString(1),
                        StatsId = reader.GetInt32(2),
                        AutoCreated = reader.GetBoolean(3),
                        UserCreated = reader.GetBoolean(4),
                        NoRecompute = reader.GetBoolean(5),
                        HasFilter = reader.GetBoolean(6),
                        LastUpdated = (await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)) ? (DateTime?)null : reader.GetDateTime(7),
                        Rows = (await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)) ? (long?)null : Convert.ToInt64(reader.GetValue(8)),
                        RowsSampled = (await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false)) ? (long?)null : Convert.ToInt64(reader.GetValue(9)),
                        ModificationCounter = (await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false)) ? (long?)null : Convert.ToInt64(reader.GetValue(10)),
                    };
                    t.Statistics.Add(s);
                    stats[t.ObjectId + ":" + s.StatsId] = s;
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (stats.TryGetValue(reader.GetInt32(0) + ":" + reader.GetInt32(1), out var s))
                        s.Columns.Add(reader.GetString(2));
                }
            }

            using (var command = new SqlCommand(string.Format(IndexesQuery, ids), connection) { CommandTimeout = 30 })
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                var indexes = new Dictionary<string, ServerIndex>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!byId.TryGetValue(reader.GetInt32(0), out var t) || (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)))
                        continue;
                    var i = new ServerIndex
                    {
                        IndexId = reader.GetInt32(1),
                        Name = reader.GetString(2),
                        TypeDesc = reader.GetString(3),
                        IsUnique = reader.GetBoolean(4),
                        IsPrimaryKey = reader.GetBoolean(5),
                        IsUniqueConstraint = reader.GetBoolean(6),
                        IsDisabled = !(await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)) && reader.GetBoolean(7),
                        FilterDefinition = (await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)) ? null : reader.GetString(8),
                    };
                    t.Indexes.Add(i);
                    indexes[t.ObjectId + ":" + i.IndexId] = i;
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!indexes.TryGetValue(reader.GetInt32(0) + ":" + reader.GetInt32(1), out var i))
                        continue;
                    string name = reader.GetString(2);
                    byte keyOrdinal = reader.GetByte(3);
                    bool descending = reader.GetBoolean(4);
                    bool included = reader.GetBoolean(5);
                    if (included)
                        i.IncludeColumns.Add(name);
                    else if (keyOrdinal > 0)
                        i.KeyColumns.Add(descending ? name + " DESC" : name);
                }
            }

            foreach (var t in byId.Values)
                result.Add(t);
        }
    }
}
