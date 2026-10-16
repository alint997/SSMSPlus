namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    /// <summary>A plan with its advisor result, kept in the comparison history of the Execution Plan Tree window.</summary>
    public sealed class PlanSnapshot
    {
        public PlanDocument Document { get; set; }

        public AdvisorResult Result { get; set; }

        public DateTime TakenAt { get; set; } = DateTime.Now;

        /// <summary>Sequence number in the history of the window (#1, #2, ...); 0 until it is added.</summary>
        public int Number { get; set; }

        public bool IsActual => Document != null && Document.HasActual;

        public string Kind => IsActual ? "actual" : "estimated";

        public double Cost => Document == null ? 0 : Document.Statements.Sum(s => s.SubtreeCost);

        /// <summary>"#3  14:05:12  actual, cost 12.5  SQLQuery1.sql – server / db".</summary>
        public string Label => "#" + Number + "  " + TakenAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "  " + Kind + ", cost "
            + PlanComparer.FormatNumber(Cost) + "  " + PlanComparer.Short(Document?.Source, 70);

        public override string ToString() => Label;
    }

    /// <summary>One line of the measures table: before / after / change, and whether the change is better (1) or worse (-1).</summary>
    public sealed class MeasureRow
    {
        public string Label { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Change { get; set; }
        public int Trend { get; set; }
    }

    /// <summary>A table read differently by the two plans.</summary>
    public sealed class AccessChange
    {
        public string Table { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Meaning { get; set; }
        public int Trend { get; set; }
    }

    public sealed class StatementComparison
    {
        public int Index { get; set; }
        public int? BaselineIndex { get; set; }
        public string Text { get; set; }
        public bool TextChanged { get; set; }
        public bool IsNew { get; set; }
        public bool IsRemoved { get; set; }

        /// <summary>1 better, -1 worse, 0 about the same.</summary>
        public int Trend { get; set; }

        /// <summary>The measure the verdict is based on, e.g. "Elapsed time 1,234 ms → 120 ms (-90%, 10x lower)".</summary>
        public string Headline { get; set; }

        public List<MeasureRow> Measures { get; } = new List<MeasureRow>();
        public List<AccessChange> Access { get; } = new List<AccessChange>();
        public List<AdvisorFinding> Fixed { get; } = new List<AdvisorFinding>();
        public List<AdvisorFinding> New { get; } = new List<AdvisorFinding>();
        public List<AdvisorFinding> Open { get; } = new List<AdvisorFinding>();
        public string FindingsBefore { get; set; }
        public string FindingsAfter { get; set; }
        public List<string> Good { get; } = new List<string>();
        public List<string> Bad { get; } = new List<string>();
    }

    /// <summary>A key measure of the whole script before / after, drawn as a pair of bars in the Compare tab.</summary>
    public sealed class KpiRow
    {
        public string Label { get; set; }
        public double? Before { get; set; }
        public double? After { get; set; }
        public string BeforeText { get; set; }
        public string AfterText { get; set; }
        public string Change { get; set; }
        public int Trend { get; set; }

        /// <summary>The measure the verdict is based on.</summary>
        public bool IsPrimary { get; set; }
    }

    public sealed class ComparisonResult
    {
        public PlanSnapshot Before { get; set; }
        public PlanSnapshot After { get; set; }

        /// <summary>1 better, -1 worse, 0 about the same, 2 mixed.</summary>
        public int Trend { get; set; }

        public string Overall { get; set; }
        public string Headline { get; set; }

        /// <summary>The result in one plain sentence, e.g. "After is 4.1x cheaper: estimated cost 6.81 → 1.64."</summary>
        public string Summary { get; set; }

        public List<KpiRow> Kpis { get; } = new List<KpiRow>();
        public List<string> Good { get; } = new List<string>();
        public List<string> Bad { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<StatementComparison> Statements { get; } = new List<StatementComparison>();

        /// <summary>The same comparison as plain text (Copy, report).</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// Plan comparison (DB2 Query Tuner "compare access plans"): the result in plain words first (better / worse /
    /// about the same, what improved, what got worse), then per statement the measures before / after, the tables
    /// read differently and the advisor findings fixed, new or still open.
    /// </summary>
    public static class PlanComparer
    {
        private const double Significant = 10; // % change that counts as better / worse

        private enum Direction
        {
            LowerIsBetter,
            Neutral,
        }

        private sealed class Measure
        {
            public string Label;
            public double? Before;
            public double? After;
            public Direction Direction;
            public string Unit = string.Empty;
            public bool OnlyWhenNonZero;
            public bool OnlyWhenChanged;

            /// <summary>Smaller absolute differences are noise (1 ms -> 0 ms is not "100% better").</summary>
            public double MinDelta;

            /// <summary>Below this value (before and after) the measure is too small to decide the verdict.</summary>
            public double MinForVerdict;

            public bool CanDecide => Both && Math.Max(Before.Value, After.Value) >= MinForVerdict;

            public bool Both => Before.HasValue && After.HasValue;

            public double? Percent => Both && Before.Value > 0 ? (After.Value - Before.Value) * 100 / Before.Value : (double?)null;

            public int Trend
            {
                get
                {
                    if (Direction == Direction.Neutral || !Both || Before.Value == After.Value)
                        return 0;
                    if (Math.Abs(After.Value - Before.Value) < MinDelta)
                        return 0;
                    if (Before.Value <= 0)
                        return After.Value > 0 ? -1 : 0;
                    var pct = Percent.Value;
                    return pct <= -Significant ? 1 : pct >= Significant ? -1 : 0;
                }
            }

            public string Value(double? v) => v.HasValue ? FormatNumber(v.Value) + Unit : "-";

            /// <summary>"Logical reads 12,000 → 340 (-97%, 35x lower)".</summary>
            public string Sentence()
            {
                var change = Change();
                return Label + " " + Value(Before) + " → " + Value(After) + (string.IsNullOrEmpty(change) ? string.Empty : " (" + change + ")");
            }

            /// <summary>"4.1x lower" when the factor is 2 or more, else "-24%"; "same" / "".</summary>
            public string ShortChange()
            {
                if (!Both || Before.Value == After.Value || Before.Value <= 0 || After.Value <= 0)
                    return Change();
                double hi = Math.Max(Before.Value, After.Value), lo = Math.Min(Before.Value, After.Value);
                if (hi / lo >= 2)
                    return (hi / lo).ToString(hi / lo < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "x " + (Before.Value > After.Value ? "lower" : "higher");
                double pct = Percent.Value;
                return (pct > 0 ? "+" : string.Empty) + pct.ToString(Math.Abs(pct) < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%";
            }

            /// <summary>"-76%, 4.1x lower" / "same" / "".</summary>
            public string Change()
            {
                if (!Both)
                    return string.Empty;
                if (Before.Value == After.Value)
                    return "same";
                if (Before.Value <= 0)
                    return "was 0";

                double pct = Percent.Value;
                var sb = new StringBuilder();
                sb.Append(pct > 0 ? "+" : string.Empty).Append(pct.ToString(Math.Abs(pct) < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)).Append('%');
                if (After.Value > 0 && (Before.Value / After.Value >= 2 || After.Value / Before.Value >= 2))
                {
                    double factor = Before.Value > After.Value ? Before.Value / After.Value : After.Value / Before.Value;
                    sb.Append(", ").Append(factor.ToString(factor < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)).Append("x ").Append(Before.Value > After.Value ? "lower" : "higher");
                }
                return sb.ToString();
            }
        }

        private static Measure Elapsed(double? a, double? b) => new Measure { Label = "Elapsed time", Before = a, After = b, Direction = Direction.LowerIsBetter, Unit = " ms", MinDelta = 5, MinForVerdict = 20 };

        private static Measure Cpu(double? a, double? b) => new Measure { Label = "CPU time", Before = a, After = b, Direction = Direction.LowerIsBetter, Unit = " ms", MinDelta = 5 };

        private static Measure Reads(double? a, double? b) => new Measure { Label = "Logical reads", Before = a, After = b, Direction = Direction.LowerIsBetter, MinDelta = 10, MinForVerdict = 100 };

        private static Measure Cost(double? a, double? b) => new Measure { Label = "Estimated cost", Before = a, After = b, Direction = Direction.LowerIsBetter, MinDelta = 0.001 };

        private static Measure Memory(double? a, double? b) => new Measure { Label = "Memory grant", Before = a, After = b, Direction = Direction.LowerIsBetter, Unit = " KB", MinDelta = 1024 };

        /// <summary>Verdict measure: measured time when it is large enough to mean something, then reads, then the estimated cost.</summary>
        private static Measure Primary(Measure elapsed, Measure reads, Measure cost) => elapsed.CanDecide ? elapsed : reads.CanDecide ? reads : cost;

        /// <summary>"After is 4.1x cheaper: estimated cost 6.81 → 1.64."</summary>
        private static string Plain(Measure m, int trend)
        {
            if (trend == 2)
                return "Some statements got better and some worse; see the details below.";
            if (!m.Both)
                return "No measure is available in both plans.";

            string values = char.ToLowerInvariant(m.Label[0]) + m.Label.Substring(1) + " " + m.Value(m.Before) + " → " + m.Value(m.After);
            if (trend == 0)
                return "No significant difference: " + values + ".";

            string how;
            double hi = Math.Max(m.Before.Value, m.After.Value);
            double lo = Math.Min(m.Before.Value, m.After.Value);
            if (lo > 0 && hi / lo >= 2)
                how = (hi / lo).ToString(hi / lo < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "x";
            else if (m.Before.Value > 0)
                how = Math.Abs(m.Percent.Value).ToString("0", CultureInfo.CurrentCulture) + "%";
            else
                how = "much";
            string word = m.Label == "Estimated cost" ? (trend > 0 ? "cheaper" : "more expensive")
                : m.Label == "Elapsed time" ? (trend > 0 ? "faster" : "slower")
                : (trend > 0 ? "fewer reads" : "more reads");
            if (m.Label == "Logical reads")
                return "After does " + how + " " + word + ": " + values + ".";
            return "After is " + how + " " + word + ": " + values + ".";
        }

        public static ComparisonResult Compare(PlanSnapshot before, PlanSnapshot after)
        {
            var r = new ComparisonResult { Before = before, After = after };
            if (before?.Document == null || after?.Document == null)
            {
                r.Overall = "Nothing to compare";
                r.Text = string.Empty;
                return r;
            }

            bool kindsDiffer = before.IsActual != after.IsActual;
            var pairs = MatchStatements(before.Document, after.Document);
            foreach (var pair in pairs)
            {
                if (pair.Item1 == null)
                    r.Statements.Add(new StatementComparison { Index = pair.Item2.Index, Text = pair.Item2.Text, IsNew = true });
                else if (pair.Item2 == null)
                    r.Statements.Add(new StatementComparison { Index = pair.Item1.Index, BaselineIndex = pair.Item1.Index, Text = pair.Item1.Text, IsRemoved = true });
                else
                    r.Statements.Add(CompareStatement(pair.Item1, pair.Item2, before.Result, after.Result));
            }

            var compared = r.Statements.Where(s => !s.IsNew && !s.IsRemoved).ToList();
            int better = compared.Count(s => s.Trend > 0);
            int worse = compared.Count(s => s.Trend < 0);
            r.Trend = better > 0 && worse > 0 ? 2 : worse > 0 ? -1 : better > 0 ? 1 : 0;
            r.Overall = r.Trend == 2 ? "Mixed" : r.Trend < 0 ? "Worse" : r.Trend > 0 ? "Better" : "About the same";

            // Key measures of the whole script (the statements found in both plans), with the verdict measure.
            var both = pairs.Where(p => p.Item1 != null && p.Item2 != null).ToList();
            var timed = both.Where(p => p.Item1.ElapsedTimeMs.HasValue && p.Item2.ElapsedTimeMs.HasValue).ToList();
            var cpuTimed = both.Where(p => p.Item1.CpuTimeMs.HasValue && p.Item2.CpuTimeMs.HasValue).ToList();
            var read = both.Where(p => p.Item1.LogicalReads.HasValue && p.Item2.LogicalReads.HasValue).ToList();
            var tElapsed = Elapsed(Sum(timed, p => p.Item1.ElapsedTimeMs), Sum(timed, p => p.Item2.ElapsedTimeMs));
            var tCpu = Cpu(Sum(cpuTimed, p => p.Item1.CpuTimeMs), Sum(cpuTimed, p => p.Item2.CpuTimeMs));
            var tReads = Reads(Sum(read, p => p.Item1.LogicalReads), Sum(read, p => p.Item2.LogicalReads));
            var tCost = Cost(both.Sum(p => p.Item1.SubtreeCost), both.Sum(p => p.Item2.SubtreeCost));
            var tMemory = Memory(Sum(both, p => Parse(p.Item1.MemoryGrantKb)), Sum(both, p => Parse(p.Item2.MemoryGrantKb)));
            var tFindings = new Measure
            {
                Label = "Findings (high + medium)",
                Before = Serious(before.Result, both.Select(p => p.Item1)),
                After = Serious(after.Result, both.Select(p => p.Item2)),
                Direction = Direction.LowerIsBetter,
            };
            var tPrimary = Primary(tElapsed, tReads, tCost);
            foreach (var m in new[] { tElapsed, tCpu, tReads, tCost, tMemory, tFindings })
            {
                if (!m.Both)
                    continue;
                r.Kpis.Add(new KpiRow
                {
                    Label = m.Label,
                    Before = m.Before,
                    After = m.After,
                    BeforeText = m.Value(m.Before),
                    AfterText = m.Value(m.After),
                    Change = m.ShortChange(),
                    Trend = m.Trend,
                    IsPrimary = m == tPrimary,
                });
            }

            if (compared.Count == 1)
            {
                r.Summary = Plain(Primary(
                    Elapsed(both[0].Item1.ElapsedTimeMs, both[0].Item2.ElapsedTimeMs),
                    Reads(both[0].Item1.LogicalReads, both[0].Item2.LogicalReads),
                    Cost(both[0].Item1.SubtreeCost, both[0].Item2.SubtreeCost)), r.Trend);
                r.Headline = compared[0].Headline;
                r.Good.AddRange(compared[0].Good.Where(NotMeasure));
                r.Bad.AddRange(compared[0].Bad.Where(NotMeasure));
            }
            else
            {
                r.Summary = r.Trend == 2 ? Plain(tPrimary, 2) : Plain(tPrimary, r.Trend == 0 ? 0 : tPrimary.Trend == 0 ? r.Trend : tPrimary.Trend);
                r.Headline = (better == 0 && worse == 0 ? "No statement changed by 10% or more. " : better + " of " + compared.Count + " statements better, " + worse + " worse. ") + "Total: " + tPrimary.Sentence();
                foreach (var s in compared)
                {
                    r.Good.AddRange(s.Good.Where(NotMeasure).Select(g => "Statement " + s.Index + ": " + g));
                    r.Bad.AddRange(s.Bad.Where(NotMeasure).Select(b => "Statement " + s.Index + ": " + b));
                }
            }

            int added = r.Statements.Count(s => s.IsNew);
            int removed = r.Statements.Count(s => s.IsRemoved);
            if (added > 0 || removed > 0)
            {
                var parts = new List<string>();
                if (added > 0)
                    parts.Add(added + " statement(s) added");
                if (removed > 0)
                    parts.Add(removed + " statement(s) removed");
                r.Notes.Add("The script changed: " + string.Join(", ", parts) + ". Only the statements found in both plans are compared.");
            }
            int textChanged = compared.Count(s => s.TextChanged);
            if (textChanged > 0)
                r.Notes.Add(compared.Count == 1 ? "The text of the statement changed." : "The text of " + textChanged + " statement(s) changed.");
            if (kindsDiffer)
                r.Notes.Add("One plan is estimated and the other actual: only the estimated cost can be compared. Use the same kind for both (Refresh repeats the last kind).");
            else if (!before.IsActual)
                r.Notes.Add("Estimated plans: the costs are the optimizer's estimates, not measured time. For time, CPU and reads compare two actual plans.");

            r.Text = BuildText(r);
            return r;
        }

        private static StatementComparison CompareStatement(PlanStatement a, PlanStatement b, AdvisorResult ra, AdvisorResult rb)
        {
            var result = new StatementComparison
            {
                Index = b.Index,
                BaselineIndex = a.Index,
                Text = b.Text,
                TextChanged = !string.Equals(Normalize(a.Text), Normalize(b.Text), StringComparison.Ordinal),
            };

            var fa = ra?.Findings.Where(f => f.Statement == a).ToList() ?? new List<AdvisorFinding>();
            var fb = rb?.Findings.Where(f => f.Statement == b).ToList() ?? new List<AdvisorFinding>();

            var elapsed = Elapsed(a.ElapsedTimeMs, b.ElapsedTimeMs);
            var cpu = Cpu(a.CpuTimeMs, b.CpuTimeMs);
            var reads = Reads(a.LogicalReads, b.LogicalReads);
            var cost = Cost(a.SubtreeCost, b.SubtreeCost);
            var memory = Memory(Parse(a.MemoryGrantKb), Parse(b.MemoryGrantKb));
            var measures = new List<Measure>
            {
                elapsed,
                cpu,
                reads,
                cost,
                memory,
                new Measure { Label = "High findings", Before = fa.Count(f => f.Severity == AdvisorSeverity.High), After = fb.Count(f => f.Severity == AdvisorSeverity.High), Direction = Direction.LowerIsBetter, OnlyWhenNonZero = true },
                new Measure { Label = "Medium findings", Before = fa.Count(f => f.Severity == AdvisorSeverity.Medium), After = fb.Count(f => f.Severity == AdvisorSeverity.Medium), Direction = Direction.LowerIsBetter, OnlyWhenNonZero = true },
                new Measure { Label = "Operators with warnings", Before = a.Root.DescendantsAndSelf().Count(n => n.HasWarnings), After = b.Root.DescendantsAndSelf().Count(n => n.HasWarnings), Direction = Direction.LowerIsBetter, OnlyWhenNonZero = true },
                new Measure { Label = "Estimated rows", Before = a.EstimatedRows, After = b.EstimatedRows, Direction = Direction.Neutral },
                new Measure { Label = "Actual rows", Before = a.HasActual ? a.Root?.ActualRows : null, After = b.HasActual ? b.Root?.ActualRows : null, Direction = Direction.Neutral },
                new Measure { Label = "Parallelism (DOP)", Before = Parse(a.DegreeOfParallelism), After = Parse(b.DegreeOfParallelism), Direction = Direction.Neutral, OnlyWhenChanged = true },
                new Measure { Label = "Operators", Before = a.Operators().Count(), After = b.Operators().Count(), Direction = Direction.Neutral, OnlyWhenChanged = true },
            };

            foreach (var m in measures)
            {
                if (!m.Before.HasValue && !m.After.HasValue)
                    continue;
                if (m.OnlyWhenNonZero && (m.Before ?? 0) == 0 && (m.After ?? 0) == 0)
                    continue;
                if (m.OnlyWhenChanged && m.Both && m.Before.Value == m.After.Value)
                    continue;
                result.Measures.Add(new MeasureRow { Label = m.Label, Before = m.Value(m.Before), After = m.Value(m.After), Change = m.Change(), Trend = m.Trend });
            }

            // Verdict: measured time first, then reads, then the estimated cost.
            var primary = Primary(elapsed, reads, cost);
            result.Trend = primary.Trend;
            result.Headline = primary.Sentence();

            foreach (var m in new[] { elapsed, cpu, reads, cost, memory }.Where(m => m.Trend != 0))
                (m.Trend > 0 ? result.Good : result.Bad).Add(m.Sentence());
            if (cost.Trend != 0 && primary != cost && cost.Trend != primary.Trend)
                result.Bad.Add("The estimated cost went the other way: the optimizer's estimates do not match what was measured (check the statistics).");

            // Tables read differently.
            var tablesA = Tables(a);
            var tablesB = Tables(b);
            foreach (var table in tablesA.Keys.Union(tablesB.Keys, StringComparer.OrdinalIgnoreCase))
            {
                tablesA.TryGetValue(table, out var ta);
                tablesB.TryGetValue(table, out var tb);
                if (ta != null && tb != null && string.Equals(ta.Path, tb.Path, StringComparison.OrdinalIgnoreCase))
                    continue;

                string meaning = Meaning(ta, tb, out int trend);
                result.Access.Add(new AccessChange { Table = table, Before = ta?.Path ?? "(not read)", After = tb?.Path ?? "(not read)", Meaning = meaning, Trend = trend });
                if (trend != 0)
                    (trend > 0 ? result.Good : result.Bad).Add(table + ": " + meaning);
            }

            // Advisor findings: fixed / new / still open.
            var keysA = fa.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var keysB = fb.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            result.Fixed.AddRange(keysA.Where(k => !keysB.ContainsKey(k.Key)).Select(k => k.Value).OrderBy(f => f.Severity));
            result.New.AddRange(keysB.Where(k => !keysA.ContainsKey(k.Key)).Select(k => k.Value).OrderBy(f => f.Severity));
            result.Open.AddRange(keysB.Where(k => keysA.ContainsKey(k.Key)).Select(k => k.Value).OrderBy(f => f.Severity));
            result.FindingsBefore = Summary(fa);
            result.FindingsAfter = Summary(fb);
            if (result.Fixed.Count > 0)
                result.Good.Add(result.Fixed.Count + " advisor finding(s) fixed" + HighCount(result.Fixed));
            if (result.New.Count > 0)
                result.Bad.Add(result.New.Count + " new advisor finding(s)" + HighCount(result.New));

            return result;
        }

        /// <summary>Sum of a value over the statements; statements without it (e.g. no time statistics) are skipped. Null when none has it.</summary>
        private static double? Sum(List<Tuple<PlanStatement, PlanStatement>> pairs, Func<Tuple<PlanStatement, PlanStatement>, double?> value)
        {
            double total = 0;
            bool any = false;
            foreach (var p in pairs)
            {
                var v = value(p);
                if (v.HasValue)
                {
                    total += v.Value;
                    any = true;
                }
            }
            return any ? total : (double?)null;
        }

        private static double Serious(AdvisorResult result, IEnumerable<PlanStatement> statements)
        {
            if (result == null)
                return 0;
            var set = new HashSet<PlanStatement>(statements);
            return result.Findings.Count(f => f.Statement != null && set.Contains(f.Statement) && f.Severity != AdvisorSeverity.Info);
        }

        /// <summary>Lines other than "Elapsed time 120 ms → 8 ms": those measures are already in the bars at the top.</summary>
        private static bool NotMeasure(string line)
        {
            return !new[] { "Elapsed time ", "CPU time ", "Logical reads ", "Estimated cost ", "Memory grant " }.Any(m => line.StartsWith(m, StringComparison.Ordinal));
        }

        private static string HighCount(List<AdvisorFinding> list)
        {
            int high = list.Count(f => f.Severity == AdvisorSeverity.High);
            return high > 0 ? " (" + high + " high)" : string.Empty;
        }

        private static string Summary(List<AdvisorFinding> list)
        {
            var text = QueryAdvisor.Summary(list);
            return string.IsNullOrEmpty(text) ? "none" : text;
        }

        // ------------------------------------------------------------------ text (Copy, report)

        private static string BuildText(ComparisonResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- SSMS Plus: plan comparison");
            sb.Append("-- Before: ").AppendLine(r.Before.Label);
            sb.Append("-- After:  ").AppendLine(r.After.Label);
            sb.AppendLine("--");
            sb.Append("-- RESULT: ").Append(r.Overall.ToUpperInvariant());
            if (!string.IsNullOrEmpty(r.Headline))
                sb.Append(" - ").Append(r.Headline);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.Summary))
                sb.Append("--   ").AppendLine(r.Summary);
            sb.AppendLine("--");
            foreach (var k in r.Kpis)
                sb.Append("--   ").Append(k.Label.PadRight(26)).Append(k.BeforeText.PadLeft(14)).Append(" → ").Append(k.AfterText.PadRight(14))
                  .Append(k.Change).AppendLine(k.Trend > 0 ? "  better" : k.Trend < 0 ? "  worse" : string.Empty);
            if (r.Kpis.Count > 0)
                sb.AppendLine("--");
            foreach (var g in r.Good)
                sb.Append("--   + ").AppendLine(g);
            foreach (var b in r.Bad)
                sb.Append("--   - ").AppendLine(b);
            foreach (var n in r.Notes)
                sb.Append("--   Note: ").AppendLine(n);

            foreach (var s in r.Statements)
            {
                sb.AppendLine("--");
                if (s.IsNew)
                {
                    sb.Append("-- ===== Statement ").Append(s.Index).Append(" (new): ").AppendLine(Short(s.Text, 120));
                    continue;
                }
                if (s.IsRemoved)
                {
                    sb.Append("-- ===== Statement ").Append(s.Index).Append(" of Before (no longer in the script): ").AppendLine(Short(s.Text, 120));
                    continue;
                }

                sb.Append("-- ===== Statement ").Append(s.Index).Append(": ").Append(Verdict(s.Trend)).Append(" - ").AppendLine(Short(s.Text, 120));
                sb.Append("--   ").Append(string.Empty.PadRight(24)).Append("Before".PadLeft(14)).Append("After".PadLeft(14)).AppendLine("   Change");
                foreach (var m in s.Measures)
                {
                    sb.Append("--   ").Append(m.Label.PadRight(24)).Append(m.Before.PadLeft(14)).Append(m.After.PadLeft(14)).Append("   ").Append(m.Change);
                    if (m.Trend != 0)
                        sb.Append(m.Trend > 0 ? "  better" : "  worse");
                    sb.AppendLine();
                }

                if (s.Access.Count > 0)
                {
                    sb.AppendLine("--   Tables read differently:");
                    foreach (var c in s.Access)
                    {
                        sb.Append("--     ").Append(c.Trend > 0 ? "+ " : c.Trend < 0 ? "- " : "~ ").Append(c.Table).Append(": ").Append(c.Before).Append(" → ").AppendLine(c.After);
                        if (!string.IsNullOrEmpty(c.Meaning))
                            sb.Append("--         ").AppendLine(c.Meaning);
                    }
                }

                sb.Append("--   Advisor findings: ").Append(s.FindingsBefore).Append(" → ").AppendLine(s.FindingsAfter);
                FindingList(sb, "Fixed", s.Fixed);
                FindingList(sb, "New", s.New);
                FindingList(sb, "Still open", s.Open);
            }

            // Plain text, not T-SQL comments: it is copied or read, never executed.
            var lines = sb.ToString().Replace("\r\n", "\n").Split('\n')
                .Select(l => l.StartsWith("-- ", StringComparison.Ordinal) ? l.Substring(3) : l.StartsWith("--", StringComparison.Ordinal) ? l.Substring(2) : l);
            return string.Join(Environment.NewLine, lines).Replace("===== ", string.Empty);
        }

        internal static string Verdict(int trend) => trend == 2 ? "mixed" : trend > 0 ? "better" : trend < 0 ? "worse" : "about the same";

        internal static string FindingText(AdvisorFinding f)
        {
            return "[" + f.Severity + "] " + f.RuleId + " " + f.Title + (string.IsNullOrEmpty(f.Object) ? string.Empty : " - " + Short(f.Object, 100));
        }

        private static void FindingList(StringBuilder sb, string title, List<AdvisorFinding> list)
        {
            if (list.Count == 0)
                return;
            sb.Append("--     ").Append(title).Append(" (").Append(list.Count).AppendLine("):");
            foreach (var f in list)
                sb.Append("--       ").AppendLine(FindingText(f));
        }

        // ------------------------------------------------------------------ tables

        private sealed class TableAccess
        {
            public string Path;
            public double Cost;
            public bool Scan;
            public bool Seek;
            public bool Lookup;
        }

        private static Dictionary<string, TableAccess> Tables(PlanStatement s)
        {
            return s.Operators()
                .Where(o => !string.IsNullOrEmpty(o.ObjectTable))
                .GroupBy(o => Sql.Name(o.ObjectSchema, o.ObjectTable), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new TableAccess
                    {
                        Path = string.Join(" + ", g.OrderBy(o => o.NodeId)
                            .Select(o => o.PhysicalOp + (string.IsNullOrEmpty(o.ObjectIndex) ? string.Empty : " " + PlanNode.Clean(o.ObjectIndex)))
                            .Distinct()),
                        Cost = g.Sum(o => o.OwnCost),
                        Scan = g.Any(o => o.Category == PlanNodeCategory.Scan),
                        Seek = g.Any(o => o.Category == PlanNodeCategory.Seek),
                        Lookup = g.Any(o => o.Category == PlanNodeCategory.Lookup),
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>What the change of access path means for the table, and whether it is better (1) or worse (-1).</summary>
        private static string Meaning(TableAccess a, TableAccess b, out int trend)
        {
            trend = 0;
            if (a == null)
                return "Now also read by the plan.";
            if (b == null)
                return "No longer read by the plan.";

            var parts = new List<string>();
            if (a.Scan && !b.Scan && b.Seek)
            {
                parts.Add("now seeks to the matching rows instead of reading the whole table");
                trend = 1;
            }
            else if (!a.Scan && b.Scan)
            {
                parts.Add("now reads the whole table (scan) instead of seeking");
                trend = -1;
            }

            if (a.Lookup && !b.Lookup)
            {
                parts.Add("no more Key / RID Lookups: the index covers the query");
                trend = trend < 0 ? trend : 1;
            }
            else if (!a.Lookup && b.Lookup)
            {
                parts.Add("Key / RID Lookups added: the index used does not cover the query");
                trend = -1;
            }

            if (parts.Count == 0)
            {
                if (a.Cost > 0 && b.Cost < a.Cost * (1 - Significant / 100))
                    trend = 1;
                else if (a.Cost > 0 && b.Cost > a.Cost * (1 + Significant / 100))
                    trend = -1;
                parts.Add("different index / operator, cost of these operators " + FormatNumber(a.Cost) + " → " + FormatNumber(b.Cost));
            }

            var text = string.Join("; ", parts);
            return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
        }

        /// <summary>Pairs the statements by position when the count is the same, else by text.</summary>
        private static List<Tuple<PlanStatement, PlanStatement>> MatchStatements(PlanDocument a, PlanDocument b)
        {
            var result = new List<Tuple<PlanStatement, PlanStatement>>();
            if (a.Statements.Count == b.Statements.Count)
            {
                for (int i = 0; i < a.Statements.Count; i++)
                    result.Add(Tuple.Create(a.Statements[i], b.Statements[i]));
                return result;
            }

            var remaining = a.Statements.ToList();
            foreach (var s in b.Statements)
            {
                var match = remaining.FirstOrDefault(x => string.Equals(Normalize(x.Text), Normalize(s.Text), StringComparison.Ordinal));
                if (match != null)
                    remaining.Remove(match);
                result.Add(Tuple.Create(match, s));
            }
            foreach (var s in remaining)
                result.Add(Tuple.Create(s, (PlanStatement)null));
            return result;
        }

        private static string Key(AdvisorFinding f) => f.RuleId + "|" + f.Title + "|" + f.Object;

        internal static string FormatNumber(double v) => v.ToString(Math.Abs(v) >= 100 || v == Math.Floor(v) ? "N0" : "0.####", CultureInfo.CurrentCulture);

        private static double? Parse(string s)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? (double?)v : null;
        }

        private static string Normalize(string text)
        {
            var t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim().ToLowerInvariant();
            while (t.Contains("  "))
                t = t.Replace("  ", " ");
            return t;
        }

        internal static string Short(string text, int max)
        {
            var t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (t.Contains("  "))
                t = t.Replace("  ", " ");
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }
    }
}
