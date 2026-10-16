namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public enum AdvisorSeverity
    {
        High = 0,
        Medium = 1,
        Info = 2,
    }

    /// <summary>One recommendation of the Query Advisor.</summary>
    public sealed class AdvisorFinding
    {
        public string RuleId { get; set; }
        public AdvisorSeverity Severity { get; set; }

        /// <summary>Access path, Statistics, Index, Query...</summary>
        public string Category { get; set; }
        public string Title { get; set; }

        /// <summary>Table / index / column the finding is about (for display).</summary>
        public string Object { get; set; }
        public string Explanation { get; set; }
        public string Suggestion { get; set; }

        /// <summary>Optional T-SQL that applies the suggestion (to review before running).</summary>
        public string Script { get; set; }

        public PlanStatement Statement { get; set; }
        public PlanNode Node { get; set; }

        /// <summary>Used to order findings of the same severity (higher first).</summary>
        public double Weight { get; set; }

        public string SeverityText => Severity.ToString();
        public int StatementIndex => Statement?.Index ?? 0;
        public string Location
        {
            get
            {
                if (Node == null || Node.IsRoot)
                    return Statement == null ? string.Empty : "Statement " + Statement.Index;
                if (Node.IsObject)
                    return Node.Title;
                return "(" + Node.NodeId + ") " + Node.PhysicalOp;
            }
        }
    }

    /// <summary>An index a rule would create (P1 covering, P2 scan, P6 missing index), before consolidation.</summary>
    public sealed class IndexCandidate
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }

        /// <summary>Key columns in order; descending ones end with " DESC".</summary>
        public List<string> Keys { get; } = new List<string>();
        public List<string> Includes { get; } = new List<string>();
        public string RuleId { get; set; }
        public PlanStatement Statement { get; set; }
        public double Weight { get; set; }

        public string TableKey => AdvisorServerInfo.Key(Database, Schema, Table);

        public IEnumerable<string> KeyNames => Keys.Select(k => k.EndsWith(" DESC", StringComparison.Ordinal) ? k.Substring(0, k.Length - 5) : k);
    }

    /// <summary>Everything the advisor found for a plan.</summary>
    public sealed class AdvisorResult
    {
        public List<AdvisorFinding> Findings { get; } = new List<AdvisorFinding>();
        public List<IndexCandidate> IndexCandidates { get; } = new List<IndexCandidate>();
    }

    /// <summary>Thresholds used by the rules (kept together so they are easy to tune).</summary>
    internal static class AdvisorThresholds
    {
        /// <summary>A lookup is worth covering from this share of the statement cost...</summary>
        public const double LookupCostPercent = 10;

        /// <summary>...or from this number of executions.</summary>
        public const double LookupExecutions = 1000;
        public const double LookupHighCostPercent = 25;
        public const double LookupHighExecutions = 10000;

        /// <summary>Scans of tables with fewer rows are not reported.</summary>
        public const double LargeTableRows = 10000;

        /// <summary>A scan whose predicate keeps at most this share of the rows could use an index.</summary>
        public const double SelectivePredicate = 0.10;

        public const double HighCostPercent = 25;
        public const double MediumCostPercent = 5;

        /// <summary>Row estimate ratio (either way) that makes a bad estimate "High".</summary>
        public const double SevereEstimateRatio = 100;

        /// <summary>Statistics are reported as stale from this share of modified rows...</summary>
        public const double StaleModifiedShare = 0.10;

        /// <summary>...and at least this many modifications.</summary>
        public const double StaleMinModifications = 1000;

        /// <summary>Sampled statistics below this percent are reported when the estimates are wrong.</summary>
        public const double LowSamplePercent = 10;
        public const double VeryLowSamplePercent = 5;
        public const double LowSampleMinRows = 1000000;

        /// <summary>Above this size UPDATE STATISTICS uses a sample instead of FULLSCAN in the script.</summary>
        public const double FullScanMaxRows = 50000000;

        public const double ExcessiveGrantKb = 100 * 1024;
        public const double ExcessiveGrantUsedShare = 0.10;

        public const double MissingIndexHighImpact = 70;
        public const double MissingIndexMediumImpact = 20;
        public const double MissingIndexHighStatementCost = 1;

        public const int MaxIncludeColumns = 12;

        /// <summary>Statements cheaper than this get index findings as Info only...</summary>
        public const double TrivialStatementCost = 0.2;

        /// <summary>...and below this at most Medium.</summary>
        public const double SmallStatementCost = 1;
    }

    /// <summary>Statistics object read from the server (sys.stats + sys.dm_db_stats_properties).</summary>
    public sealed class ServerStatistics
    {
        public string Name { get; set; }
        public int StatsId { get; set; }
        public List<string> Columns { get; } = new List<string>();
        public DateTime? LastUpdated { get; set; }
        public long? Rows { get; set; }
        public long? RowsSampled { get; set; }
        public long? ModificationCounter { get; set; }
        public bool NoRecompute { get; set; }
        public bool AutoCreated { get; set; }
        public bool UserCreated { get; set; }
        public bool HasFilter { get; set; }

        public double? SamplePercent => Rows.HasValue && Rows.Value > 0 && RowsSampled.HasValue ? RowsSampled.Value * 100.0 / Rows.Value : (double?)null;
    }

    public sealed class ServerIndex
    {
        public string Name { get; set; }
        public int IndexId { get; set; }
        public string TypeDesc { get; set; }
        public bool IsUnique { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsUniqueConstraint { get; set; }
        public bool IsDisabled { get; set; }
        public string FilterDefinition { get; set; }

        /// <summary>Key columns in order; descending ones end with " DESC".</summary>
        public List<string> KeyColumns { get; } = new List<string>();
        public List<string> IncludeColumns { get; } = new List<string>();

        public bool IsClustered => TypeDesc != null && TypeDesc.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase) && !IsColumnstore;
        public bool IsColumnstore => TypeDesc != null && TypeDesc.IndexOf("COLUMNSTORE", StringComparison.OrdinalIgnoreCase) >= 0;

        public IEnumerable<string> KeyColumnNames => KeyColumns.Select(k => k.EndsWith(" DESC", StringComparison.Ordinal) ? k.Substring(0, k.Length - 5) : k);
    }

    public sealed class ServerTableInfo
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }
        public int ObjectId { get; set; }
        public long? Rows { get; set; }
        public List<ServerStatistics> Statistics { get; } = new List<ServerStatistics>();
        public List<ServerIndex> Indexes { get; } = new List<ServerIndex>();
    }

    /// <summary>Catalog information read from the server for the tables of a plan.</summary>
    public sealed class AdvisorServerInfo
    {
        private readonly Dictionary<string, ServerTableInfo> _tables = new Dictionary<string, ServerTableInfo>(StringComparer.OrdinalIgnoreCase);

        public string Server { get; set; }
        public DateTime ReadAt { get; set; } = DateTime.Now;
        public List<string> Errors { get; } = new List<string>();
        public IEnumerable<ServerTableInfo> Tables => _tables.Values;

        public static string Key(string database, string schema, string table)
        {
            return (PlanNode.Clean(database) ?? string.Empty) + "|" + (PlanNode.Clean(schema) ?? string.Empty) + "|" + (PlanNode.Clean(table) ?? string.Empty);
        }

        public void Add(ServerTableInfo table) => _tables[Key(table.Database, table.Schema, table.Table)] = table;

        public ServerTableInfo Find(string database, string schema, string table)
        {
            _tables.TryGetValue(Key(database, schema, table), out var info);
            return info;
        }

        public ServerTableInfo Find(PlanNode node) => node == null ? null : Find(node.ObjectDatabase, node.ObjectSchema, node.ObjectTable);
    }

    /// <summary>A table of the plan (database, schema, table).</summary>
    public sealed class PlanTable
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }

        public string Key => AdvisorServerInfo.Key(Database, Schema, Table);

        /// <summary>Temporary tables and table variables are skipped by the catalog checks.</summary>
        public bool IsTemporary
        {
            get
            {
                var t = PlanNode.Clean(Table) ?? string.Empty;
                return t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("@", StringComparison.Ordinal)
                    || string.Equals(PlanNode.Clean(Database), "tempdb", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string TwoPartName => Sql.Name(Schema, Table);

        public static List<PlanTable> From(PlanDocument document)
        {
            var result = new Dictionary<string, PlanTable>(StringComparer.OrdinalIgnoreCase);
            if (document == null)
                return new List<PlanTable>();

            foreach (var statement in document.Statements)
            {
                if (statement.Root == null)
                    continue;
                foreach (var node in statement.Root.DescendantsAndSelf().Where(n => !n.IsObject && !string.IsNullOrEmpty(n.ObjectTable)))
                {
                    var t = new PlanTable { Database = node.ObjectDatabase, Schema = node.ObjectSchema, Table = node.ObjectTable };
                    if (!result.ContainsKey(t.Key))
                        result.Add(t.Key, t);
                }
            }

            return result.Values.ToList();
        }
    }

    /// <summary>T-SQL formatting helpers for the generated scripts.</summary>
    internal static class Sql
    {
        public static string Quote(string name)
        {
            var clean = PlanNode.Clean(name);
            return string.IsNullOrEmpty(clean) ? string.Empty : "[" + clean.Replace("]", "]]") + "]";
        }

        public static string Name(string schema, string table)
        {
            return string.IsNullOrEmpty(PlanNode.Clean(schema)) ? Quote(table) : Quote(schema) + "." + Quote(table);
        }

        public static string Use(string database)
        {
            return string.IsNullOrEmpty(PlanNode.Clean(database)) ? string.Empty : "USE " + Quote(database) + ";" + Environment.NewLine + "GO" + Environment.NewLine;
        }

        public static string IndexName(string table, IEnumerable<string> keys, bool hasIncludes)
        {
            var name = "IX_" + PlanNode.Clean(table) + "_" + string.Join("_", keys.Select(PlanNode.Clean)) + (hasIncludes ? "_incl" : string.Empty);
            name = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        public static string ColumnList(IEnumerable<string> columns)
        {
            return string.Join(", ", columns.Select(c =>
                c.EndsWith(" DESC", StringComparison.Ordinal) ? Quote(c.Substring(0, c.Length - 5)) + " DESC" : Quote(c)));
        }
    }
}
