namespace SSMSPlus.Features.ExecutionPlan
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Xml.Linq;

    /// <summary>Visual family of an operator, used for its color and shape (DB2 Visual Explain style).</summary>
    public enum PlanNodeCategory
    {
        Root,
        Scan,
        Seek,
        Lookup,
        Join,
        Sort,
        Aggregate,
        Spool,
        Parallelism,
        Other,
        Table,
        Index,
    }

    public sealed class PlanProperty
    {
        public PlanProperty(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public string Value { get; }
    }

    /// <summary>A column referenced by an operator (predicate, seek or output list).</summary>
    public sealed class PlanColumn
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }
        public string Alias { get; set; }
        public string Column { get; set; }

        /// <summary>Comparison the column takes part in (EQ, LT, GT, ...), or null.</summary>
        public string CompareOp { get; set; }

        /// <summary>True when the column is inside a function / conversion / calculation (not sargable).</summary>
        public bool Wrapped { get; set; }

        public string Name => PlanNode.Clean(Column);

        public bool BelongsTo(PlanNode node)
        {
            if (node == null || string.IsNullOrEmpty(Column))
                return false;
            if (!string.IsNullOrEmpty(Table))
                return string.Equals(PlanNode.Clean(Table), PlanNode.Clean(node.ObjectTable), StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(Alias) || string.IsNullOrEmpty(node.ObjectAlias) || string.Equals(PlanNode.Clean(Alias), PlanNode.Clean(node.ObjectAlias), StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(Alias) && string.Equals(PlanNode.Clean(Alias), PlanNode.Clean(node.ObjectAlias), StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => string.Join(".", new[] { Alias ?? Table, Column }.Where(x => !string.IsNullOrEmpty(x)).Select(PlanNode.Clean));
    }

    /// <summary>A statistics object the optimizer used (showplan OptimizerStatsUsage, SQL Server 2017+).</summary>
    public sealed class PlanStatisticsUsage
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }
        public string Statistics { get; set; }
        public double? ModificationCount { get; set; }
        public double? SamplingPercent { get; set; }
        public DateTime? LastUpdate { get; set; }
    }

    /// <summary>A PlanAffectingConvert warning of the statement.</summary>
    public sealed class PlanConvertIssue
    {
        /// <summary>"Seek Plan" or "Cardinality Estimate".</summary>
        public string Issue { get; set; }
        public string Expression { get; set; }
    }

    public sealed class PlanParameter
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string CompiledValue { get; set; }
        public string RuntimeValue { get; set; }
    }

    /// <summary>One node of the tree: an operator (RelOp), the statement root, or a table / index leaf.</summary>
    public sealed class PlanNode
    {
        public int NodeId { get; set; } = -1;
        public string PhysicalOp { get; set; }
        public string LogicalOp { get; set; }
        public PlanNodeCategory Category { get; set; }
        public bool IsObject => Category == PlanNodeCategory.Table || Category == PlanNodeCategory.Index;
        public bool IsRoot => Category == PlanNodeCategory.Root;

        public double EstimateRows { get; set; }
        public double EstimateRowsAllExecutions { get; set; }
        public double? EstimatedRowsRead { get; set; }
        public double EstimateExecutions { get; set; } = 1;
        public double EstimateIO { get; set; }
        public double EstimateCPU { get; set; }
        public double SubtreeCost { get; set; }
        public double OwnCost { get; set; }
        public double CostPercent { get; set; }
        public double AvgRowSize { get; set; }
        public bool Parallel { get; set; }
        public string ExecutionMode { get; set; }

        public bool HasActual { get; set; }
        public double ActualRows { get; set; }
        public double ActualExecutions { get; set; }
        public double? ActualRowsRead { get; set; }
        public double? ActualElapsedMs { get; set; }
        public double? ActualCpuMs { get; set; }
        public double? ActualLogicalReads { get; set; }
        public double? ActualPhysicalReads { get; set; }

        /// <summary>Actual / estimated rows (all executions); >1 means more rows than estimated.</summary>
        public double? RowEstimateRatio
        {
            get
            {
                if (!HasActual)
                    return null;
                return Math.Max(ActualRows, 1) / Math.Max(EstimateRowsAllExecutions, 1);
            }
        }

        public bool BadRowEstimate
        {
            get
            {
                var r = RowEstimateRatio;
                return r.HasValue && (r.Value >= 10 || r.Value <= 0.1) && Math.Max(ActualRows, EstimateRowsAllExecutions) >= 100;
            }
        }

        public string ObjectDatabase { get; set; }
        public string ObjectSchema { get; set; }
        public string ObjectTable { get; set; }
        public string ObjectIndex { get; set; }
        public string ObjectAlias { get; set; }
        public string IndexKind { get; set; }

        public string ObjectDisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(ObjectTable))
                    return null;
                var name = Clean(ObjectTable);
                if (!string.IsNullOrEmpty(ObjectIndex))
                    name += "." + Clean(ObjectIndex);
                return name;
            }
        }

        public string SeekPredicate { get; set; }
        public string Predicate { get; set; }
        public string OutputList { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Rows in the table read by this operator (RelOp TableCardinality), when the plan has it.</summary>
        public double? TableCardinality { get; set; }
        public List<PlanColumn> PredicateColumns { get; } = new List<PlanColumn>();
        public List<PlanColumn> SeekColumns { get; } = new List<PlanColumn>();
        public List<PlanColumn> OutputColumns { get; } = new List<PlanColumn>();
        public bool HasSpill { get; set; }
        public int SpillLevel { get; set; }
        public bool NoJoinPredicate { get; set; }
        public List<PlanColumn> ColumnsWithNoStatistics { get; } = new List<PlanColumn>();

        /// <summary>The statement this node belongs to (set by the parser).</summary>
        public PlanStatement Statement { get; set; }

        /// <summary>Text of the statement (root node only).</summary>
        public string StatementText { get; set; }

        public PlanNode Parent { get; set; }
        public List<PlanNode> Children { get; } = new List<PlanNode>();

        /// <summary>Short label shown in the node: the operator, or the table / index name.</summary>
        public string Title
        {
            get
            {
                switch (Category)
                {
                    case PlanNodeCategory.Table:
                        return Clean(ObjectTable);
                    case PlanNodeCategory.Index:
                        return Clean(ObjectIndex);
                    case PlanNodeCategory.Root:
                        return (PhysicalOp ?? "QUERY").ToUpperInvariant();
                    default:
                        return PhysicalOp;
                }
            }
        }

        public string AllWarningsText => string.Join(Environment.NewLine, AllWarnings());

        public IEnumerable<string> AllWarnings()
        {
            foreach (var w in Warnings)
                yield return w;
            if (BadRowEstimate)
                yield return string.Format(CultureInfo.InvariantCulture, "Row estimate is off: estimated {0:N0}, actual {1:N0}", EstimateRowsAllExecutions, ActualRows);
        }

        public bool HasWarnings => Warnings.Count > 0 || BadRowEstimate;

        public IEnumerable<PlanNode> DescendantsAndSelf()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.DescendantsAndSelf())
                    yield return d;
        }

        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return name.Replace("[", string.Empty).Replace("]", string.Empty);
        }

        public List<PlanProperty> GetProperties()
        {
            var p = new List<PlanProperty>();
            void Add(string n, string v)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    p.Add(new PlanProperty(n, v));
            }

            string Num(double v) => v.ToString(v >= 100 ? "N0" : "0.######", CultureInfo.CurrentCulture);

            if (IsRoot)
            {
                Add("Statement", StatementText);
                Add("Statement type", PhysicalOp);
                Add("Estimated subtree cost", Num(SubtreeCost));
                Add("Estimated rows", Num(EstimateRows));
                if (HasActual)
                    Add("Actual rows", Num(ActualRows));
                foreach (var w in AllWarnings())
                    Add("Warning", w);
                return p;
            }

            if (IsObject)
            {
                Add("Database", Clean(ObjectDatabase));
                Add("Schema", Clean(ObjectSchema));
                Add("Table", Clean(ObjectTable));
                Add("Index", Clean(ObjectIndex));
                Add("Index kind", IndexKind);
                Add("Alias", Clean(ObjectAlias));
                return p;
            }

            Add("Node", NodeId.ToString(CultureInfo.InvariantCulture));
            Add("Physical operation", PhysicalOp);
            Add("Logical operation", LogicalOp);
            Add("Object", ObjectDisplayName == null ? null : (string.IsNullOrEmpty(ObjectSchema) ? string.Empty : Clean(ObjectSchema) + ".") + ObjectDisplayName);
            Add("Cost (this operator)", string.Format(CultureInfo.CurrentCulture, "{0} ({1:0.#}%)", Num(OwnCost), CostPercent));
            Add("Estimated subtree cost", Num(SubtreeCost));
            Add("Estimated I/O cost", Num(EstimateIO));
            Add("Estimated CPU cost", Num(EstimateCPU));
            Add("Estimated rows per execution", Num(EstimateRows));
            Add("Estimated rows (all executions)", Num(EstimateRowsAllExecutions));
            if (EstimatedRowsRead.HasValue)
                Add("Estimated rows to be read", Num(EstimatedRowsRead.Value));
            Add("Estimated executions", Num(EstimateExecutions));
            Add("Estimated row size", Num(AvgRowSize) + " B");
            if (HasActual)
            {
                Add("Actual rows", Num(ActualRows));
                if (ActualRowsRead.HasValue)
                    Add("Actual rows read", Num(ActualRowsRead.Value));
                Add("Actual executions", Num(ActualExecutions));
                if (ActualElapsedMs.HasValue)
                    Add("Actual elapsed time", Num(ActualElapsedMs.Value) + " ms");
                if (ActualCpuMs.HasValue)
                    Add("Actual CPU time", Num(ActualCpuMs.Value) + " ms");
                if (ActualLogicalReads.HasValue)
                    Add("Actual logical reads", Num(ActualLogicalReads.Value));
                if (ActualPhysicalReads.HasValue)
                    Add("Actual physical reads", Num(ActualPhysicalReads.Value));
                if (RowEstimateRatio.HasValue)
                    Add("Actual / estimated rows", RowEstimateRatio.Value.ToString("0.##", CultureInfo.CurrentCulture) + "x");
            }
            Add("Execution mode", ExecutionMode);
            Add("Parallel", Parallel ? "Yes" : null);
            Add("Seek predicate", SeekPredicate);
            Add("Predicate", Predicate);
            Add("Output", OutputList);
            foreach (var w in AllWarnings())
                Add("Warning", w);
            return p;
        }
    }

    public sealed class MissingIndexInfo
    {
        public double Impact { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Table { get; set; }
        public List<string> EqualityColumns { get; } = new List<string>();
        public List<string> InequalityColumns { get; } = new List<string>();
        public List<string> IncludeColumns { get; } = new List<string>();

        public string ToCreateIndex()
        {
            var keys = EqualityColumns.Concat(InequalityColumns).ToList();
            var table = PlanNode.Clean(Table);
            var name = "IX_" + table + "_" + string.Join("_", keys.Select(PlanNode.Clean));
            if (name.Length > 120)
                name = name.Substring(0, 120);

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "-- Missing index, estimated impact {0:0.#}%{1}", Impact, Environment.NewLine);
            sb.AppendFormat("CREATE NONCLUSTERED INDEX [{0}]{1}", name, Environment.NewLine);
            sb.AppendFormat("ON {0}.{1}.{2} ({3})", Database, Schema, Table, string.Join(", ", keys));
            if (IncludeColumns.Count > 0)
                sb.Append(Environment.NewLine).AppendFormat("INCLUDE ({0})", string.Join(", ", IncludeColumns));
            sb.Append(';');
            return sb.ToString();
        }
    }

    public sealed class PlanStatement
    {
        public int Index { get; set; }
        public string Text { get; set; }
        public string StatementType { get; set; }
        public double SubtreeCost { get; set; }
        public double EstimatedRows { get; set; }
        public bool HasActual { get; set; }
        public PlanNode Root { get; set; }
        public List<MissingIndexInfo> MissingIndexes { get; } = new List<MissingIndexInfo>();
        public List<string> Warnings { get; } = new List<string>();
        public string DegreeOfParallelism { get; set; }
        public string CompileTime { get; set; }
        public string MemoryGrantKb { get; set; }
        public double? RequestedMemoryKb { get; set; }
        public double? GrantedMemoryKb { get; set; }
        public double? MaxUsedMemoryKb { get; set; }
        public string MemoryGrantWarning { get; set; }

        /// <summary>QueryTimeStats of actual plans (SQL Server 2016 SP1+), in ms.</summary>
        public double? CpuTimeMs { get; set; }
        public double? ElapsedTimeMs { get; set; }

        /// <summary>Sum of the actual logical reads of the operators, when the plan has them.</summary>
        public double? LogicalReads => Root == null || !Operators().Any(o => o.ActualLogicalReads.HasValue) ? (double?)null : Operators().Sum(o => o.ActualLogicalReads ?? 0);
        public List<PlanStatisticsUsage> StatisticsUsage { get; } = new List<PlanStatisticsUsage>();
        public List<PlanConvertIssue> Converts { get; } = new List<PlanConvertIssue>();
        public List<PlanParameter> Parameters { get; } = new List<PlanParameter>();

        public string ShortText
        {
            get
            {
                var t = (Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
                while (t.Contains("  "))
                    t = t.Replace("  ", " ");
                if (t.Length > 140)
                    t = t.Substring(0, 140) + "...";
                return string.Format(CultureInfo.CurrentCulture, "{0}. [{1:0.###}] {2}", Index, SubtreeCost, t);
            }
        }

        public override string ToString() => ShortText;

        public IEnumerable<PlanNode> Operators() => Root == null ? Enumerable.Empty<PlanNode>() : Root.DescendantsAndSelf().Where(n => !n.IsObject && !n.IsRoot);
    }

    public sealed class PlanDocument
    {
        public List<PlanStatement> Statements { get; } = new List<PlanStatement>();
        public string Xml { get; set; }
        public string Source { get; set; }
        public bool HasActual => Statements.Any(s => s.HasActual);
    }

    /// <summary>Parses SQL Server showplan XML (estimated or actual) into <see cref="PlanDocument"/>.</summary>
    public static class ShowPlanParser
    {
        public static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

        public static bool LooksLikeShowPlan(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && text.IndexOf("<ShowPlanXML", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Parses one or more ShowPlanXML documents (one per batch).</summary>
        public static PlanDocument Parse(IEnumerable<string> xmlDocuments, string source)
        {
            var doc = new PlanDocument { Source = source };
            var xmlText = new StringBuilder();
            foreach (var xml in xmlDocuments)
            {
                if (string.IsNullOrWhiteSpace(xml))
                    continue;
                var x = XDocument.Parse(xml.Trim().TrimStart('﻿'));
                if (xmlText.Length > 0)
                    xmlText.AppendLine().AppendLine();
                xmlText.Append(x.ToString());
                foreach (var stmt in x.Descendants(Ns + "StmtSimple"))
                {
                    var queryPlan = stmt.Element(Ns + "QueryPlan");
                    if (queryPlan == null)
                        continue;
                    var s = ParseStatement(stmt, queryPlan);
                    s.Index = doc.Statements.Count + 1;
                    doc.Statements.Add(s);
                }
            }

            doc.Xml = xmlText.ToString();
            return doc;
        }

        public static PlanDocument Parse(string xml, string source) => Parse(new[] { xml }, source);

        private static PlanStatement ParseStatement(XElement stmt, XElement queryPlan)
        {
            var s = new PlanStatement
            {
                Text = (string)stmt.Attribute("StatementText"),
                StatementType = (string)stmt.Attribute("StatementType"),
                SubtreeCost = D(stmt, "StatementSubTreeCost"),
                EstimatedRows = D(stmt, "StatementEstRows"),
                DegreeOfParallelism = (string)queryPlan.Attribute("DegreeOfParallelism"),
                CompileTime = (string)queryPlan.Attribute("CompileTime"),
            };

            var grant = queryPlan.Element(Ns + "MemoryGrantInfo");
            if (grant != null)
            {
                s.MemoryGrantKb = (string)grant.Attribute("GrantedMemory") ?? (string)grant.Attribute("SerialDesiredMemory");
                s.RequestedMemoryKb = N(grant, "RequestedMemory") ?? N(grant, "SerialDesiredMemory");
                s.GrantedMemoryKb = N(grant, "GrantedMemory");
                s.MaxUsedMemoryKb = N(grant, "MaxUsedMemory");
            }

            var planWarnings = queryPlan.Element(Ns + "Warnings");
            if (planWarnings != null)
            {
                s.Warnings.AddRange(DescribeWarnings(planWarnings));
                foreach (var c in planWarnings.Elements(Ns + "PlanAffectingConvert"))
                    s.Converts.Add(new PlanConvertIssue { Issue = (string)c.Attribute("ConvertIssue"), Expression = (string)c.Attribute("Expression") });
                var mgw = planWarnings.Element(Ns + "MemoryGrantWarning");
                if (mgw != null)
                    s.MemoryGrantWarning = (string)mgw.Attribute("GrantWarningKind");
            }

            var timeStats = queryPlan.Element(Ns + "QueryTimeStats");
            if (timeStats != null)
            {
                s.CpuTimeMs = N(timeStats, "CpuTime");
                s.ElapsedTimeMs = N(timeStats, "ElapsedTime");
            }

            var statsUsage = queryPlan.Element(Ns + "OptimizerStatsUsage");
            if (statsUsage != null)
            {
                foreach (var si in statsUsage.Elements(Ns + "StatisticsInfo"))
                {
                    DateTime lastUpdate;
                    s.StatisticsUsage.Add(new PlanStatisticsUsage
                    {
                        Database = (string)si.Attribute("Database"),
                        Schema = (string)si.Attribute("Schema"),
                        Table = (string)si.Attribute("Table"),
                        Statistics = (string)si.Attribute("Statistics"),
                        ModificationCount = N(si, "ModificationCount"),
                        SamplingPercent = N(si, "SamplingPercent"),
                        LastUpdate = DateTime.TryParse((string)si.Attribute("LastUpdate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out lastUpdate) ? (DateTime?)lastUpdate : null,
                    });
                }
            }

            var parameters = queryPlan.Element(Ns + "ParameterList");
            if (parameters != null)
            {
                foreach (var pr in parameters.Elements(Ns + "ColumnReference"))
                {
                    s.Parameters.Add(new PlanParameter
                    {
                        Name = (string)pr.Attribute("Column"),
                        DataType = (string)pr.Attribute("ParameterDataType"),
                        CompiledValue = (string)pr.Attribute("ParameterCompiledValue"),
                        RuntimeValue = (string)pr.Attribute("ParameterRuntimeValue"),
                    });
                }
            }

            foreach (var group in queryPlan.Descendants(Ns + "MissingIndexGroup"))
            {
                foreach (var mi in group.Elements(Ns + "MissingIndex"))
                {
                    var info = new MissingIndexInfo
                    {
                        Impact = D(group, "Impact"),
                        Database = (string)mi.Attribute("Database"),
                        Schema = (string)mi.Attribute("Schema"),
                        Table = (string)mi.Attribute("Table"),
                    };
                    foreach (var cg in mi.Elements(Ns + "ColumnGroup"))
                    {
                        var cols = cg.Elements(Ns + "Column").Select(c => (string)c.Attribute("Name")).Where(n => n != null);
                        switch ((string)cg.Attribute("Usage"))
                        {
                            case "EQUALITY": info.EqualityColumns.AddRange(cols); break;
                            case "INEQUALITY": info.InequalityColumns.AddRange(cols); break;
                            default: info.IncludeColumns.AddRange(cols); break;
                        }
                    }
                    s.MissingIndexes.Add(info);
                }
            }

            var root = new PlanNode
            {
                Category = PlanNodeCategory.Root,
                PhysicalOp = s.StatementType,
                StatementText = s.Text,
                SubtreeCost = s.SubtreeCost,
                EstimateRows = s.EstimatedRows,
                EstimateRowsAllExecutions = s.EstimatedRows,
            };
            root.Warnings.AddRange(s.Warnings);
            foreach (var mi in s.MissingIndexes)
                root.Warnings.Add(string.Format(CultureInfo.InvariantCulture, "Missing index on {0}.{1} (impact {2:0.#}%)", PlanNode.Clean(mi.Schema), PlanNode.Clean(mi.Table), mi.Impact));

            var relOp = queryPlan.Element(Ns + "RelOp");
            if (relOp != null)
            {
                var top = ParseRelOp(relOp, s.SubtreeCost > 0 ? s.SubtreeCost : D(relOp, "EstimatedTotalSubtreeCost"));
                top.Parent = root;
                root.Children.Add(top);
                if (top.HasActual)
                {
                    root.HasActual = true;
                    root.ActualRows = top.ActualRows;
                    s.HasActual = true;
                }
            }

            s.Root = root;
            foreach (var n in root.DescendantsAndSelf())
                n.Statement = s;
            return s;
        }

        private static PlanNode ParseRelOp(XElement relOp, double statementCost)
        {
            var node = new PlanNode
            {
                NodeId = (int)D(relOp, "NodeId"),
                PhysicalOp = (string)relOp.Attribute("PhysicalOp"),
                LogicalOp = (string)relOp.Attribute("LogicalOp"),
                EstimateRows = D(relOp, "EstimateRows"),
                EstimateIO = D(relOp, "EstimateIO"),
                EstimateCPU = D(relOp, "EstimateCPU"),
                SubtreeCost = D(relOp, "EstimatedTotalSubtreeCost"),
                AvgRowSize = D(relOp, "AvgRowSize"),
                Parallel = (string)relOp.Attribute("Parallel") == "1" || (string)relOp.Attribute("Parallel") == "true",
                ExecutionMode = (string)relOp.Attribute("EstimatedExecutionMode"),
            };

            if (relOp.Attribute("EstimatedRowsRead") != null)
                node.EstimatedRowsRead = D(relOp, "EstimatedRowsRead");
            node.TableCardinality = N(relOp, "TableCardinality");

            node.EstimateExecutions = 1 + D(relOp, "EstimateRebinds") + D(relOp, "EstimateRewinds");
            node.EstimateRowsAllExecutions = node.EstimateRows * Math.Max(1, node.EstimateExecutions);

            var runtime = relOp.Element(Ns + "RunTimeInformation");
            if (runtime != null)
            {
                var threads = runtime.Elements(Ns + "RunTimeCountersPerThread").ToList();
                if (threads.Count > 0)
                {
                    node.HasActual = true;
                    node.ActualRows = threads.Sum(t => D(t, "ActualRows"));
                    node.ActualExecutions = threads.Sum(t => D(t, "ActualExecutions"));
                    node.ActualRowsRead = SumOrNull(threads, "ActualRowsRead");
                    node.ActualElapsedMs = MaxOrNull(threads, "ActualElapsedms");
                    node.ActualCpuMs = SumOrNull(threads, "ActualCPUms");
                    node.ActualLogicalReads = SumOrNull(threads, "ActualLogicalReads");
                    node.ActualPhysicalReads = SumOrNull(threads, "ActualPhysicalReads");
                }
            }

            var warnings = relOp.Element(Ns + "Warnings");
            if (warnings != null)
            {
                node.Warnings.AddRange(DescribeWarnings(warnings));
                node.NoJoinPredicate = (string)warnings.Attribute("NoJoinPredicate") == "1" || (string)warnings.Attribute("NoJoinPredicate") == "true";
                foreach (var spill in warnings.Elements().Where(e => e.Name.LocalName.EndsWith("SpillDetails", StringComparison.Ordinal) || e.Name.LocalName == "SpillToTempDb"))
                {
                    node.HasSpill = true;
                    node.SpillLevel = Math.Max(node.SpillLevel, (int)D(spill, "SpillLevel"));
                }
                foreach (var noStats in warnings.Elements(Ns + "ColumnsWithNoStatistics"))
                    node.ColumnsWithNoStatistics.AddRange(noStats.Descendants(Ns + "ColumnReference").Select(c => ToColumn(c)));
            }

            // Operator-specific content: everything below this RelOp that is not inside a child RelOp.
            var childRelOps = new List<XElement>();
            XElement obj = null;
            var seek = new List<string>();
            string predicate = null;
            XElement outputList = relOp.Element(Ns + "OutputList");

            foreach (var element in relOp.Elements())
                Walk(element, childRelOps, ref obj, seek, ref predicate, node);

            if (obj != null)
            {
                node.ObjectDatabase = (string)obj.Attribute("Database");
                node.ObjectSchema = (string)obj.Attribute("Schema");
                node.ObjectTable = (string)obj.Attribute("Table");
                node.ObjectIndex = (string)obj.Attribute("Index");
                node.ObjectAlias = (string)obj.Attribute("Alias");
                node.IndexKind = (string)obj.Attribute("IndexKind");
            }

            if (seek.Count > 0)
                node.SeekPredicate = string.Join(" AND ", seek.Distinct());
            node.Predicate = predicate;

            if (outputList != null)
            {
                node.OutputList = string.Join(", ", outputList.Elements(Ns + "ColumnReference").Select(ColumnName));
                node.OutputColumns.AddRange(outputList.Elements(Ns + "ColumnReference").Select(c => ToColumn(c)));
            }

            node.Category = Categorize(node.PhysicalOp, node.LogicalOp);

            double childCost = 0;
            foreach (var childRelOp in childRelOps)
            {
                var child = ParseRelOp(childRelOp, statementCost);
                child.Parent = node;
                node.Children.Add(child);
                childCost += child.SubtreeCost;
            }

            node.OwnCost = Math.Max(0, node.SubtreeCost - childCost);
            node.CostPercent = statementCost > 0 ? Math.Min(100, node.OwnCost * 100 / statementCost) : 0;

            // Table and index as leaves under the operator that reads them, like DB2.
            if (!string.IsNullOrEmpty(node.ObjectTable) && IsDataAccess(node.PhysicalOp))
            {
                PlanNode leafParent = node;
                if (!string.IsNullOrEmpty(node.ObjectIndex) && !IsHeapAccess(node))
                {
                    var index = ObjectLeaf(node, PlanNodeCategory.Index);
                    index.Parent = node;
                    node.Children.Add(index);
                    leafParent = index;
                }

                var table = ObjectLeaf(node, PlanNodeCategory.Table);
                table.Parent = leafParent;
                leafParent.Children.Add(table);
            }

            return node;
        }

        private static PlanNode ObjectLeaf(PlanNode op, PlanNodeCategory category)
        {
            return new PlanNode
            {
                Category = category,
                ObjectDatabase = op.ObjectDatabase,
                ObjectSchema = op.ObjectSchema,
                ObjectTable = op.ObjectTable,
                ObjectIndex = category == PlanNodeCategory.Index ? op.ObjectIndex : null,
                ObjectAlias = op.ObjectAlias,
                IndexKind = category == PlanNodeCategory.Index ? op.IndexKind : null,
            };
        }

        private static bool IsHeapAccess(PlanNode n)
        {
            return string.Equals(n.PhysicalOp, "Table Scan", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n.PhysicalOp, "RID Lookup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(n.IndexKind, "Heap", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDataAccess(string physicalOp)
        {
            if (string.IsNullOrEmpty(physicalOp))
                return false;
            var op = physicalOp.ToLowerInvariant();
            return op.Contains("scan") || op.Contains("seek") || op.Contains("lookup")
                || op == "table insert" || op == "table update" || op == "table delete" || op == "table merge"
                || op.StartsWith("clustered index ") || op.StartsWith("index ");
        }

        private static void Walk(XElement element, List<XElement> childRelOps, ref XElement obj, List<string> seek, ref string predicate, PlanNode node)
        {
            var name = element.Name;
            if (name == Ns + "RelOp")
            {
                childRelOps.Add(element);
                return;
            }

            if (name == Ns + "RunTimeInformation" || name == Ns + "OutputList" || name == Ns + "Warnings" || name == Ns + "MemoryFractions")
                return;

            if (name == Ns + "Object" && obj == null)
                obj = element;

            if (name == Ns + "SeekPredicates" || name == Ns + "SeekPredicateNew" || name == Ns + "SeekPredicate")
            {
                foreach (var scalar in element.Descendants(Ns + "RangeColumns"))
                {
                    var range = scalar.Parent;
                    var cols = scalar.Elements(Ns + "ColumnReference").Select(ColumnName).ToList();
                    node.SeekColumns.AddRange(scalar.Elements(Ns + "ColumnReference").Select(c => ToColumn(c, ScanOperatorCode((string)range?.Attribute("ScanType")))));
                    var values = range?.Element(Ns + "RangeExpressions")?.Elements(Ns + "ScalarOperator").Select(v => (string)v.Attribute("ScalarString")).ToList();
                    var scanType = (string)range?.Attribute("ScanType") ?? "EQ";
                    for (int i = 0; i < cols.Count; i++)
                    {
                        var v = values != null && i < values.Count ? values[i] : "?";
                        seek.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", cols[i], ScanOperator(scanType), v));
                    }
                }
                return;
            }

            if (name == Ns + "Predicate" && predicate == null)
            {
                predicate = (string)element.Element(Ns + "ScalarOperator")?.Attribute("ScalarString");
                foreach (var c in element.Descendants(Ns + "ColumnReference"))
                {
                    if (c.Attribute("Column") == null || c.Ancestors(Ns + "RelOp").FirstOrDefault() != element.Ancestors(Ns + "RelOp").FirstOrDefault())
                        continue;
                    node.PredicateColumns.Add(PredicateColumn(c, element));
                }
                return;
            }

            foreach (var child in element.Elements())
                Walk(child, childRelOps, ref obj, seek, ref predicate, node);
        }

        private static string ScanOperatorCode(string scanType) => string.IsNullOrEmpty(scanType) ? "EQ" : scanType;

        private static PlanColumn ToColumn(XElement c, string compareOp = null)
        {
            return new PlanColumn
            {
                Database = (string)c.Attribute("Database"),
                Schema = (string)c.Attribute("Schema"),
                Table = (string)c.Attribute("Table"),
                Alias = (string)c.Attribute("Alias"),
                Column = (string)c.Attribute("Column"),
                CompareOp = compareOp,
            };
        }

        /// <summary>
        /// Column of a predicate with the comparison it is part of. Walks up to the nearest Compare / Like;
        /// anything else on the way (Convert, Intrinsic, Arithmetic, UDF...) means the column is wrapped.
        /// </summary>
        private static PlanColumn PredicateColumn(XElement columnReference, XElement predicate)
        {
            var column = ToColumn(columnReference);
            for (var e = columnReference.Parent; e != null && e != predicate; e = e.Parent)
            {
                var name = e.Name.LocalName;
                if (name == "Identifier" || name == "ScalarOperator")
                    continue;
                if (name == "Compare")
                {
                    column.CompareOp = (string)e.Attribute("CompareOp");
                    break;
                }
                if (name == "Intrinsic" && string.Equals((string)e.Attribute("FunctionName"), "like", StringComparison.OrdinalIgnoreCase))
                {
                    column.CompareOp = "LIKE";
                    break;
                }
                if (name == "Logical")
                    break;
                column.Wrapped = true;
            }
            return column;
        }

        private static string ScanOperator(string scanType)
        {
            switch (scanType)
            {
                case "EQ": return "=";
                case "LT": return "<";
                case "LE": return "<=";
                case "GT": return ">";
                case "GE": return ">=";
                case "NE": return "<>";
                case "IS": return "IS";
                case "IS NOT": return "IS NOT";
                default: return scanType;
            }
        }

        private static string ColumnName(XElement c)
        {
            var parts = new[] { (string)c.Attribute("Alias") ?? (string)c.Attribute("Table"), (string)c.Attribute("Column") }
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(PlanNode.Clean);
            return string.Join(".", parts);
        }

        private static IEnumerable<string> DescribeWarnings(XElement warnings)
        {
            foreach (var a in warnings.Attributes())
            {
                if (a.Value == "1" || a.Value == "true")
                {
                    switch (a.Name.LocalName)
                    {
                        case "NoJoinPredicate": yield return "No join predicate (possible cartesian product)"; break;
                        case "UnmatchedIndexes": yield return "Unmatched indexes (filtered index not used because of parameterization)"; break;
                        case "FullUpdateForOnlineIndexBuild": yield return "Full update for online index build"; break;
                        default: yield return a.Name.LocalName; break;
                    }
                }
            }

            foreach (var e in warnings.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "SpillToTempDb":
                        yield return string.Format(CultureInfo.InvariantCulture, "Spill to tempdb (level {0})", (string)e.Attribute("SpillLevel") ?? "?");
                        break;
                    case "SortSpillDetails":
                    case "HashSpillDetails":
                        yield return e.Name.LocalName.Replace("Details", " details") + ": " + string.Join(", ", e.Attributes().Select(x => x.Name.LocalName + "=" + x.Value));
                        break;
                    case "ColumnsWithNoStatistics":
                        yield return "Columns with no statistics: " + string.Join(", ", e.Descendants(Ns + "ColumnReference").Select(ColumnName));
                        break;
                    case "PlanAffectingConvert":
                        yield return string.Format("Implicit conversion may affect the plan ({0}): {1}", (string)e.Attribute("ConvertIssue"), (string)e.Attribute("Expression"));
                        break;
                    case "MemoryGrantWarning":
                        yield return string.Format("Memory grant warning: {0} (requested {1} KB, granted {2} KB, used {3} KB)",
                            (string)e.Attribute("GrantWarningKind"), (string)e.Attribute("RequestedMemory"), (string)e.Attribute("GrantedMemory"), (string)e.Attribute("MaxUsedMemory"));
                        break;
                    case "Wait":
                        yield return string.Format("Wait: {0} ({1} ms)", (string)e.Attribute("WaitType"), (string)e.Attribute("WaitTime"));
                        break;
                    default:
                        yield return e.Name.LocalName;
                        break;
                }
            }
        }

        public static PlanNodeCategory Categorize(string physicalOp, string logicalOp)
        {
            var op = (physicalOp ?? string.Empty).ToLowerInvariant();
            var logical = (logicalOp ?? string.Empty).ToLowerInvariant();

            if (op.Contains("lookup"))
                return PlanNodeCategory.Lookup;
            if (op.Contains("seek"))
                return PlanNodeCategory.Seek;
            if (op.Contains("scan"))
                return op.Contains("constant") ? PlanNodeCategory.Other : PlanNodeCategory.Scan;
            if (op.Contains("spool") || op.Contains("worktable"))
                return PlanNodeCategory.Spool;
            if (op == "parallelism" || logical.Contains("streams"))
                return PlanNodeCategory.Parallelism;
            if (op.Contains("sort"))
                return PlanNodeCategory.Sort;
            if (op.Contains("aggregate") || logical.Contains("aggregate"))
                return PlanNodeCategory.Aggregate;
            if (op.Contains("join") || op.Contains("nested loops") || op == "hash match" || logical.Contains("join") || logical.Contains("semi") || logical.Contains("union") || logical.Contains("concatenation"))
                return PlanNodeCategory.Join;
            return PlanNodeCategory.Other;
        }

        private static double D(XElement e, string attribute)
        {
            var a = e?.Attribute(attribute);
            if (a == null)
                return 0;
            double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
            return v;
        }

        private static double? N(XElement e, string attribute)
        {
            var a = e?.Attribute(attribute);
            if (a == null)
                return null;
            double v;
            return double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? (double?)v : null;
        }

        private static double? SumOrNull(List<XElement> threads, string attribute)
        {
            if (!threads.Any(t => t.Attribute(attribute) != null))
                return null;
            return threads.Sum(t => D(t, attribute));
        }

        private static double? MaxOrNull(List<XElement> threads, string attribute)
        {
            if (!threads.Any(t => t.Attribute(attribute) != null))
                return null;
            return threads.Max(t => D(t, attribute));
        }
    }
}
