namespace SSMSPlus.Features.Advisor
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Annotated query (DB2 "Query Annotation"): the statement text with a comment after every table reference,
    /// saying how many rows the table has, how the plan reads it (operator, index, share of the cost), the estimated
    /// and actual rows, the age of its statistics and the advisor findings about it.
    /// </summary>
    public static class QueryAnnotator
    {
        public static string Annotate(PlanDocument document, AdvisorServerInfo server, IList<AdvisorFinding> findings)
        {
            var sb = new StringBuilder();
            if (document == null || document.Statements.Count == 0)
                return string.Empty;

            double total = document.Statements.Sum(s => Math.Max(0, s.SubtreeCost));
            foreach (var statement in document.Statements)
            {
                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();
                sb.AppendFormat(CultureInfo.CurrentCulture, "-- ===== Statement {0}: cost {1:0.###}{2}",
                    statement.Index, statement.SubtreeCost,
                    total > 0 ? string.Format(CultureInfo.CurrentCulture, " ({0:0.#}% of the batch)", statement.SubtreeCost * 100 / total) : string.Empty);
                if (statement.HasActual)
                    sb.AppendFormat(CultureInfo.CurrentCulture, ", {0} row(s) returned", QueryAdvisor.Count(statement.Root?.ActualRows ?? 0));
                var statementFindings = findings?.Where(f => f.Statement == statement).ToList() ?? new List<AdvisorFinding>();
                if (statementFindings.Count > 0)
                    sb.Append(", advisor: ").Append(QueryAdvisor.Summary(statementFindings));
                sb.AppendLine();
                sb.Append(AnnotateStatement(statement, server, statementFindings));
            }

            return sb.ToString();
        }

        private static string AnnotateStatement(PlanStatement statement, AdvisorServerInfo server, List<AdvisorFinding> findings)
        {
            string text = statement.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return "-- (the plan has no statement text)";

            TSqlFragment fragment;
            IList<ParseError> errors;
            try
            {
                using (var reader = new StringReader(text))
                    fragment = new TSql170Parser(true).Parse(reader, out errors);
            }
            catch
            {
                fragment = null;
                errors = null;
            }

            var operators = statement.Operators().Where(o => !string.IsNullOrEmpty(o.ObjectTable)).ToList();
            var inserts = new List<KeyValuePair<int, string>>();
            var shown = new HashSet<PlanNode>();
            if (fragment != null && (errors == null || errors.Count == 0))
            {
                var collector = new TableCollector();
                fragment.Accept(collector);
                foreach (var reference in collector.Tables)
                {
                    var nodes = Match(reference, operators);
                    shown.UnionWith(nodes);
                    string note = Note(reference, nodes, server, findings, statement);
                    if (note != null)
                        inserts.Add(new KeyValuePair<int, string>(reference.StartOffset + reference.FragmentLength, " /* " + note + " */"));
                }
            }

            var sb = new StringBuilder(text);
            foreach (var insert in inserts.OrderByDescending(i => i.Key))
                sb.Insert(insert.Key, insert.Value);

            // Tables the text does not show (views expanded, statements of procedures...): list them after the text.
            var others = operators.Where(o => !shown.Contains(o))
                .GroupBy(o => AdvisorServerInfo.Key(o.ObjectDatabase, o.ObjectSchema, o.ObjectTable), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (others.Count > 0)
            {
                sb.AppendLine().AppendLine().Append("-- Also read by the plan:");
                foreach (var g in others)
                {
                    var first = g.First();
                    sb.AppendLine().Append("--   ").Append(Sql.Name(first.ObjectSchema, first.ObjectTable)).Append(": ")
                      .Append(Describe(g.ToList(), server?.Find(first), first, statement, findings));
                }
            }

            return sb.ToString();
        }

        private static List<PlanNode> Match(NamedTableReference reference, List<PlanNode> operators)
        {
            var name = reference.SchemaObject?.BaseIdentifier?.Value;
            var schema = reference.SchemaObject?.SchemaIdentifier?.Value;
            var alias = reference.Alias?.Value;
            if (string.IsNullOrEmpty(name))
                return new List<PlanNode>();

            var nodes = operators.Where(o => string.Equals(PlanNode.Clean(o.ObjectTable), name, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(schema) || string.Equals(PlanNode.Clean(o.ObjectSchema), schema, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // With an alias, the operators that use it (the plan keeps the alias of each table reference).
            var byAlias = nodes.Where(o => string.Equals(PlanNode.Clean(o.ObjectAlias), alias ?? name, StringComparison.OrdinalIgnoreCase)
                || (alias == null && string.IsNullOrEmpty(o.ObjectAlias))).ToList();
            if (byAlias.Count > 0)
                nodes = byAlias;

            return nodes;
        }

        private static string Note(NamedTableReference reference, List<PlanNode> nodes, AdvisorServerInfo server, List<AdvisorFinding> findings, PlanStatement statement)
        {
            if (nodes.Count == 0)
            {
                var name = reference.SchemaObject?.BaseIdentifier?.Value ?? string.Empty;
                return name.StartsWith("#", StringComparison.Ordinal) || name.StartsWith("@", StringComparison.Ordinal) ? null : "not read by this plan";
            }

            var first = nodes[0];
            return Describe(nodes, server?.Find(first), first, statement, findings);
        }

        private static string Describe(List<PlanNode> nodes, ServerTableInfo info, PlanNode first, PlanStatement statement, List<AdvisorFinding> findings)
        {
            var parts = new List<string>();
            double? rows = info?.Rows ?? nodes.Select(n => n.TableCardinality).FirstOrDefault(r => r.HasValue);
            if (rows.HasValue)
                parts.Add(QueryAdvisor.Count(rows.Value) + " rows");

            foreach (var n in nodes.OrderBy(n => n.NodeId).Take(3))
            {
                var access = new StringBuilder();
                access.Append(n.PhysicalOp);
                if (!string.IsNullOrEmpty(n.ObjectIndex))
                    access.Append(' ').Append(PlanNode.Clean(n.ObjectIndex));
                access.AppendFormat(CultureInfo.CurrentCulture, " ({0}%)", n.CostPercent.ToString("0.#", CultureInfo.CurrentCulture));
                access.Append(", est ").Append(QueryAdvisor.Count(n.EstimateRowsAllExecutions));
                if (n.HasActual)
                {
                    access.Append(" -> act ").Append(QueryAdvisor.Count(n.ActualRows));
                    if (n.ActualExecutions > 1)
                        access.Append(" in ").Append(QueryAdvisor.Count(n.ActualExecutions)).Append(" executions");
                    if (n.BadRowEstimate)
                        access.Append(" (!)");
                }
                parts.Add(access.ToString());
            }
            if (nodes.Count > 3)
                parts.Add("+" + (nodes.Count - 3) + " more operator(s)");

            var stats = info?.Statistics.Where(s => s.LastUpdated.HasValue).ToList();
            if (stats != null && stats.Count > 0)
            {
                var oldest = stats.Min(s => s.LastUpdated.Value);
                var worst = stats.Where(s => s.Rows.HasValue && s.Rows.Value > 0 && s.ModificationCounter.HasValue)
                    .Select(s => s.ModificationCounter.Value * 100.0 / s.Rows.Value)
                    .DefaultIfEmpty(0).Max();
                parts.Add(string.Format(CultureInfo.CurrentCulture, "stats from {0:yyyy-MM-dd}{1}", oldest, worst >= 1 ? string.Format(CultureInfo.CurrentCulture, ", up to {0:0}% modified", worst) : string.Empty));
            }
            else
            {
                var usage = statement.StatisticsUsage.Where(u => string.Equals(PlanNode.Clean(u.Table), PlanNode.Clean(first.ObjectTable), StringComparison.OrdinalIgnoreCase) && u.LastUpdate.HasValue).ToList();
                if (usage.Count > 0)
                    parts.Add(string.Format(CultureInfo.CurrentCulture, "stats from {0:yyyy-MM-dd}", usage.Min(u => u.LastUpdate.Value)));
            }

            var rules = findings.Where(f => f.Node != null && nodes.Contains(f.Node)
                    || (f.Object != null && f.RuleId == "S1" && f.Object == Sql.Name(first.ObjectSchema, first.ObjectTable)))
                .Select(f => f.RuleId)
                .Distinct()
                .ToList();
            if (rules.Count > 0)
                parts.Add("see " + string.Join(", ", rules));

            return string.Join(" | ", parts).Replace("*/", "* /");
        }

        private sealed class TableCollector : TSqlFragmentVisitor
        {
            public List<NamedTableReference> Tables { get; } = new List<NamedTableReference>();

            public override void ExplicitVisit(NamedTableReference node)
            {
                Tables.Add(node);
                base.ExplicitVisit(node);
            }
        }
    }
}
