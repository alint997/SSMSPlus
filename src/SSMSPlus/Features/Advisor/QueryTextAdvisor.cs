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
    /// Query Advisor, text rules T1-T10 (DB2 "Query Advisor" style): patterns in the SQL text that prevent good plans
    /// or give wrong results, with a suggested rewrite where one can be generated. Works on the statement text of
    /// each plan statement, parsed with ScriptDom; statements that do not parse are skipped.
    /// </summary>
    internal static class QueryTextAdvisor
    {
        public static void Run(PlanStatement statement, List<AdvisorFinding> findings)
        {
            if (string.IsNullOrWhiteSpace(statement?.Text))
                return;

            TSqlFragment fragment;
            try
            {
                var parser = SqlParsers.Create();
                IList<ParseError> errors;
                using (var reader = new StringReader(statement.Text))
                    fragment = parser.Parse(reader, out errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                    return; // partial text (for example a statement of a procedure that needs its context)
            }
            catch
            {
                return;
            }

            var visitor = new TextRuleVisitor(statement, statement.Text);
            fragment.Accept(visitor);
            findings.AddRange(visitor.Findings);
        }
    }

    /// <summary>Walks the statement and collects the text findings.</summary>
    internal sealed class TextRuleVisitor : TSqlFragmentVisitor
    {
        private readonly PlanStatement _statement;
        private readonly string _text;
        private readonly HashSet<string> _reportedOnce = new HashSet<string>();
        private int _existsDepth;
        private QueryExpression _topQuery;

        public TextRuleVisitor(PlanStatement statement, string text)
        {
            _statement = statement;
            _text = text;
        }

        public List<AdvisorFinding> Findings { get; } = new List<AdvisorFinding>();

        // ------------------------------------------------------------------ helpers

        private string TextOf(TSqlFragment f)
        {
            if (f == null || f.StartOffset < 0 || f.FragmentLength <= 0 || f.StartOffset + f.FragmentLength > _text.Length)
                return string.Empty;
            return _text.Substring(f.StartOffset, f.FragmentLength);
        }

        private static string Short(string s)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            while (s.Contains("  "))
                s = s.Replace("  ", " ");
            s = s.Trim();
            return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
        }

        private AdvisorFinding Add(string rule, AdvisorSeverity severity, string title, TSqlFragment where, double weight)
        {
            var f = new AdvisorFinding
            {
                RuleId = rule,
                Severity = severity,
                Category = "Query",
                Title = title,
                Object = Short(TextOf(where)),
                Statement = _statement,
                Node = _statement.Root,
                Weight = weight,
            };
            Findings.Add(f);
            return f;
        }

        private static List<ColumnReferenceExpression> Columns(TSqlFragment f)
        {
            var collector = new ColumnCollector();
            f?.Accept(collector);
            return collector.Columns;
        }

        private static string ColumnKey(ColumnReferenceExpression c)
        {
            var ids = c.MultiPartIdentifier?.Identifiers;
            return ids == null || ids.Count == 0 ? string.Empty : ids[ids.Count - 1].Value.ToLowerInvariant();
        }

        private static ScalarExpression Unwrap(ScalarExpression e)
        {
            while (e is ParenthesisExpression p)
                e = p.Expression;
            return e;
        }

        private static string FunctionName(ScalarExpression e)
        {
            return (Unwrap(e) as FunctionCall)?.FunctionName?.Value?.ToUpperInvariant();
        }

        private static string Operator(BooleanComparisonType t)
        {
            switch (t)
            {
                case BooleanComparisonType.Equals: return "=";
                case BooleanComparisonType.NotEqualToBrackets: return "<>";
                case BooleanComparisonType.NotEqualToExclamation: return "!=";
                case BooleanComparisonType.GreaterThan: return ">";
                case BooleanComparisonType.GreaterThanOrEqualTo: return ">=";
                case BooleanComparisonType.LessThan: return "<";
                case BooleanComparisonType.LessThanOrEqualTo: return "<=";
                default: return t.ToString();
            }
        }

        // ------------------------------------------------------------------ predicates: WHERE, ON, HAVING

        public override void ExplicitVisit(WhereClause node)
        {
            if (node.SearchCondition != null)
            {
                CheckPredicates(node.SearchCondition);
                CheckOr(node.SearchCondition);
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            if (node.SearchCondition != null)
                CheckPredicates(node.SearchCondition);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(HavingClause node)
        {
            if (node.SearchCondition != null)
                CheckPredicates(node.SearchCondition);
            base.ExplicitVisit(node);
        }

        private void CheckPredicates(BooleanExpression condition)
        {
            var predicates = new PredicateCollector();
            condition.Accept(predicates);

            foreach (var comparison in predicates.Comparisons)
            {
                CheckNullComparison(comparison);
                CheckWrappedColumn(comparison);
            }

            foreach (var like in predicates.Likes)
                CheckLeadingWildcard(like);

            foreach (var inPredicate in predicates.Ins)
                CheckNotIn(inPredicate);
        }

        // T10: col = NULL / col <> NULL
        private void CheckNullComparison(BooleanComparisonExpression c)
        {
            bool leftNull = Unwrap(c.FirstExpression) is NullLiteral;
            bool rightNull = Unwrap(c.SecondExpression) is NullLiteral;
            if (!leftNull && !rightNull)
                return;

            bool equals = c.ComparisonType == BooleanComparisonType.Equals;
            bool notEquals = c.ComparisonType == BooleanComparisonType.NotEqualToBrackets || c.ComparisonType == BooleanComparisonType.NotEqualToExclamation;
            if (!equals && !notEquals)
                return;

            var other = leftNull ? c.SecondExpression : c.FirstExpression;
            var f = Add("T10", AdvisorSeverity.High, "Comparison with NULL using " + (equals ? "=" : "<>"), c, 70);
            f.Explanation = "A comparison with NULL is never true (it is UNKNOWN), so this condition never matches any row, whatever the data. "
                + "With ANSI_NULLS OFF it behaves differently, which makes the query depend on a session setting.";
            f.Suggestion = "Use IS NULL / IS NOT NULL.";
            f.Script = "-- instead of: " + Short(TextOf(c)) + Environment.NewLine + TextOf(other) + (equals ? " IS NULL" : " IS NOT NULL");
        }

        // T1: function / calculation on a column
        private void CheckWrappedColumn(BooleanComparisonExpression c)
        {
            var left = Unwrap(c.FirstExpression);
            var right = Unwrap(c.SecondExpression);
            bool leftWrapped = IsWrappedColumn(left);
            bool rightWrapped = IsWrappedColumn(right);
            if (!leftWrapped && !rightWrapped)
                return;

            var wrapped = leftWrapped ? left : right;
            var other = leftWrapped ? right : left;
            var op = c.ComparisonType;
            if (!leftWrapped)
                op = Mirror(op);

            string rewrite = Rewrite(wrapped, op, other);
            var columns = string.Join(", ", Columns(wrapped).Select(TextOf).Distinct());
            var f = Add("T1", AdvisorSeverity.Medium, "Function or calculation on a column in a condition", c, 40);
            f.Explanation = "The column " + columns + " is inside " + Describe(wrapped) + ". SQL Server has to compute the expression for every row "
                + "before comparing, so an index on the column cannot be used to seek, and the statistics of the column cannot be used for the row estimate.";
            f.Suggestion = rewrite != null
                ? "Rewrite the condition so the column stands alone on one side of the comparison (equivalent condition below)."
                : "Rewrite the condition so the column stands alone on one side of the comparison: move the calculation to the other side, or compare with a range instead of a function result.";
            if (rewrite != null)
                f.Script = "-- instead of: " + Short(TextOf(c)) + Environment.NewLine + rewrite;
        }

        private bool IsWrappedColumn(ScalarExpression e)
        {
            if (e is ColumnReferenceExpression || e == null)
                return false;
            if (!(e is FunctionCall || e is ConvertCall || e is CastCall || e is TryConvertCall || e is TryCastCall
                || e is BinaryExpression || e is UnaryExpression || e is CoalesceExpression || e is IIfCall
                || e is SearchedCaseExpression || e is SimpleCaseExpression || e is NullIfExpression || e is LeftFunctionCall || e is RightFunctionCall))
                return false;
            if (Columns(e).Count == 0)
                return false;

            // CAST / CONVERT of a column to date is sargable (SQL Server turns it into a range).
            if (e is CastCall cast && Unwrap(cast.Parameter) is ColumnReferenceExpression && IsDateType(cast.DataType))
                return false;
            if (e is ConvertCall convert && Unwrap(convert.Parameter) is ColumnReferenceExpression && IsDateType(convert.DataType))
                return false;
            return true;
        }

        private static bool IsDateType(DataTypeReference t)
        {
            return (t as SqlDataTypeReference)?.SqlDataTypeOption == SqlDataTypeOption.Date;
        }

        private static BooleanComparisonType Mirror(BooleanComparisonType t)
        {
            switch (t)
            {
                case BooleanComparisonType.GreaterThan: return BooleanComparisonType.LessThan;
                case BooleanComparisonType.GreaterThanOrEqualTo: return BooleanComparisonType.LessThanOrEqualTo;
                case BooleanComparisonType.LessThan: return BooleanComparisonType.GreaterThan;
                case BooleanComparisonType.LessThanOrEqualTo: return BooleanComparisonType.GreaterThanOrEqualTo;
                default: return t;
            }
        }

        private string Describe(ScalarExpression e)
        {
            if (e is FunctionCall fc)
                return "the function " + fc.FunctionName.Value.ToUpperInvariant() + "()";
            if (e is ConvertCall || e is CastCall || e is TryConvertCall || e is TryCastCall)
                return "a conversion";
            if (e is BinaryExpression)
                return "a calculation";
            if (e is CoalesceExpression)
                return "COALESCE()";
            if (e is LeftFunctionCall)
                return "the function LEFT()";
            if (e is RightFunctionCall)
                return "the function RIGHT()";
            return "an expression";
        }

        /// <summary>Equivalent sargable condition for common cases, or null.</summary>
        private string Rewrite(ScalarExpression wrapped, BooleanComparisonType op, ScalarExpression other)
        {
            string value = TextOf(other);
            bool otherHasColumns = Columns(other).Count > 0;

            // YEAR(col) = 2026  ->  col >= '20260101' AND col < '20270101'
            if (wrapped is FunctionCall fc && fc.Parameters.Count == 1 && Unwrap(fc.Parameters[0]) is ColumnReferenceExpression col && !otherHasColumns)
            {
                string column = TextOf(col);
                string name = fc.FunctionName.Value.ToUpperInvariant();
                if (name == "YEAR" && op == BooleanComparisonType.Equals)
                {
                    if (Unwrap(other) is IntegerLiteral year && int.TryParse(year.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                        return string.Format(CultureInfo.InvariantCulture, "{0} >= '{1:0000}0101' AND {0} < '{2:0000}0101'", column, y, y + 1);
                    return string.Format(CultureInfo.InvariantCulture, "{0} >= DATEFROMPARTS({1}, 1, 1) AND {0} < DATEFROMPARTS({1} + 1, 1, 1)", column, value);
                }

                if ((name == "UPPER" || name == "LOWER" || name == "LTRIM" || name == "RTRIM" || name == "TRIM") && op == BooleanComparisonType.Equals)
                    return column + " = " + value + "   -- same result with a case-insensitive collation (" + name + ") / trailing spaces are ignored by = (RTRIM)";
            }

            // ISNULL(col, x) = c  ->  col = c  (when c is a different literal)
            if (wrapped is FunctionCall isnull && isnull.FunctionName.Value.Equals("ISNULL", StringComparison.OrdinalIgnoreCase)
                && isnull.Parameters.Count == 2 && Unwrap(isnull.Parameters[0]) is ColumnReferenceExpression isnullCol
                && op == BooleanComparisonType.Equals && Unwrap(other) is Literal otherLiteral && Unwrap(isnull.Parameters[1]) is Literal replacement)
            {
                string column = TextOf(isnullCol);
                return string.Equals(otherLiteral.Value, replacement.Value, StringComparison.Ordinal)
                    ? "(" + column + " = " + value + " OR " + column + " IS NULL)"
                    : column + " = " + value;
            }

            // LEFT(col, n) = 'abc'  ->  col LIKE 'abc%'
            if (wrapped is LeftFunctionCall left && left.Parameters.Count == 2 && Unwrap(left.Parameters[0]) is ColumnReferenceExpression leftCol
                && op == BooleanComparisonType.Equals && Unwrap(other) is StringLiteral prefix && !prefix.Value.Contains("%") && !prefix.Value.Contains("_") && !prefix.Value.Contains("["))
                return TextOf(leftCol) + " LIKE " + (prefix.IsNational ? "N" : string.Empty) + "'" + prefix.Value.Replace("'", "''") + "%'";

            // SUBSTRING(col, 1, n) = 'abc'  ->  col LIKE 'abc%'
            if (wrapped is FunctionCall sub && sub.FunctionName.Value.Equals("SUBSTRING", StringComparison.OrdinalIgnoreCase) && sub.Parameters.Count == 3
                && Unwrap(sub.Parameters[0]) is ColumnReferenceExpression subCol && Unwrap(sub.Parameters[1]) is IntegerLiteral start && start.Value == "1"
                && op == BooleanComparisonType.Equals && Unwrap(other) is StringLiteral subPrefix && !subPrefix.Value.Contains("%") && !subPrefix.Value.Contains("_") && !subPrefix.Value.Contains("["))
                return TextOf(subCol) + " LIKE " + (subPrefix.IsNational ? "N" : string.Empty) + "'" + subPrefix.Value.Replace("'", "''") + "%'";

            // col + n > x  ->  col > x - n   (also -, for numbers / dates with literals)
            if (wrapped is BinaryExpression bin && (bin.BinaryExpressionType == BinaryExpressionType.Add || bin.BinaryExpressionType == BinaryExpressionType.Subtract)
                && Unwrap(bin.FirstExpression) is ColumnReferenceExpression binCol && Unwrap(bin.SecondExpression) is IntegerLiteral && !otherHasColumns)
            {
                string inverse = bin.BinaryExpressionType == BinaryExpressionType.Add ? " - " : " + ";
                return TextOf(binCol) + " " + Operator(op) + " " + value + inverse + TextOf(bin.SecondExpression);
            }

            // DATEADD(unit, n, col) > x  ->  col > DATEADD(unit, -n, x)
            if (wrapped is FunctionCall dateAdd && dateAdd.FunctionName.Value.Equals("DATEADD", StringComparison.OrdinalIgnoreCase) && dateAdd.Parameters.Count == 3
                && Unwrap(dateAdd.Parameters[2]) is ColumnReferenceExpression dateCol && !otherHasColumns)
            {
                string amount = TextOf(dateAdd.Parameters[1]);
                return TextOf(dateCol) + " " + Operator(op) + " DATEADD(" + TextOf(dateAdd.Parameters[0]) + ", -(" + amount + "), " + value + ")";
            }

            return null;
        }

        // T2: LIKE '%...'
        private void CheckLeadingWildcard(LikePredicate like)
        {
            var pattern = Unwrap(like.SecondExpression);
            string first = null;
            if (pattern is StringLiteral s)
                first = s.Value;
            else if (pattern is BinaryExpression b && Unwrap(b.FirstExpression) is StringLiteral s2)
                first = s2.Value;
            if (first == null || !first.StartsWith("%", StringComparison.Ordinal) || Columns(like.FirstExpression).Count == 0)
                return;

            var f = Add("T2", AdvisorSeverity.Medium, "LIKE with a leading wildcard", like, 30);
            f.Explanation = "A pattern that starts with % can match anywhere in the value, so an index on " + Short(TextOf(like.FirstExpression))
                + " cannot be used to seek: every row (or every index entry) is read and tested.";
            f.Suggestion = "If the value starts with the text, drop the leading % (LIKE 'abc%' can seek). For searching words inside long texts, use a full-text index (CONTAINS). "
                + "For a few fixed prefixes/suffixes, a computed column (for example REVERSE(col) for suffix searches) with an index can help.";
        }

        // T3: NOT IN (subquery)
        private void CheckNotIn(InPredicate predicate)
        {
            if (!predicate.NotDefined || predicate.Subquery == null)
                return;

            var f = Add("T3", AdvisorSeverity.High, "NOT IN with a subquery", predicate, 60);
            f.Explanation = "If the subquery returns even one NULL, NOT IN returns no rows at all (x NOT IN (1, NULL) is UNKNOWN). "
                + "SQL Server also has to add extra checks for NULLs to the plan, which often makes it slower than NOT EXISTS.";
            f.Suggestion = "Use NOT EXISTS, which has no NULL problem and gives the optimizer an anti semi join. Qualify the columns of the subquery with its alias so they cannot bind to the outer query.";

            var spec = predicate.Subquery.QueryExpression as QuerySpecification;
            if (spec != null && spec.SelectElements.Count == 1 && spec.SelectElements[0] is SelectScalarExpression inner && spec.FromClause != null)
            {
                var sb = new StringBuilder();
                sb.Append("NOT EXISTS (SELECT 1 ").Append(TextOf(spec.FromClause))
                  .Append(" WHERE ").Append(TextOf(inner.Expression)).Append(" = ").Append(TextOf(predicate.Expression));
                if (spec.WhereClause?.SearchCondition != null)
                    sb.Append(" AND (").Append(TextOf(spec.WhereClause.SearchCondition)).Append(')');
                sb.Append(')');
                f.Script = "-- instead of: " + Short(TextOf(predicate)) + Environment.NewLine + sb;
            }
        }

        // T8: OR between conditions on different columns
        private void CheckOr(BooleanExpression condition)
        {
            var collector = new OrCollector();
            condition.Accept(collector);
            foreach (var or in collector.Ors)
            {
                var left = new HashSet<string>(Columns(or.FirstExpression).Select(ColumnKey));
                var right = new HashSet<string>(Columns(or.SecondExpression).Select(ColumnKey));
                if (left.Count == 0 || right.Count == 0 || left.SetEquals(right) || !_reportedOnce.Add("T8"))
                    continue;

                var f = Add("T8", AdvisorSeverity.Info, "OR between conditions on different columns", or, 10);
                f.Explanation = "With OR on different columns one index cannot answer the whole condition. The optimizer can combine two index seeks, "
                    + "but often it scans the table instead.";
                f.Suggestion = "Make sure each column has an index; if the plan still scans, split the query into two queries combined with UNION (or UNION ALL when the two parts cannot return the same row).";
            }
        }

        // ------------------------------------------------------------------ other rules

        // T4: SELECT *
        public override void ExplicitVisit(ExistsPredicate node)
        {
            _existsDepth++;
            base.ExplicitVisit(node);
            _existsDepth--;
        }

        public override void ExplicitVisit(SelectStarExpression node)
        {
            if (_existsDepth == 0 && _reportedOnce.Add("T4"))
            {
                var f = Add("T4", AdvisorSeverity.Info, "SELECT *", node, 5);
                f.Explanation = "All columns are read and sent to the client. An index can rarely cover the query (Key Lookups), the rows are wider "
                    + "(more memory for sorts and hashes), and the query breaks or changes silently when columns are added to the table.";
                f.Suggestion = "List only the columns you need.";
            }
            base.ExplicitVisit(node);
        }

        // T5: ORDER BY with TOP 100 PERCENT in a subquery / view / CTE
        public override void ExplicitVisit(SelectStatement node)
        {
            if (_topQuery == null)
                _topQuery = node.QueryExpression;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (!ReferenceEquals(node, _topQuery) && node.OrderByClause != null && node.TopRowFilter != null && node.TopRowFilter.Percent
                && Unwrap(node.TopRowFilter.Expression) is Literal hundred && (hundred.Value == "100" || hundred.Value == "100.0"))
            {
                var f = Add("T5", AdvisorSeverity.Info, "ORDER BY with TOP 100 PERCENT in a subquery or view", node.OrderByClause, 5);
                f.Explanation = "The optimizer ignores this ORDER BY: TOP 100 PERCENT keeps all rows, so it does not need to sort. The result order is not guaranteed.";
                f.Suggestion = "Remove TOP 100 PERCENT / ORDER BY from the subquery or view, and put ORDER BY in the outermost query.";
            }
            base.ExplicitVisit(node);
        }

        // T6: NOLOCK / READ UNCOMMITTED
        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.TableHints.Any(h => h.HintKind == TableHintKind.NoLock || h.HintKind == TableHintKind.ReadUncommitted) && _reportedOnce.Add("T6"))
                AddNoLock(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetTransactionIsolationLevelStatement node)
        {
            if (node.Level == IsolationLevel.ReadUncommitted && _reportedOnce.Add("T6"))
                AddNoLock(node);
            base.ExplicitVisit(node);
        }

        private void AddNoLock(TSqlFragment node)
        {
            var f = Add("T6", AdvisorSeverity.Medium, "NOLOCK / READ UNCOMMITTED", node, 25);
            f.Explanation = "Dirty reads: the query can return rows that are rolled back later, miss rows or return the same row twice "
                + "(when pages split during the scan), and can fail with error 601. It does not make the query itself faster; it only avoids waiting for locks.";
            f.Suggestion = "If readers wait for writers, enable READ_COMMITTED_SNAPSHOT on the database (readers see the last committed version without blocking), "
                + "and keep the hint only where approximate results are acceptable.";
        }

        // T7: scalar user-defined function
        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget target && !IsTypeMethod(node.FunctionName.Value))
            {
                var schema = target.MultiPartIdentifier.Identifiers.LastOrDefault()?.Value;
                if (!string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase) && _reportedOnce.Add("T7:" + TextOf(target) + node.FunctionName.Value))
                {
                    var f = Add("T7", AdvisorSeverity.Medium, "Scalar user-defined function " + TextOf(target) + "." + node.FunctionName.Value + "()", node, 30);
                    f.Explanation = "A scalar function is called once per row and its cost is not in the plan (it looks cheap). It also prevents a parallel plan. "
                        + "On SQL Server 2019+ (compatibility level 150) simple functions can be inlined automatically; check is_inlineable in sys.sql_modules.";
                    f.Suggestion = "Rewrite the logic inline in the query, or as an inline table-valued function used with CROSS APPLY.";
                    f.Script = "SELECT OBJECT_NAME(object_id) AS function_name, is_inlineable" + Environment.NewLine
                        + "FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'" + (TextOf(target) + "." + node.FunctionName.Value).Replace("'", "''") + "');";
                }
            }
            base.ExplicitVisit(node);
        }

        /// <summary>Methods of xml / spatial / hierarchyid columns also have a call target (col.value(...)).</summary>
        private static bool IsTypeMethod(string name)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "value":
                case "query":
                case "exist":
                case "nodes":
                case "modify":
                case "tostring":
                case "getancestor":
                case "getdescendant":
                case "getlevel":
                case "isdescendantof":
                case "getreparentedvalue":
                    return true;
                default:
                    return name != null && name.StartsWith("ST", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2]);
            }
        }

        // T9: cursor
        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            if (_reportedOnce.Add("T9"))
            {
                var f = Add("T9", AdvisorSeverity.Info, "Cursor", node, 10);
                f.Explanation = "A cursor processes one row at a time: each FETCH is a separate operation, which is usually much slower than one set-based statement.";
                f.Suggestion = "Replace the loop with a set-based INSERT / UPDATE / MERGE when possible. If the cursor stays, declare it LOCAL FAST_FORWARD (read-only, forward-only).";
            }
            base.ExplicitVisit(node);
        }

        // ------------------------------------------------------------------ collectors

        private sealed class ColumnCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> Columns { get; } = new List<ColumnReferenceExpression>();

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                if (node.ColumnType == ColumnType.Regular && node.MultiPartIdentifier != null)
                    Columns.Add(node);
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                // Columns of a subquery belong to it.
            }
        }

        /// <summary>Comparisons, LIKEs and INs of one search condition, not descending into subqueries.</summary>
        private sealed class PredicateCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = new List<BooleanComparisonExpression>();
            public List<LikePredicate> Likes { get; } = new List<LikePredicate>();
            public List<InPredicate> Ins { get; } = new List<InPredicate>();

            public override void ExplicitVisit(BooleanComparisonExpression node) => Comparisons.Add(node);

            public override void ExplicitVisit(LikePredicate node) => Likes.Add(node);

            public override void ExplicitVisit(InPredicate node) => Ins.Add(node);

            public override void ExplicitVisit(ScalarSubquery node)
            {
            }

            public override void ExplicitVisit(ExistsPredicate node)
            {
            }
        }

        private sealed class OrCollector : TSqlFragmentVisitor
        {
            public List<BooleanBinaryExpression> Ors { get; } = new List<BooleanBinaryExpression>();

            public override void ExplicitVisit(BooleanBinaryExpression node)
            {
                if (node.BinaryExpressionType == BooleanBinaryExpressionType.Or)
                    Ors.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
            }

            public override void ExplicitVisit(ExistsPredicate node)
            {
            }
        }
    }
}
