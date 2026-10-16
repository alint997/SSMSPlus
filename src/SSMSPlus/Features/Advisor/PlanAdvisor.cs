namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Query Advisor: rules on the execution plan (access path, indexes, warnings) and on the statistics of
    /// the tables it reads, inspired by the DB2 Query Tuner advisors. Pure logic, no UI and no connection:
    /// the catalog information comes in through <see cref="AdvisorServerInfo"/> (optional).
    /// </summary>
    public static class QueryAdvisor
    {
        public static List<AdvisorFinding> Analyze(PlanDocument document, AdvisorServerInfo server)
        {
            return AnalyzeAll(document, server).Findings;
        }

        /// <summary>Findings (sorted) and the index candidates of the rules, for the consolidated index advice.</summary>
        public static AdvisorResult AnalyzeAll(PlanDocument document, AdvisorServerInfo server)
        {
            var result = new AdvisorResult();
            if (document == null)
                return result;

            var findings = new List<AdvisorFinding>();
            double total = document.Statements.Sum(s => Math.Max(0, s.SubtreeCost));
            foreach (var statement in document.Statements.Where(s => s.Root != null))
            {
                var context = new PlanRules(statement, server, total, findings, result.IndexCandidates);
                context.Run();
                QueryTextAdvisor.Run(statement, findings);
            }

            StatisticsRules.Run(document, server, findings);
            result.Findings.AddRange(Sort(findings));
            return result;
        }

        public static List<AdvisorFinding> Sort(IEnumerable<AdvisorFinding> findings)
        {
            return findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Weight).ThenBy(f => f.StatementIndex).ToList();
        }

        /// <summary>"2 high, 1 medium, 3 info".</summary>
        public static string Summary(IEnumerable<AdvisorFinding> findings)
        {
            var list = findings.ToList();
            var parts = new List<string>();
            int high = list.Count(f => f.Severity == AdvisorSeverity.High);
            int medium = list.Count(f => f.Severity == AdvisorSeverity.Medium);
            int info = list.Count(f => f.Severity == AdvisorSeverity.Info);
            if (high > 0)
                parts.Add(high + " high");
            if (medium > 0)
                parts.Add(medium + " medium");
            if (info > 0)
                parts.Add(info + " info");
            return parts.Count == 0 ? "no findings" : string.Join(", ", parts);
        }

        internal static string Count(double value) => value.ToString("N0", CultureInfo.CurrentCulture);

        internal static string Percent(double value) => value.ToString(value < 1 ? "0.##" : "0.#", CultureInfo.CurrentCulture) + "%";

        internal static bool SameTable(PlanNode a, PlanNode b)
        {
            return a != null && b != null
                && string.Equals(PlanNode.Clean(a.ObjectTable), PlanNode.Clean(b.ObjectTable), StringComparison.OrdinalIgnoreCase)
                && string.Equals(PlanNode.Clean(a.ObjectSchema), PlanNode.Clean(b.ObjectSchema), StringComparison.OrdinalIgnoreCase)
                && string.Equals(PlanNode.Clean(a.ObjectDatabase), PlanNode.Clean(b.ObjectDatabase), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the row estimate of the node is wrong by itself: per execution, and not only because the
        /// operator ran more / fewer times than estimated (inner side of nested loops), and not inherited from a child.
        /// </summary>
        internal static bool IsEstimateOrigin(PlanNode node)
        {
            if (node == null || !node.BadRowEstimate)
                return false;
            if (node.Children.Any(c => !c.IsObject && c.DescendantsAndSelf().Any(d => !d.IsObject && d.BadRowEstimate)))
                return false;
            double perExecution = Math.Max(node.ActualRows / Math.Max(1, node.ActualExecutions), 1);
            double estimated = Math.Max(node.EstimateRows, 1);
            double ratio = perExecution / estimated;
            return ratio >= 10 || ratio <= 0.1;
        }

        internal static string TableName(PlanNode node) => Sql.Name(node.ObjectSchema, node.ObjectTable);

        internal static string IndexName(PlanNode node)
        {
            return string.IsNullOrEmpty(node.ObjectIndex) ? TableName(node) : TableName(node) + "." + Sql.Quote(node.ObjectIndex);
        }

        internal static string UpdateStatistics(string database, string schema, string table, IList<string> statistics, double? rows)
        {
            var sb = new StringBuilder();
            sb.Append(Sql.Use(database));
            sb.Append("UPDATE STATISTICS ").Append(Sql.Name(schema, table));
            if (statistics != null && statistics.Count > 0)
                sb.Append(" (").Append(string.Join(", ", statistics.Select(Sql.Quote))).Append(')');
            if (rows.HasValue && rows.Value > AdvisorThresholds.FullScanMaxRows)
                sb.Append(" WITH SAMPLE 25 PERCENT; -- large table: FULLSCAN is more accurate but takes longer");
            else
                sb.Append(" WITH FULLSCAN;");
            return sb.ToString();
        }
    }

    /// <summary>Plan rules P1-P9 for one statement.</summary>
    internal sealed class PlanRules
    {
        private readonly PlanStatement _statement;
        private readonly AdvisorServerInfo _server;
        private readonly List<AdvisorFinding> _findings;
        private readonly double _statementShare;
        private readonly List<PlanNode> _operators;
        private readonly List<IndexCandidate> _candidates;

        public PlanRules(PlanStatement statement, AdvisorServerInfo server, double totalCost, List<AdvisorFinding> findings, List<IndexCandidate> candidates)
        {
            _candidates = candidates ?? new List<IndexCandidate>();
            _statement = statement;
            _server = server;
            _findings = findings;
            _statementShare = totalCost > 0 ? Math.Max(0, statement.SubtreeCost) / totalCost : 1;
            _operators = statement.Operators().ToList();
        }

        public void Run()
        {
            KeyLookups();
            Scans();
            BadEstimates();
            Spills();
            ImplicitConversions();
            MissingIndexes();
            NoJoinPredicate();
            NoStatistics();
            MemoryGrant();
            ParameterSniffing();
        }

        private AdvisorFinding Add(string rule, AdvisorSeverity severity, string category, string title, PlanNode node, double nodeWeight)
        {
            var f = new AdvisorFinding
            {
                RuleId = rule,
                Severity = severity,
                Category = category,
                Title = title,
                Statement = _statement,
                Node = node ?? _statement.Root,
                Weight = nodeWeight * _statementShare,
            };
            if (node != null && !node.IsRoot && !string.IsNullOrEmpty(node.ObjectTable))
                f.Object = QueryAdvisor.IndexName(node);
            _findings.Add(f);
            return f;
        }

        private static double Executions(PlanNode n) => n.HasActual ? n.ActualExecutions : n.EstimateExecutions;

        private static double RowsPerExecution(PlanNode n) => n.HasActual ? n.ActualRows / Math.Max(1, n.ActualExecutions) : n.EstimateRows;

        private static bool IsColumnstore(PlanNode n)
        {
            return (n.PhysicalOp ?? string.Empty).IndexOf("Columnstore", StringComparison.OrdinalIgnoreCase) >= 0
                || (n.IndexKind ?? string.Empty).IndexOf("Columnstore", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(n.ExecutionMode, "Batch", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- P1: Key / RID lookup

        private void KeyLookups()
        {
            foreach (var lookup in _operators.Where(o => o.Category == PlanNodeCategory.Lookup))
            {
                double executions = Executions(lookup);
                if (lookup.CostPercent < AdvisorThresholds.LookupCostPercent && executions < AdvisorThresholds.LookupExecutions)
                    continue;

                bool heap = (lookup.PhysicalOp ?? string.Empty).StartsWith("RID", StringComparison.OrdinalIgnoreCase);
                var severity = CapByStatementCost(lookup.CostPercent >= AdvisorThresholds.LookupHighCostPercent || executions >= AdvisorThresholds.LookupHighExecutions
                    ? AdvisorSeverity.High
                    : AdvisorSeverity.Medium);

                var access = FindIndexAccess(lookup);
                var needed = lookup.OutputColumns.Concat(lookup.PredicateColumns)
                    .Where(c => c.BelongsTo(lookup))
                    .Select(c => c.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string source = access != null ? QueryAdvisor.IndexName(access) : "the nonclustered index";
                var f = Add("P1", severity, "Access path", heap ? "RID Lookup: index does not cover the query" : "Key Lookup: index does not cover the query", lookup, lookup.CostPercent + Math.Min(50, executions / 1000));
                f.Object = access != null ? QueryAdvisor.IndexName(access) : QueryAdvisor.TableName(lookup);

                var text = new StringBuilder();
                text.AppendFormat(CultureInfo.CurrentCulture,
                    "For each row found in {0}, SQL Server goes back to the {1} to read {2}. The lookup ran {3} time(s) and costs {4} of the statement.",
                    source,
                    heap ? "heap (table without clustered index)" : "clustered index",
                    needed.Count == 0 ? "more columns" : needed.Count + " more column(s): " + string.Join(", ", needed),
                    QueryAdvisor.Count(executions),
                    QueryAdvisor.Percent(lookup.CostPercent));
                if (!string.IsNullOrEmpty(lookup.Predicate))
                    text.Append(" The lookup also filters rows with: ").Append(lookup.Predicate).Append('.');
                f.Explanation = text.ToString() + CostNote();

                var suggestion = new StringBuilder("Make the index cover the query: add the columns read by the lookup to its INCLUDE list, so the lookup (and its nested loops) disappears.");
                if (needed.Count > AdvisorThresholds.MaxIncludeColumns)
                    suggestion.Append(" The query reads many columns; select only the columns you need first, otherwise the index becomes very wide.");
                if (heap)
                    suggestion.Append(" The table is a heap: a clustered index usually helps too.");
                f.Suggestion = suggestion.ToString();

                if (needed.Count > 0 && needed.Count <= AdvisorThresholds.MaxIncludeColumns * 2)
                {
                    f.Script = CoveringIndexScript(lookup, access, needed);
                    AddCoveringCandidate(lookup, access, needed, f.Weight);
                }
            }
        }

        /// <summary>The seek / scan on another index of the same table that feeds the lookup (same nested loops).</summary>
        private static PlanNode FindIndexAccess(PlanNode lookup)
        {
            var join = lookup.Parent;
            while (join != null && !join.IsRoot && join.Category != PlanNodeCategory.Join)
                join = join.Parent;
            if (join == null || join.IsRoot)
                return null;

            return join.DescendantsAndSelf()
                .Where(n => !n.IsObject && n != lookup
                    && (n.Category == PlanNodeCategory.Seek || n.Category == PlanNodeCategory.Scan)
                    && QueryAdvisor.SameTable(n, lookup)
                    && !string.IsNullOrEmpty(n.ObjectIndex)
                    && !n.DescendantsAndSelf().Contains(lookup))
                .FirstOrDefault();
        }

        private void AddCandidate(string ruleId, PlanNode tableNode, IEnumerable<string> keys, IEnumerable<string> includes, double weight)
        {
            AddCandidate(ruleId, tableNode.ObjectDatabase, tableNode.ObjectSchema, tableNode.ObjectTable, keys, includes, weight);
        }

        private void AddCandidate(string ruleId, string database, string schema, string table, IEnumerable<string> keys, IEnumerable<string> includes, double weight)
        {
            var candidate = new IndexCandidate
            {
                RuleId = ruleId,
                Database = PlanNode.Clean(database),
                Schema = PlanNode.Clean(schema),
                Table = PlanNode.Clean(table),
                Statement = _statement,
                Weight = weight,
            };
            candidate.Keys.AddRange(keys.Select(PlanNode.Clean).Where(k => !string.IsNullOrEmpty(k)));
            var keyNames = new HashSet<string>(candidate.KeyNames, StringComparer.OrdinalIgnoreCase);
            candidate.Includes.AddRange(includes.Select(PlanNode.Clean).Where(c => !string.IsNullOrEmpty(c) && !keyNames.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase));
            if (candidate.Keys.Count > 0)
                _candidates.Add(candidate);
        }

        private void AddCoveringCandidate(PlanNode lookup, PlanNode access, List<string> needed, double weight)
        {
            var index = access == null ? null : _server?.Find(lookup)?.Indexes.FirstOrDefault(i => string.Equals(i.Name, PlanNode.Clean(access.ObjectIndex), StringComparison.OrdinalIgnoreCase));
            if (index != null && !index.IsColumnstore && !index.IsClustered)
            {
                AddCandidate("P1", lookup, index.KeyColumns, index.IncludeColumns.Concat(needed), weight);
                return;
            }

            var keys = access?.SeekColumns.Where(c => c.BelongsTo(access)).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            AddCandidate("P1", lookup, keys, needed, weight);
        }

        private string CoveringIndexScript(PlanNode lookup, PlanNode access, List<string> needed)
        {
            var table = _server?.Find(lookup);
            var index = access == null ? null : table?.Indexes.FirstOrDefault(i => string.Equals(i.Name, PlanNode.Clean(access.ObjectIndex), StringComparison.OrdinalIgnoreCase));
            var sb = new StringBuilder();
            sb.Append(Sql.Use(lookup.ObjectDatabase));

            if (index != null && !index.IsColumnstore && !index.IsClustered)
            {
                var keyNames = new HashSet<string>(index.KeyColumnNames, StringComparer.OrdinalIgnoreCase);
                var include = index.IncludeColumns.Concat(needed.Where(c => !keyNames.Contains(c) && !index.IncludeColumns.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
                bool constraint = index.IsPrimaryKey || index.IsUniqueConstraint;
                string name = constraint ? Sql.IndexName(lookup.ObjectTable, index.KeyColumnNames, true) : index.Name;

                sb.AppendLine(constraint
                    ? "-- " + Sql.Quote(index.Name) + " belongs to a constraint: a new covering index with the same keys is created instead."
                    : "-- Rebuilds " + Sql.Quote(index.Name) + " with the same keys and the columns read by the lookup added to INCLUDE.");
                sb.Append("CREATE ").Append(index.IsUnique && !constraint ? "UNIQUE " : string.Empty).Append("NONCLUSTERED INDEX ").Append(Sql.Quote(name)).AppendLine();
                sb.Append("ON ").Append(QueryAdvisor.TableName(lookup)).Append(" (").Append(Sql.ColumnList(index.KeyColumns)).Append(')');
                if (include.Count > 0)
                    sb.AppendLine().Append("INCLUDE (").Append(Sql.ColumnList(include)).Append(')');
                if (!string.IsNullOrEmpty(index.FilterDefinition))
                    sb.AppendLine().Append("WHERE ").Append(index.FilterDefinition);
                if (!constraint)
                    sb.AppendLine().Append("WITH (DROP_EXISTING = ON)");
                sb.Append(';');
                return sb.ToString();
            }

            var keys = access?.SeekColumns.Where(c => c.BelongsTo(access)).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            var includes = needed.Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0)
            {
                sb.AppendLine("-- Draft: add these columns to the INCLUDE list of " + (access != null ? Sql.Quote(access.ObjectIndex) : "the index used by the query") + ":");
                sb.Append("--   ").Append(Sql.ColumnList(includes));
                return sb.ToString();
            }

            sb.AppendLine("-- Draft: the full key of " + (access != null ? Sql.Quote(access.ObjectIndex) : "the index") + " is not known (use \"Read from server\"). Keys below come from the seek predicate.");
            sb.Append("CREATE NONCLUSTERED INDEX ").Append(Sql.Quote(Sql.IndexName(lookup.ObjectTable, keys, includes.Count > 0))).AppendLine();
            sb.Append("ON ").Append(QueryAdvisor.TableName(lookup)).Append(" (").Append(Sql.ColumnList(keys)).Append(')');
            if (includes.Count > 0)
                sb.AppendLine().Append("INCLUDE (").Append(Sql.ColumnList(includes)).Append(')');
            sb.Append(';');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- P2: scan of a large table with a selective predicate

        private void Scans()
        {
            foreach (var scan in _operators.Where(o => o.Category == PlanNodeCategory.Scan && !string.IsNullOrEmpty(o.ObjectTable) && !IsColumnstore(o)))
            {
                double rows = scan.TableCardinality ?? _server?.Find(scan)?.Rows ?? scan.EstimatedRowsRead ?? 0;
                if (rows < AdvisorThresholds.LargeTableRows)
                    continue;

                var predicateColumns = scan.PredicateColumns.Where(c => c.BelongsTo(scan)).ToList();
                if (predicateColumns.Count == 0)
                    continue;

                double returned = RowsPerExecution(scan);
                double selectivity = returned / rows;
                if (selectivity > AdvisorThresholds.SelectivePredicate)
                    continue;

                var severity = CapByStatementCost(scan.CostPercent >= AdvisorThresholds.HighCostPercent ? AdvisorSeverity.High
                    : scan.CostPercent >= AdvisorThresholds.MediumCostPercent ? AdvisorSeverity.Medium
                    : AdvisorSeverity.Info);

                string table = QueryAdvisor.TableName(scan);
                double executions = Executions(scan);
                string what = string.Format(CultureInfo.CurrentCulture,
                    "{0} reads all {1} rows of {2} and keeps {3} {4} ({5}) that match: {6}{7}.",
                    scan.PhysicalOp,
                    QueryAdvisor.Count(rows),
                    table,
                    scan.HasActual ? string.Empty : "an estimated",
                    QueryAdvisor.Count(returned) + " row(s)",
                    QueryAdvisor.Percent(selectivity * 100),
                    scan.Predicate,
                    executions > 1 ? ". This happens " + QueryAdvisor.Count(executions) + " times" : string.Empty).Replace("  ", " ");

                var sargable = predicateColumns.Where(c => !c.Wrapped && c.CompareOp != null && c.CompareOp != "NE").ToList();
                var wrapped = predicateColumns.Where(c => c.Wrapped).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (sargable.Count == 0)
                {
                    var nf = Add("P2", severity, "Access path", "Scan: the condition cannot use an index", scan, scan.CostPercent);
                    nf.Explanation = what + Environment.NewLine + Environment.NewLine
                        + (wrapped.Count > 0
                            ? "Column(s) " + string.Join(", ", wrapped) + " are inside a function, conversion or calculation. SQL Server would have to compute it for every row before comparing, so an index on the column cannot be used to jump to the matching rows."
                              : "The condition (<> / NOT / OR on different columns) cannot be turned into an index range.")
                        + CostNote();
                    nf.Suggestion = "Rewrite the condition so the column stands alone on one side of the comparison, then index it:" + Environment.NewLine
                        + "• YEAR(d) = 2026  →  d >= '20260101' AND d < '20270101'" + Environment.NewLine
                        + "• ISNULL(c, 0) = 1  →  c = 1" + Environment.NewLine
                        + "• CONVERT_IMPLICIT on the column  →  give the parameter / variable the column's data type (varchar vs nvarchar).";
                    continue;
                }

                var equality = sargable.Where(c => c.CompareOp == "EQ").Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var inequality = sargable.Where(c => c.CompareOp != "EQ").Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(c => !equality.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                var keys = equality.Concat(inequality.Take(1)).ToList();
                var read = scan.OutputColumns.Where(c => c.BelongsTo(scan)).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var includes = inequality.Skip(1).Concat(read)
                    .Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool tooWide = includes.Count > AdvisorThresholds.MaxIncludeColumns;
                string keyList = string.Join(", ", keys.Select(Sql.Quote));

                var tableInfo = _server?.Find(scan);
                var existing = tableInfo?.Indexes.FirstOrDefault(i => !i.IsColumnstore && !i.IsDisabled
                    && string.Equals(i.KeyColumnNames.FirstOrDefault(), keys[0], StringComparison.OrdinalIgnoreCase));

                var why = new StringBuilder();
                if (existing != null)
                {
                    why.AppendFormat(CultureInfo.CurrentCulture, "Index {0} starts with {1} but was not used. ", Sql.Quote(existing.Name), Sql.Quote(keys[0]));
                    if (!existing.IsClustered && read.Count > 0)
                        why.AppendFormat(CultureInfo.CurrentCulture, "It does not contain the {0} column(s) the query reads, so every row found would need a Key Lookup; for about {1} row(s) the optimizer estimated that reading the whole table is cheaper. ", read.Count, QueryAdvisor.Count(returned));
                    if (wrapped.Count > 0)
                        why.Append("Column(s) ").Append(string.Join(", ", wrapped)).Append(" are inside a function or conversion. ");
                }
                else if (tableInfo != null)
                {
                    why.AppendFormat(CultureInfo.CurrentCulture, "There is no index whose first key column is {0}, so SQL Server cannot jump to the matching rows and has to read the whole table and test every row. ", Sql.Quote(keys[0]));
                    var other = tableInfo.Indexes.Where(i => !i.IsColumnstore && i.KeyColumnNames.Skip(1).Contains(keys[0], StringComparer.OrdinalIgnoreCase)).Select(i => Sql.Quote(i.Name)).ToList();
                    if (other.Count > 0)
                        why.Append("Index(es) ").Append(string.Join(", ", other)).Append(" contain the column, but not as the first key column, which is the only one an index seek can start from. ");
                }
                else
                {
                    why.AppendFormat(CultureInfo.CurrentCulture, "No index with {0} as its first key column was used, so SQL Server read the whole table and tested every row. Use \"Read from server\" to check the existing indexes. ", Sql.Quote(keys[0]));
                }
                if (_statement.MissingIndexes.Any(m => string.Equals(PlanNode.Clean(m.Table), PlanNode.Clean(scan.ObjectTable), StringComparison.OrdinalIgnoreCase)))
                    why.Append("The optimizer also reports a missing index on this table (P6). ");

                var f = Add("P2", severity, "Access path", "Scan of a large table with a selective condition", scan, scan.CostPercent);
                f.Explanation = what + Environment.NewLine + Environment.NewLine + why.ToString().TrimEnd() + CostNote();

                var suggestion = new StringBuilder();
                suggestion.AppendFormat(CultureInfo.CurrentCulture, "Create an index on {0}", keyList);
                if (keys.Count > 1)
                    suggestion.Append(" (equality columns first, then the range column)");
                suggestion.AppendFormat(CultureInfo.CurrentCulture, ". SQL Server can then seek to the matching rows and read about {0} row(s) instead of {1}.", QueryAdvisor.Count(Math.Max(1, returned)), QueryAdvisor.Count(rows));
                if (tooWide)
                {
                    suggestion.AppendLine().AppendLine()
                        .AppendFormat(CultureInfo.CurrentCulture, "The query reads {0} columns{1}, too many to include in the index: each row found will need a Key Lookup. That is fine while the condition returns few rows; if it can return many, list only the columns you need and add them to INCLUDE.",
                            read.Count, System.Text.RegularExpressions.Regex.IsMatch(_statement.Text ?? string.Empty, @"select\s+(top\s*\(?\s*\d+\s*\)?\s+)?(\w+\.)?\*", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? " (the query uses SELECT *)" : string.Empty);
                }
                else if (includes.Count > 0)
                {
                    suggestion.AppendLine().AppendLine().Append("The other columns the query reads are in INCLUDE, so the index covers the query and no Key Lookup is needed.");
                }
                if (existing != null && !existing.IsClustered)
                    suggestion.AppendLine().AppendLine().AppendFormat(CultureInfo.CurrentCulture, "Alternatively add the columns to the INCLUDE list of {0} instead of creating a new index.", Sql.Quote(existing.Name));
                f.Suggestion = suggestion.ToString();

                var sb = new StringBuilder();
                sb.Append(Sql.Use(scan.ObjectDatabase));
                sb.Append("CREATE NONCLUSTERED INDEX ").Append(Sql.Quote(Sql.IndexName(scan.ObjectTable, keys, !tooWide && includes.Count > 0))).AppendLine();
                sb.Append("ON ").Append(table).Append(" (").Append(Sql.ColumnList(keys)).Append(')');
                if (!tooWide && includes.Count > 0)
                    sb.AppendLine().Append("INCLUDE (").Append(Sql.ColumnList(includes)).Append(')');
                sb.Append(';');
                f.Script = sb.ToString();
                AddCandidate("P2", scan, keys, tooWide ? Enumerable.Empty<string>() : includes, f.Weight);
            }
        }

        /// <summary>Index suggestions on cheap statements matter only for queries that run very often.</summary>
        private AdvisorSeverity CapByStatementCost(AdvisorSeverity severity)
        {
            if (_statement.SubtreeCost < AdvisorThresholds.TrivialStatementCost)
                return AdvisorSeverity.Info;
            if (_statement.SubtreeCost < AdvisorThresholds.SmallStatementCost && severity == AdvisorSeverity.High)
                return AdvisorSeverity.Medium;
            return severity;
        }

        private string CostNote()
        {
            if (_statement.SubtreeCost >= AdvisorThresholds.SmallStatementCost)
                return string.Empty;
            return Environment.NewLine + Environment.NewLine + string.Format(CultureInfo.CurrentCulture,
                "The whole statement is cheap (estimated cost {0:0.###}), so this matters mainly if the query runs very often or the table keeps growing.", _statement.SubtreeCost);
        }

        // ---------------------------------------------------------------- P3: wrong row estimates (actual plans)

        private void BadEstimates()
        {
            // Report where the error starts, not every operator above it.
            foreach (var node in _operators.Where(QueryAdvisor.IsEstimateOrigin))
            {

                double ratio = node.RowEstimateRatio ?? 1;
                bool severe = ratio >= AdvisorThresholds.SevereEstimateRatio || ratio <= 1 / AdvisorThresholds.SevereEstimateRatio;
                double share = _statement.SubtreeCost > 0 ? node.SubtreeCost / _statement.SubtreeCost : 0;
                var severity = severe || share >= 0.25 ? AdvisorSeverity.High : AdvisorSeverity.Medium;

                var tables = node.DescendantsAndSelf().Where(n => !n.IsObject && !string.IsNullOrEmpty(n.ObjectTable))
                    .GroupBy(n => AdvisorServerInfo.Key(n.ObjectDatabase, n.ObjectSchema, n.ObjectTable), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                var f = Add("P3", severity, "Statistics", "Row estimate is wrong", node, 30 + Math.Min(50, Math.Abs(Math.Log10(ratio)) * 10));
                f.Explanation = string.Format(CultureInfo.CurrentCulture,
                    "Estimated {0} row(s), actual {1} ({2:0.#}x {3}). Wrong estimates lead to the wrong join type, the wrong index, too small memory grants (spills) or too large ones.",
                    QueryAdvisor.Count(node.EstimateRowsAllExecutions),
                    QueryAdvisor.Count(node.ActualRows),
                    ratio >= 1 ? ratio : 1 / ratio,
                    ratio >= 1 ? "more than estimated" : "fewer than estimated");

                var hints = new List<string> { "Update the statistics of the table(s) involved (see the Statistics findings)." };
                if (tables.Any(t => (PlanNode.Clean(t.ObjectTable) ?? string.Empty).StartsWith("@", StringComparison.Ordinal)))
                    hints.Add("A table variable is involved: it has no statistics. Use a #temp table, or OPTION (RECOMPILE).");
                if (node.PredicateColumns.Where(c => c.BelongsTo(node)).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    hints.Add("The predicate uses several columns of the same table; if they are correlated, multi-column statistics (CREATE STATISTICS ... (col1, col2)) or a filtered statistic can help.");
                if (node.PredicateColumns.Any(c => c.Wrapped && c.BelongsTo(node)))
                    hints.Add("A column is inside a function or conversion: the optimizer cannot use its histogram. Rewrite the condition.");
                if (_statement.Parameters.Any(p => p.RuntimeValue != null && p.CompiledValue != p.RuntimeValue))
                    hints.Add("The plan was compiled for other parameter values (see P9).");
                f.Suggestion = string.Join(" ", hints);

                var scripts = tables.Where(t => !new PlanTable { Database = t.ObjectDatabase, Table = t.ObjectTable }.IsTemporary)
                    .Select(t => QueryAdvisor.UpdateStatistics(t.ObjectDatabase, t.ObjectSchema, t.ObjectTable, null, t.TableCardinality ?? _server?.Find(t)?.Rows))
                    .ToList();
                if (scripts.Count > 0)
                    f.Script = string.Join(Environment.NewLine + Environment.NewLine, scripts);
            }
        }

        // ---------------------------------------------------------------- P4: spills to tempdb

        private void Spills()
        {
            foreach (var node in _operators.Where(o => o.HasSpill))
            {
                var severity = node.SpillLevel >= 2 || (node.HasActual && node.CostPercent >= AdvisorThresholds.LookupCostPercent) ? AdvisorSeverity.High : AdvisorSeverity.Medium;
                var f = Add("P4", severity, "Memory", node.PhysicalOp + " spilled to tempdb", node, 20 + node.CostPercent);
                f.Explanation = "The operator did not get enough memory and wrote intermediate data to tempdb" + (node.SpillLevel > 0 ? " (spill level " + node.SpillLevel + ")" : string.Empty)
                    + ". The memory grant is computed from the row estimate, so spills usually follow an underestimate.";

                var hints = new List<string>();
                if (node.DescendantsAndSelf().Any(d => !d.IsObject && d.BadRowEstimate))
                    hints.Add("Fix the row estimate below this operator first (P3 / statistics).");
                if (node.Category == PlanNodeCategory.Sort)
                    hints.Add("An index that already returns the rows in the ORDER BY / GROUP BY order removes the sort.");
                else
                    hints.Add("Join or aggregate on fewer rows (filter earlier), or provide indexes so a merge / nested loops join can be used.");
                hints.Add("Select only the columns you need: wide rows need more memory.");
                f.Suggestion = string.Join(" ", hints);
            }
        }

        // ---------------------------------------------------------------- P5: implicit conversions

        private void ImplicitConversions()
        {
            bool predicateConvert = _operators.Any(o => (o.Predicate ?? string.Empty).IndexOf("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase) >= 0
                || (o.SeekPredicate ?? string.Empty).IndexOf("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var group in _statement.Converts.GroupBy(c => (c.Issue ?? string.Empty) + "|" + c.Expression))
            {
                var convert = group.First();
                bool seek = (convert.Issue ?? string.Empty).IndexOf("Seek", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!seek && !predicateConvert)
                    continue; // conversions in the SELECT list only: harmless

                var node = _operators.Where(o => (o.Predicate ?? string.Empty).IndexOf("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(o => o.CostPercent)
                    .FirstOrDefault();

                var f = Add("P5", seek ? AdvisorSeverity.High : AdvisorSeverity.Info, "Query", seek ? "Implicit conversion prevents an index seek" : "Implicit conversion may affect the row estimate", node, seek ? 40 : 1);
                f.Object = convert.Expression;
                f.Explanation = "SQL Server converts a value at run time: " + convert.Expression + ". "
                    + (seek ? "When the column itself is converted, an index on it can only be scanned, not sought." : "The conversion makes the row estimate less accurate.");
                var parameters = _statement.Parameters.Where(p => !string.IsNullOrEmpty(p.DataType)).Select(p => p.Name + " " + p.DataType).ToList();
                f.Suggestion = "Give parameters, variables and literals the same data type as the column (typical case: nvarchar parameter / N'...' literal against a varchar column), and join columns of the same type."
                    + (parameters.Count > 0 ? " Parameters of the statement: " + string.Join(", ", parameters) + "." : string.Empty);
            }
        }

        // ---------------------------------------------------------------- P6: missing indexes reported by the optimizer

        private void MissingIndexes()
        {
            foreach (var mi in _statement.MissingIndexes)
            {
                var severity = mi.Impact >= AdvisorThresholds.MissingIndexHighImpact && _statement.SubtreeCost >= AdvisorThresholds.MissingIndexHighStatementCost ? AdvisorSeverity.High
                    : mi.Impact >= AdvisorThresholds.MissingIndexMediumImpact ? AdvisorSeverity.Medium
                    : AdvisorSeverity.Info;

                var node = _operators.Where(o => string.Equals(PlanNode.Clean(o.ObjectTable), PlanNode.Clean(mi.Table), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(PlanNode.Clean(o.ObjectSchema), PlanNode.Clean(mi.Schema), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(o => o.CostPercent)
                    .FirstOrDefault();

                var f = Add("P6", severity, "Index", "Missing index suggested by the optimizer", node, mi.Impact);
                f.Object = Sql.Name(mi.Schema, mi.Table);
                var explanation = new StringBuilder();
                explanation.AppendFormat(CultureInfo.CurrentCulture, "The optimizer estimates that this index would lower the cost of the statement by {0}.", QueryAdvisor.Percent(mi.Impact));
                explanation.Append(" Its suggestion only looks at this statement, ignores existing indexes and does not order the key columns by selectivity.");

                var first = mi.EqualityColumns.Concat(mi.InequalityColumns).Select(PlanNode.Clean).FirstOrDefault();
                var similar = _server?.Find(mi.Database, mi.Schema, mi.Table)?.Indexes
                    .Where(i => !i.IsColumnstore && string.Equals(i.KeyColumnNames.FirstOrDefault(), first, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (similar != null && similar.Count > 0)
                    explanation.Append(" Existing index(es) with the same leading column: ").Append(string.Join(", ", similar.Select(i => Sql.Quote(i.Name)))).Append(" - extending one of them may be better than a new index.");
                f.Explanation = explanation.ToString();
                f.Suggestion = "Review the index (key order, included columns, overlap with existing indexes, write overhead) before creating it.";
                f.Script = mi.ToCreateIndex();
                AddCandidate("P6", mi.Database, mi.Schema, mi.Table, mi.EqualityColumns.Concat(mi.InequalityColumns), mi.IncludeColumns, f.Weight);
            }
        }

        // ---------------------------------------------------------------- P7: join without predicate

        private void NoJoinPredicate()
        {
            foreach (var node in _operators.Where(o => o.NoJoinPredicate))
            {
                var f = Add("P7", AdvisorSeverity.High, "Query", "Join without a join predicate", node, 60 + node.CostPercent);
                f.Explanation = "Every row of one input is combined with every row of the other (cartesian product). This is usually a missing or wrong ON / WHERE condition.";
                f.Suggestion = "Check the join conditions of the tables under this operator. If the cross join is intended, make it explicit with CROSS JOIN.";
            }
        }

        // ---------------------------------------------------------------- P8: no statistics, memory grant

        private void NoStatistics()
        {
            foreach (var node in _operators.Where(o => o.ColumnsWithNoStatistics.Count > 0))
            {
                var columns = node.ColumnsWithNoStatistics.Where(c => !string.IsNullOrEmpty(c.Column)).ToList();
                var f = Add("P8", AdvisorSeverity.Medium, "Statistics", "Columns without statistics", node, 15);
                f.Object = string.Join(", ", columns.Select(c => c.ToString()));
                f.Explanation = "The optimizer had no statistics for " + f.Object + " and had to guess the number of rows. This happens when AUTO_CREATE_STATISTICS is off for the database, or the database is read-only.";
                f.Suggestion = "Turn AUTO_CREATE_STATISTICS on, or create the statistics manually.";

                var sb = new StringBuilder();
                var firstWithTable = columns.FirstOrDefault(c => !string.IsNullOrEmpty(c.Database));
                sb.Append(Sql.Use(firstWithTable?.Database));
                sb.AppendLine("-- SELECT name, is_auto_create_stats_on FROM sys.databases WHERE name = DB_NAME();");
                foreach (var c in columns.Where(c => !string.IsNullOrEmpty(c.Table)).GroupBy(c => c.ToString(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    sb.Append("CREATE STATISTICS ").Append(Sql.Quote("st_" + PlanNode.Clean(c.Table) + "_" + c.Name))
                      .Append(" ON ").Append(Sql.Name(c.Schema, c.Table)).Append(" (").Append(Sql.Quote(c.Column)).AppendLine(") WITH FULLSCAN;");
                }
                f.Script = sb.ToString().TrimEnd();
            }
        }

        private void MemoryGrant()
        {
            if (!string.IsNullOrEmpty(_statement.MemoryGrantWarning))
            {
                bool tooSmall = _statement.MemoryGrantWarning.IndexOf("More Than", StringComparison.OrdinalIgnoreCase) >= 0
                    || _statement.MemoryGrantWarning.IndexOf("Increase", StringComparison.OrdinalIgnoreCase) >= 0;
                var f = Add("P8", tooSmall ? AdvisorSeverity.Medium : AdvisorSeverity.Info, "Memory", "Memory grant warning: " + _statement.MemoryGrantWarning, null, 5);
                f.Explanation = MemoryText();
                f.Suggestion = tooSmall
                    ? "The query needed more memory than estimated: fix the row estimates (statistics) of the sorts / hashes."
                    : "The query reserved much more memory than it used, which can make other queries wait (RESOURCE_SEMAPHORE). Fix overestimated rows, and avoid wide or (n)varchar(max) columns in sorts and hashes.";
                return;
            }

            if (_statement.GrantedMemoryKb.HasValue && _statement.MaxUsedMemoryKb.HasValue
                && _statement.GrantedMemoryKb.Value >= AdvisorThresholds.ExcessiveGrantKb
                && _statement.MaxUsedMemoryKb.Value < _statement.GrantedMemoryKb.Value * AdvisorThresholds.ExcessiveGrantUsedShare)
            {
                var f = Add("P8", AdvisorSeverity.Info, "Memory", "Memory grant much larger than used", null, 5);
                f.Explanation = MemoryText();
                f.Suggestion = "Large unused grants reduce the memory available to other queries. They come from overestimated rows or wide columns in sorts / hashes.";
            }
        }

        private string MemoryText()
        {
            return string.Format(CultureInfo.CurrentCulture, "Requested {0} KB, granted {1} KB, used {2} KB.",
                _statement.RequestedMemoryKb.HasValue ? QueryAdvisor.Count(_statement.RequestedMemoryKb.Value) : "?",
                _statement.GrantedMemoryKb.HasValue ? QueryAdvisor.Count(_statement.GrantedMemoryKb.Value) : "?",
                _statement.MaxUsedMemoryKb.HasValue ? QueryAdvisor.Count(_statement.MaxUsedMemoryKb.Value) : "?");
        }

        // ---------------------------------------------------------------- P9: parameter sniffing

        private void ParameterSniffing()
        {
            var different = _statement.Parameters.Where(p => p.RuntimeValue != null && p.CompiledValue != null && p.CompiledValue != p.RuntimeValue).ToList();
            if (different.Count == 0 || !_operators.Any(o => o.BadRowEstimate))
                return;

            var f = Add("P9", AdvisorSeverity.Medium, "Query", "Plan compiled for other parameter values", null, 25);
            f.Explanation = "The plan was compiled for " + string.Join(", ", different.Select(p => p.Name + " = " + p.CompiledValue))
                + " but ran with " + string.Join(", ", different.Select(p => p.Name + " = " + p.RuntimeValue))
                + ", and some row estimates are wrong. A plan that is good for one value can be bad for another (parameter sniffing).";
            f.Suggestion = "If the best plan depends on the value: OPTION (RECOMPILE) for queries that do not run very often, OPTIMIZE FOR (@p = typical value / UNKNOWN), "
                + "or separate procedures for the very different cases. On SQL Server 2022 with compatibility level 160, Parameter Sensitive Plan optimization can keep several plans.";
        }
    }
}
