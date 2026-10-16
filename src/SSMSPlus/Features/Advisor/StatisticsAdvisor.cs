namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Statistics Advisor (S1-S4): stale, never updated, low sample and NORECOMPUTE statistics on the tables of
    /// the plan. Uses the server catalog when it was read, otherwise the OptimizerStatsUsage of the plan
    /// (SQL Server 2017+ plans).
    /// </summary>
    internal static class StatisticsRules
    {
        private sealed class StatItem
        {
            public string Name;
            public string Columns;
            public double? Rows;
            public double? Modifications;
            public double? SamplePercent;
            public DateTime? LastUpdated;
            public bool NoRecompute;
            public bool NeverUpdated;
        }

        public static void Run(PlanDocument document, AdvisorServerInfo server, List<AdvisorFinding> findings)
        {
            var nodes = document.Statements.Where(s => s.Root != null)
                .SelectMany(s => s.Root.DescendantsAndSelf())
                .Where(n => !n.IsObject && !n.IsRoot && !string.IsNullOrEmpty(n.ObjectTable))
                .ToList();

            var badTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bad in document.Statements.Where(s => s.Root != null).SelectMany(s => s.Operators()).Where(QueryAdvisor.IsEstimateOrigin))
            {
                foreach (var n in bad.DescendantsAndSelf().Where(n => !n.IsObject && !string.IsNullOrEmpty(n.ObjectTable)))
                    badTables.Add(AdvisorServerInfo.Key(n.ObjectDatabase, n.ObjectSchema, n.ObjectTable));
            }

            var usage = document.Statements.SelectMany(s => s.StatisticsUsage).ToList();

            foreach (var table in PlanTable.From(document).Where(t => !t.IsTemporary))
            {
                var tableNodes = nodes.Where(n => string.Equals(AdvisorServerInfo.Key(n.ObjectDatabase, n.ObjectSchema, n.ObjectTable), table.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var tableUsage = usage.Where(u => string.Equals(AdvisorServerInfo.Key(u.Database, u.Schema, u.Table), table.Key, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(u => PlanNode.Clean(u.Statistics), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                var used = new HashSet<string>(tableUsage.Select(u => PlanNode.Clean(u.Statistics)), StringComparer.OrdinalIgnoreCase);

                var items = new List<StatItem>();
                var info = server?.Find(table.Database, table.Schema, table.Table);
                double? tableRows = info?.Rows ?? tableNodes.Select(n => n.TableCardinality).Where(r => r.HasValue).Select(r => r.Value).DefaultIfEmpty().Max();
                if (tableRows.HasValue && tableRows.Value <= 0 && info?.Rows == null)
                    tableRows = null;

                if (info != null)
                {
                    foreach (var st in info.Statistics)
                    {
                        bool relevant = used.Count > 0 ? used.Contains(st.Name) : IsReferenced(st, tableNodes);
                        if (!relevant)
                            continue;
                        items.Add(new StatItem
                        {
                            Name = st.Name,
                            Columns = string.Join(", ", st.Columns),
                            Rows = st.Rows ?? info.Rows,
                            Modifications = st.ModificationCounter,
                            SamplePercent = st.SamplePercent,
                            LastUpdated = st.LastUpdated,
                            NoRecompute = st.NoRecompute,
                            NeverUpdated = !st.LastUpdated.HasValue && (info.Rows ?? 0) > 0,
                        });
                    }
                }
                else
                {
                    foreach (var u in tableUsage)
                    {
                        items.Add(new StatItem
                        {
                            Name = PlanNode.Clean(u.Statistics),
                            Rows = tableRows,
                            Modifications = u.ModificationCount,
                            SamplePercent = u.SamplingPercent,
                            LastUpdated = u.LastUpdate,
                        });
                    }
                }

                bool badEstimate = badTables.Contains(table.Key);
                var flagged = new List<Tuple<StatItem, List<string>>>();
                bool serious = false;
                foreach (var item in items)
                {
                    var reasons = new List<string>();
                    double rows = item.Rows ?? 0;
                    bool stale = item.Modifications.HasValue && rows > 0
                        && item.Modifications.Value >= AdvisorThresholds.StaleMinModifications
                        && item.Modifications.Value / rows >= AdvisorThresholds.StaleModifiedShare;
                    if (stale)
                    {
                        reasons.Add(string.Format(CultureInfo.CurrentCulture, "{0} modifications since the last update ({1} of {2} rows)",
                            QueryAdvisor.Count(item.Modifications.Value), QueryAdvisor.Percent(item.Modifications.Value * 100 / rows), QueryAdvisor.Count(rows)));
                        serious = true;
                    }

                    if (item.NeverUpdated)
                    {
                        reasons.Add("never updated (empty statistics on a table with rows)");
                        serious = true;
                    }

                    if (item.SamplePercent.HasValue && item.SamplePercent.Value < AdvisorThresholds.LowSamplePercent
                        && (badEstimate || (item.SamplePercent.Value < AdvisorThresholds.VeryLowSamplePercent && rows >= AdvisorThresholds.LowSampleMinRows)))
                    {
                        reasons.Add("built from a sample of " + QueryAdvisor.Percent(item.SamplePercent.Value) + " of the rows");
                    }

                    if (item.NoRecompute && (stale || item.NeverUpdated))
                        reasons.Add("NORECOMPUTE is set, so SQL Server never updates it automatically");

                    if (reasons.Count > 0)
                        flagged.Add(Tuple.Create(item, reasons));
                }

                if (flagged.Count == 0)
                    continue;

                var severity = badEstimate ? (serious ? AdvisorSeverity.High : AdvisorSeverity.Medium) : (serious ? AdvisorSeverity.Medium : AdvisorSeverity.Info);
                var node = tableNodes.OrderByDescending(n => n.CostPercent).FirstOrDefault();
                var statement = node?.Statement ?? document.Statements.FirstOrDefault();

                var text = new StringBuilder();
                text.Append(flagged.Count == 1 ? "One statistics object" : flagged.Count + " statistics objects")
                    .Append(" used for ").Append(table.TwoPartName).Append(info == null ? " (from the plan)" : string.Empty).Append(" may be out of date:");
                foreach (var f in flagged)
                {
                    text.AppendLine().Append("• ").Append(Sql.Quote(f.Item1.Name));
                    if (!string.IsNullOrEmpty(f.Item1.Columns))
                        text.Append(" (").Append(f.Item1.Columns).Append(')');
                    text.Append(": ").Append(string.Join("; ", f.Item2));
                    if (f.Item1.LastUpdated.HasValue)
                        text.Append("; last updated ").Append(f.Item1.LastUpdated.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                }
                if (badEstimate)
                    text.AppendLine().Append("Some row estimates on this table are wrong in the actual plan (P3).");

                var finding = new AdvisorFinding
                {
                    RuleId = "S1",
                    Severity = severity,
                    Category = "Statistics",
                    Title = "Statistics may be out of date",
                    Object = table.TwoPartName,
                    Statement = statement,
                    Node = node ?? statement?.Root,
                    Weight = (badEstimate ? 50 : 10) + flagged.Count,
                    Explanation = text.ToString(),
                    Suggestion = "Update these statistics with FULLSCAN, then get the plan again. If they get stale quickly, schedule a statistics job (for example Ola Hallengren's IndexOptimize with @UpdateStatistics) or use a persisted sample percent."
                        + (flagged.Any(f => f.Item1.NoRecompute) ? " UPDATE STATISTICS without NORECOMPUTE turns automatic updates back on." : string.Empty),
                    Script = QueryAdvisor.UpdateStatistics(table.Database, table.Schema, table.Table, flagged.Select(f => f.Item1.Name).ToList(), flagged.Select(f => f.Item1.Rows).Max()),
                };
                findings.Add(finding);
            }
        }

        /// <summary>Without OptimizerStatsUsage: statistics whose leading column the plan filters / seeks on, or of an index the plan reads.</summary>
        private static bool IsReferenced(ServerStatistics st, List<PlanNode> tableNodes)
        {
            if (tableNodes.Any(n => string.Equals(PlanNode.Clean(n.ObjectIndex), st.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
            var leading = st.Columns.FirstOrDefault();
            if (leading == null)
                return false;
            return tableNodes.Any(n => n.PredicateColumns.Concat(n.SeekColumns).Any(c => c.BelongsTo(n) && string.Equals(c.Name, leading, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
