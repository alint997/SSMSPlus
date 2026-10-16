namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    public enum IndexAction
    {
        Create,
        Extend,
        AlreadyCovered,
    }

    /// <summary>One consolidated index recommendation for a table.</summary>
    public sealed class IndexRecommendation
    {
        public IndexAction Action { get; set; }
        public string Name { get; set; }
        public List<string> Keys { get; } = new List<string>();
        public List<string> Includes { get; } = new List<string>();
        public ServerIndex Existing { get; set; }
        public List<IndexCandidate> Sources { get; } = new List<IndexCandidate>();
        public bool IncludesDropped { get; set; }
        public double Weight => Sources.Sum(s => s.Weight);
        public string Script { get; set; }
    }

    /// <summary>Recommendations and existing indexes of one table.</summary>
    public sealed class TableIndexAdvice
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }
        public ServerTableInfo Info { get; set; }
        public List<IndexRecommendation> Recommendations { get; } = new List<IndexRecommendation>();
    }

    /// <summary>
    /// Index Advisor (DB2 style, without what-if indexes): merges the indexes suggested by the rules (P1 covering,
    /// P2 scans, P6 optimizer missing indexes) per table, so one index serves several statements, and compares
    /// them with the existing indexes: extend an index that already has the same leading keys instead of
    /// creating a near duplicate, or report that an existing index already covers the suggestion.
    /// </summary>
    public static class IndexAdvisor
    {
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

        public static List<TableIndexAdvice> Recommend(IEnumerable<IndexCandidate> candidates, AdvisorServerInfo server)
        {
            var result = new List<TableIndexAdvice>();
            foreach (var table in candidates.Where(c => c.Keys.Count > 0).GroupBy(c => c.TableKey, Ci))
            {
                var first = table.First();
                var advice = new TableIndexAdvice
                {
                    Database = first.Database,
                    Schema = first.Schema,
                    Table = first.Table,
                    Info = server?.Find(first.Database, first.Schema, first.Table),
                };

                // 1. Merge: a candidate whose keys are a prefix of another one is served by the longer one.
                var merged = new List<IndexRecommendation>();
                foreach (var c in table.OrderByDescending(c => c.Keys.Count).ThenByDescending(c => c.Weight))
                {
                    var target = merged.FirstOrDefault(m => IsPrefix(c.KeyNames.ToList(), Names(m.Keys)));
                    if (target == null)
                    {
                        target = new IndexRecommendation { Action = IndexAction.Create };
                        target.Keys.AddRange(c.Keys);
                        merged.Add(target);
                    }

                    target.Sources.Add(c);
                    foreach (var include in c.Includes.Concat(c.KeyNames))
                        if (!Names(target.Keys).Contains(include, Ci) && !target.Includes.Contains(include, Ci))
                            target.Includes.Add(include);
                }

                // 2. Compare with the existing indexes.
                foreach (var m in merged)
                {
                    if (m.Includes.Count > AdvisorThresholds.MaxIncludeColumns)
                    {
                        m.Includes.Clear();
                        m.IncludesDropped = true;
                    }

                    var keys = Names(m.Keys);
                    var usable = advice.Info?.Indexes.Where(i => !i.IsColumnstore && !i.IsDisabled && string.IsNullOrEmpty(i.FilterDefinition)).ToList()
                        ?? new List<ServerIndex>();

                    // An existing index whose keys start with ours: it can do the same seeks.
                    var covering = usable.FirstOrDefault(i => IsPrefix(keys, i.KeyColumnNames.ToList()));
                    if (covering != null)
                    {
                        var available = new HashSet<string>(covering.KeyColumnNames.Concat(covering.IncludeColumns), Ci);
                        if (covering.IsClustered)
                            available.UnionWith(m.Includes); // the clustered index has every column
                        var missing = m.Includes.Where(c => !available.Contains(c)).ToList();
                        m.Existing = covering;
                        m.Name = covering.Name;
                        if (missing.Count == 0)
                        {
                            m.Action = IndexAction.AlreadyCovered;
                        }
                        else
                        {
                            m.Action = IndexAction.Extend;
                            m.Keys.Clear();
                            m.Keys.AddRange(covering.KeyColumns);
                            m.Includes.Clear();
                            m.Includes.AddRange(covering.IncludeColumns.Concat(missing).Distinct(Ci));
                        }
                    }
                    else
                    {
                        // An existing nonclustered index whose keys are a prefix of ours: widen it instead of a near duplicate.
                        var narrower = usable.Where(i => !i.IsClustered && !i.IsPrimaryKey && !i.IsUniqueConstraint && !i.IsUnique
                                && i.KeyColumns.Count > 0 && IsPrefix(i.KeyColumnNames.ToList(), keys))
                            .OrderByDescending(i => i.KeyColumns.Count)
                            .FirstOrDefault();
                        if (narrower != null)
                        {
                            m.Action = IndexAction.Extend;
                            m.Existing = narrower;
                            m.Name = narrower.Name;
                            foreach (var include in narrower.IncludeColumns)
                                if (!keys.Contains(include, Ci) && !m.Includes.Contains(include, Ci))
                                    m.Includes.Add(include);
                        }
                        else
                        {
                            m.Action = IndexAction.Create;
                            m.Name = Sql.IndexName(advice.Table, keys, m.Includes.Count > 0);
                        }
                    }

                    m.Script = BuildScript(advice, m);
                }

                // Two candidates can end up extending the same existing index: keep the widest.
                foreach (var group in merged.Where(m => m.Action == IndexAction.Extend).GroupBy(m => m.Name, Ci).Where(g => g.Count() > 1).ToList())
                {
                    var keep = group.OrderByDescending(m => m.Keys.Count).First();
                    foreach (var other in group.Where(m => m != keep))
                    {
                        foreach (var include in other.Includes.Concat(Names(other.Keys)))
                            if (!Names(keep.Keys).Contains(include, Ci) && !keep.Includes.Contains(include, Ci))
                                keep.Includes.Add(include);
                        keep.Sources.AddRange(other.Sources);
                        merged.Remove(other);
                    }
                    keep.Script = BuildScript(advice, keep);
                }

                advice.Recommendations.AddRange(merged.OrderBy(m => m.Action == IndexAction.AlreadyCovered ? 1 : 0).ThenByDescending(m => m.Weight));
                result.Add(advice);
            }

            return result.OrderByDescending(a => a.Recommendations.Where(r => r.Action != IndexAction.AlreadyCovered).Sum(r => r.Weight)).ToList();
        }

        private static List<string> Names(IEnumerable<string> keys)
        {
            return keys.Select(k => k.EndsWith(" DESC", StringComparison.Ordinal) ? k.Substring(0, k.Length - 5) : k).ToList();
        }

        private static bool IsPrefix(IList<string> prefix, IList<string> keys)
        {
            if (prefix.Count == 0 || prefix.Count > keys.Count)
                return false;
            for (int i = 0; i < prefix.Count; i++)
            {
                if (!string.Equals(prefix[i], keys[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string BuildScript(TableIndexAdvice table, IndexRecommendation m)
        {
            if (m.Action == IndexAction.AlreadyCovered)
                return null;

            var sb = new StringBuilder();
            bool extend = m.Action == IndexAction.Extend && m.Existing != null;
            sb.Append("CREATE ").Append(extend && m.Existing.IsUnique ? "UNIQUE " : string.Empty).Append("NONCLUSTERED INDEX ").Append(Sql.Quote(m.Name)).AppendLine();
            sb.Append("ON ").Append(Sql.Name(table.Schema, table.Table)).Append(" (").Append(Sql.ColumnList(m.Keys)).Append(')');
            if (m.Includes.Count > 0)
                sb.AppendLine().Append("INCLUDE (").Append(Sql.ColumnList(m.Includes)).Append(')');
            if (extend && !string.IsNullOrEmpty(m.Existing.FilterDefinition))
                sb.AppendLine().Append("WHERE ").Append(m.Existing.FilterDefinition);
            if (extend)
                sb.AppendLine().Append("WITH (DROP_EXISTING = ON)");
            sb.Append(';');
            return sb.ToString();
        }

        /// <summary>The whole advice as a T-SQL document: comments with the reasons and existing indexes, then the scripts.</summary>
        public static string Report(IList<TableIndexAdvice> tables, AdvisorServerInfo server)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- SSMS Plus Index Advisor");
            sb.AppendLine("-- Indexes suggested by the rules (P1 covering, P2 scans, P6 optimizer), merged per table and compared with the existing indexes.");
            sb.AppendLine("-- Review every index before creating it: each index speeds up reads but slows down INSERT / UPDATE / DELETE and uses space.");
            if (server == null)
                sb.AppendLine("-- The existing indexes are not known: use \"Read from server\" so suggestions can be merged with them.");
            if (tables.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- No index suggestions for this plan.");
                return sb.ToString();
            }

            foreach (var t in tables)
            {
                sb.AppendLine();
                sb.Append("-- ===== ").Append(Sql.Name(t.Schema, t.Table));
                if (t.Info?.Rows != null)
                    sb.Append("  (").Append(QueryAdvisor.Count(t.Info.Rows.Value)).Append(" rows)");
                sb.AppendLine();

                if (t.Info != null)
                {
                    sb.AppendLine("-- Existing indexes:");
                    if (t.Info.Indexes.Count == 0)
                        sb.AppendLine("--   (none: heap)");
                    foreach (var i in t.Info.Indexes.OrderBy(i => i.IndexId))
                    {
                        sb.Append("--   ").Append(i.Name).Append(" (").Append(Describe(i)).Append("): ").Append(string.Join(", ", i.KeyColumns));
                        if (i.IncludeColumns.Count > 0)
                            sb.Append("  INCLUDE (").Append(string.Join(", ", i.IncludeColumns)).Append(')');
                        if (!string.IsNullOrEmpty(i.FilterDefinition))
                            sb.Append("  WHERE ").Append(i.FilterDefinition);
                        sb.AppendLine();
                    }
                }

                int n = 0;
                foreach (var r in t.Recommendations)
                {
                    sb.AppendLine();
                    var reasons = string.Join(", ", r.Sources
                        .GroupBy(s => s.RuleId + " statement " + (s.Statement?.Index ?? 0))
                        .Select(g => g.Key));
                    switch (r.Action)
                    {
                        case IndexAction.AlreadyCovered:
                            sb.Append("-- Already covered by ").Append(r.Name).Append(" (same keys and columns): ").Append(reasons)
                              .AppendLine(". No new index needed; if the plan does not use it, see the finding (estimates, conversions, parameter values).");
                            continue;
                        case IndexAction.Extend:
                            sb.Append("-- ").Append(++n).Append(". Extend ").Append(r.Name).Append(" (same leading keys) instead of a new index. For: ").AppendLine(reasons);
                            break;
                        default:
                            sb.Append("-- ").Append(++n).Append(". New index. For: ").AppendLine(reasons);
                            break;
                    }
                    if (r.Sources.Count > 1)
                        sb.AppendLine("--    One index for " + r.Sources.Count + " suggestions (keys merged, included columns combined).");
                    if (r.IncludesDropped)
                        sb.AppendLine("--    Too many columns to include: the index has only the keys; the queries keep a Key Lookup.");
                    sb.Append(Sql.Use(t.Database));
                    sb.AppendLine(r.Script);
                }
            }

            return sb.ToString();
        }

        private static string Describe(ServerIndex i)
        {
            var parts = new List<string> { (i.TypeDesc ?? string.Empty).ToLowerInvariant().Replace('_', ' ') };
            if (i.IsPrimaryKey)
                parts.Add("primary key");
            else if (i.IsUniqueConstraint)
                parts.Add("unique constraint");
            else if (i.IsUnique)
                parts.Add("unique");
            if (i.IsDisabled)
                parts.Add("disabled");
            return string.Join(", ", parts);
        }
    }
}
