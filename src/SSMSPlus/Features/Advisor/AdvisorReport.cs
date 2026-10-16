namespace SSMSPlus.Features.Advisor
{
    using SSMSPlus.Features.ExecutionPlan;
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Text;

    /// <summary>
    /// Query report (DB2 "Query Report"): one self-contained HTML file with the statements, the advisor findings
    /// (explanation, suggestion, script), the annotated query, the index advice and, when a baseline is set, the
    /// comparison. Meant to be saved and shared; it does not depend on SSMS.
    /// </summary>
    public static class AdvisorReport
    {
        public static string BuildHtml(PlanDocument document, AdvisorResult result, AdvisorServerInfo server,
            string annotatedQuery, string indexAdvice, ComparisonResult comparison)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<title>SSMS Plus query report - ").Append(E(document?.Source)).AppendLine("</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1e1e1e;background:#fff;margin:24px;max-width:1200px}");
            sb.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 8px;border-bottom:1px solid #ddd;padding-bottom:4px}h3{font-size:15px;margin:16px 0 6px}");
            sb.AppendLine(".meta{color:#666;margin-bottom:16px}table{border-collapse:collapse;width:100%;margin:8px 0}th,td{border:1px solid #ddd;padding:5px 8px;text-align:left;vertical-align:top}th{background:#f3f3f3}");
            sb.AppendLine("td.num{text-align:right;white-space:nowrap}pre{background:#f6f8fa;border:1px solid #e1e4e8;padding:10px;overflow:auto;font-family:Consolas,monospace;font-size:12.5px;white-space:pre-wrap}");
            sb.AppendLine(".sev{font-weight:600;white-space:nowrap}.High{color:#c42b1c}.Medium{color:#b86e00}.Info{color:#1f6fc5}.finding{border:1px solid #ddd;border-left:4px solid #999;padding:8px 12px;margin:10px 0}");
            sb.AppendLine(".finding.High{border-left-color:#c42b1c}.finding.Medium{border-left-color:#e89a1c}.finding.Info{border-left-color:#3b8eea}.finding.High,.finding.Medium,.finding.Info{color:#1e1e1e}pre{color:#1e1e1e}.finding p{margin:6px 0;color:#1e1e1e}.label{font-weight:600}");
            sb.AppendLine(".verdict{border-left:5px solid #888;padding:6px 14px;margin:10px 0 16px}.verdict .v{font-size:22px;font-weight:700}.verdict .s{font-size:15px;margin:2px 0 6px}.better{color:#2e8540}.worse{color:#c42b1c}.mixed{color:#b86e00}.same{color:#666}");
            sb.AppendLine(".cards{display:flex;flex-wrap:wrap;gap:12px;margin:8px 0 14px}.card{border:1px solid #ddd;border-radius:4px;padding:10px 12px;width:250px}.card.primary{border:2px solid #888}.card .t{font-weight:600}.card .k{font-size:11px;color:#666;font-style:italic}.card .c{font-size:17px;font-weight:700;margin:2px 0 6px}");
            sb.AppendLine(".bar{display:flex;align-items:center;gap:6px;margin:2px 0}.bar span.n{width:46px;color:#666}.bar span:last-child{white-space:nowrap}.bar span.b{display:inline-block;height:12px;border-radius:2px;min-width:2px}.ok{color:#2e8540;font-weight:700}.ko{color:#c42b1c;font-weight:700}ul.changes{list-style:none;padding-left:0}ul.changes li{margin:3px 0}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<h1>SSMS Plus query report</h1>");
            sb.Append("<div class=\"meta\">").Append(E(document?.Source)).Append(" &middot; ")
              .Append(document != null && document.HasActual ? "actual plan" : "estimated plan").Append(" &middot; ")
              .Append(E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            if (server != null)
                sb.Append(" &middot; statistics and indexes read from ").Append(E(server.Server));
            sb.AppendLine("</div>");

            // Statements
            sb.AppendLine("<h2>Statements</h2>");
            sb.AppendLine("<table><tr><th>#</th><th>Statement</th><th>Est. cost</th><th>Est. rows</th><th>Actual rows</th><th>Elapsed ms</th><th>Logical reads</th><th>Findings</th></tr>");
            if (document != null)
            {
                foreach (var s in document.Statements)
                {
                    var findings = result?.Findings.Where(f => f.Statement == s).ToList();
                    sb.Append("<tr><td class=\"num\">").Append(s.Index).Append("</td><td>").Append(E(Short(s.Text, 160))).Append("</td>")
                      .Append("<td class=\"num\">").Append(s.SubtreeCost.ToString("0.###", CultureInfo.InvariantCulture)).Append("</td>")
                      .Append("<td class=\"num\">").Append(Count(s.EstimatedRows)).Append("</td>")
                      .Append("<td class=\"num\">").Append(s.HasActual ? Count(s.Root?.ActualRows ?? 0) : string.Empty).Append("</td>")
                      .Append("<td class=\"num\">").Append(s.ElapsedTimeMs.HasValue ? Count(s.ElapsedTimeMs.Value) : string.Empty).Append("</td>")
                      .Append("<td class=\"num\">").Append(s.LogicalReads.HasValue ? Count(s.LogicalReads.Value) : string.Empty).Append("</td>")
                      .Append("<td>").Append(E(findings == null ? string.Empty : QueryAdvisor.Summary(findings))).AppendLine("</td></tr>");
                }
            }
            sb.AppendLine("</table>");

            // Findings
            sb.AppendLine("<h2>Advisor findings</h2>");
            if (result == null || result.Findings.Count == 0)
            {
                sb.AppendLine("<p>No findings.</p>");
            }
            else
            {
                sb.Append("<p>").Append(E(QueryAdvisor.Summary(result.Findings))).AppendLine("</p>");
                foreach (var f in result.Findings)
                {
                    sb.Append("<div class=\"finding ").Append(f.Severity).AppendLine("\">");
                    sb.Append("<div><span class=\"sev ").Append(f.Severity).Append("\">").Append(f.Severity).Append("</span> &nbsp; <b>")
                      .Append(E(f.RuleId)).Append(" &nbsp;").Append(E(f.Title)).Append("</b> &middot; ").Append(E(f.Category))
                      .Append(" &middot; statement ").Append(f.StatementIndex);
                    if (!string.IsNullOrEmpty(f.Location))
                        sb.Append(" &middot; ").Append(E(f.Location));
                    sb.AppendLine("</div>");
                    if (!string.IsNullOrEmpty(f.Object))
                        sb.Append("<p>").Append(E(f.Object)).AppendLine("</p>");
                    if (!string.IsNullOrEmpty(f.Explanation))
                        sb.Append("<p><span class=\"label\">Why:</span> ").Append(Paragraphs(f.Explanation)).AppendLine("</p>");
                    if (!string.IsNullOrEmpty(f.Suggestion))
                        sb.Append("<p><span class=\"label\">Suggestion:</span> ").Append(Paragraphs(f.Suggestion)).AppendLine("</p>");
                    if (!string.IsNullOrEmpty(f.Script))
                        sb.Append("<pre>").Append(E(f.Script)).AppendLine("</pre>");
                    sb.AppendLine("</div>");
                }
            }

            Section(sb, "Annotated query", annotatedQuery);
            Section(sb, "Index advice", indexAdvice);
            if (comparison != null && comparison.Before != null)
                ComparisonSection(sb, comparison);

            sb.AppendLine("<p class=\"meta\">Generated by SSMS Plus. Review every script before running it.</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>The comparison as a page: verdict, bars before / after, what changed, details on request.</summary>
        private static void ComparisonSection(StringBuilder sb, ComparisonResult r)
        {
            string cls = r.Trend == 2 ? "mixed" : r.Trend > 0 ? "better" : r.Trend < 0 ? "worse" : "same";
            string color = Color(r.Trend);
            sb.AppendLine("<h2>Compare</h2>");
            sb.Append("<div class=\"verdict\" style=\"border-left-color:").Append(color).AppendLine("\">");
            sb.Append("<div class=\"v ").Append(cls).Append("\">").Append(E(r.Overall.ToUpperInvariant())).AppendLine("</div>");
            if (!string.IsNullOrEmpty(r.Summary))
                sb.Append("<div class=\"s\">").Append(E(r.Summary)).AppendLine("</div>");
            sb.Append("<div class=\"meta\" style=\"margin:0\">Before: ").Append(E(r.Before.Label)).Append("<br>After: ").Append(E(r.After.Label)).AppendLine("</div></div>");

            if (r.Kpis.Count > 0)
            {
                sb.AppendLine("<div class=\"cards\">");
                foreach (var k in r.Kpis)
                {
                    double max = Math.Max(k.Before ?? 0, k.After ?? 0);
                    string after = k.Trend == 0 ? "#3b8eea" : Color(k.Trend);
                    sb.Append("<div class=\"card").Append(k.IsPrimary ? " primary" : string.Empty).Append("\"><div class=\"t\">").Append(E(k.Label)).Append("</div>");
                    if (k.IsPrimary)
                        sb.Append("<div class=\"k\">decides the result</div>");
                    sb.Append("<div class=\"c\" style=\"color:").Append(k.Trend == 0 ? "#666" : Color(k.Trend)).Append("\">")
                      .Append(E(string.IsNullOrEmpty(k.Change) ? "-" : k.Change)).Append(k.Trend > 0 ? " better" : k.Trend < 0 ? " worse" : string.Empty).Append("</div>");
                    Bar(sb, "Before", k.Before, k.BeforeText, max, "#999");
                    Bar(sb, "After", k.After, k.AfterText, max, after);
                    sb.AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }

            if (r.Good.Count + r.Bad.Count + r.Notes.Count > 0)
            {
                sb.AppendLine("<h3>What changed</h3><ul class=\"changes\">");
                foreach (var g in r.Good)
                    sb.Append("<li><span class=\"ok\">&#10003;</span> ").Append(E(g)).AppendLine("</li>");
                foreach (var b in r.Bad)
                    sb.Append("<li><span class=\"ko\">&#10007;</span> ").Append(E(b)).AppendLine("</li>");
                foreach (var n in r.Notes)
                    sb.Append("<li><i>").Append(E(n)).AppendLine("</i></li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("<details><summary>Details per statement</summary>");
            foreach (var s in r.Statements)
            {
                sb.Append("<h3>Statement ").Append(s.Index).Append(" &middot; ");
                if (s.IsNew)
                    sb.Append("new in After");
                else if (s.IsRemoved)
                    sb.Append("only in Before");
                else
                    sb.Append("<span style=\"color:").Append(Color(s.Trend)).Append("\">").Append(PlanComparer.Verdict(s.Trend)).Append("</span>");
                sb.AppendLine("</h3>");
                sb.Append("<div class=\"meta\">").Append(E(Short(s.Text, 300))).AppendLine("</div>");
                if (s.IsNew || s.IsRemoved)
                    continue;

                sb.AppendLine("<table><tr><th>Measure</th><th>Before</th><th>After</th><th>Change</th></tr>");
                foreach (var m in s.Measures)
                {
                    sb.Append("<tr><td>").Append(E(m.Label)).Append("</td><td class=\"num\">").Append(E(m.Before)).Append("</td><td class=\"num\">").Append(E(m.After))
                      .Append("</td><td style=\"color:").Append(m.Trend == 0 ? "inherit" : Color(m.Trend)).Append("\">").Append(E(m.Change))
                      .Append(m.Trend > 0 ? " better" : m.Trend < 0 ? " worse" : string.Empty).AppendLine("</td></tr>");
                }
                sb.AppendLine("</table>");
                if (s.Access.Count > 0)
                {
                    sb.AppendLine("<p class=\"label\">Tables read differently</p><ul class=\"changes\">");
                    foreach (var c in s.Access)
                    {
                        sb.Append("<li><span style=\"color:").Append(c.Trend == 0 ? "#666" : Color(c.Trend)).Append(";font-weight:700\">").Append(c.Trend > 0 ? "&#10003;" : c.Trend < 0 ? "&#10007;" : "~")
                          .Append("</span> <b>").Append(E(c.Table)).Append("</b>: ").Append(E(c.Before)).Append(" &rarr; ").Append(E(c.After))
                          .Append("<br><span class=\"meta\">").Append(E(c.Meaning)).AppendLine("</span></li>");
                    }
                    sb.AppendLine("</ul>");
                }
                sb.Append("<p><span class=\"label\">Advisor findings:</span> ").Append(E(s.FindingsBefore)).Append(" &rarr; ").Append(E(s.FindingsAfter)).AppendLine("</p>");
                FindingItems(sb, "Fixed", s.Fixed, "#2e8540");
                FindingItems(sb, "New", s.New, "#c42b1c");
                FindingItems(sb, "Still open", s.Open, "#666");
            }
            sb.AppendLine("</details>");
        }

        private static void FindingItems(StringBuilder sb, string title, System.Collections.Generic.List<AdvisorFinding> list, string color)
        {
            if (list.Count == 0)
                return;
            sb.Append("<p style=\"margin:4px 0;color:").Append(color).Append(";font-weight:600\">").Append(title).Append(" (").Append(list.Count).AppendLine(")</p><ul>");
            foreach (var f in list)
                sb.Append("<li>").Append(E(PlanComparer.FindingText(f))).AppendLine("</li>");
            sb.AppendLine("</ul>");
        }

        private static void Bar(StringBuilder sb, string label, double? value, string text, double max, string color)
        {
            double w = max > 0 && value.HasValue ? Math.Max(2, 120 * value.Value / max) : 2;
            sb.Append("<div class=\"bar\"><span class=\"n\">").Append(label).Append("</span><span class=\"b\" style=\"width:")
              .Append(w.ToString("0", CultureInfo.InvariantCulture)).Append("px;background:").Append(color).Append("\"></span><span>").Append(E(text)).AppendLine("</span></div>");
        }

        private static string Color(int trend) => trend == 2 ? "#b86e00" : trend > 0 ? "#2e8540" : trend < 0 ? "#c42b1c" : "#666";

        private static void Section(StringBuilder sb, string title, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            sb.Append("<h2>").Append(E(title)).AppendLine("</h2>");
            sb.Append("<pre>").Append(E(text)).AppendLine("</pre>");
        }

        private static string Paragraphs(string text)
        {
            return E(text).Replace("\r\n", "\n").Replace("\n", "<br>");
        }

        private static string E(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

        private static string Count(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string Short(string text, int max)
        {
            var t = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (t.Contains("  "))
                t = t.Replace("  ", " ");
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }
    }
}
