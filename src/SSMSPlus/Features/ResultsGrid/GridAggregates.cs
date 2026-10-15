namespace SSMSPlus.Features.ResultsGrid
{
    using Microsoft.SqlServer.Management.UI.Grid;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    /// <summary>Count / Sum / Avg / Min / Max for one column (or for the whole selection).</summary>
    internal sealed class AggregateValues
    {
        public string Name { get; set; }
        public long Cells { get; set; }
        public long Nulls { get; set; }
        public long Numeric { get; set; }
        public long Distinct { get; set; }
        public decimal Sum { get; set; }
        public double SumDouble { get; set; }
        public bool Overflow { get; set; }
        public decimal? Min { get; set; }
        public decimal? Max { get; set; }
        public double? MinDouble { get; set; }
        public double? MaxDouble { get; set; }

        public string FormatSum() => Overflow ? Format(SumDouble) : Format(Sum);
        public string FormatAvg() => Numeric == 0 ? string.Empty : Overflow ? Format(SumDouble / Numeric) : Format(Math.Round(Sum / Numeric, 6));
        public string FormatMin() => Overflow ? Format(MinDouble ?? 0) : Format(Min ?? 0);
        public string FormatMax() => Overflow ? Format(MaxDouble ?? 0) : Format(Max ?? 0);

        private static string Format(decimal value) => value.ToString("#,0.######", CultureInfo.CurrentCulture);
        private static string Format(double value) => value.ToString("#,0.######", CultureInfo.CurrentCulture);
    }

    internal sealed class AggregateResult
    {
        public AggregateValues Total { get; set; }
        public List<AggregateValues> Columns { get; } = new List<AggregateValues>();
        public bool TooLarge { get; set; }
        public long SelectedCells { get; set; }

        /// <summary>One-line text in the style of Excel's status bar.</summary>
        public string ToStatusText()
        {
            if (TooLarge)
                return $"SSMS Plus: {SelectedCells:N0} cells selected (too many for aggregates)";

            var t = Total;
            var sb = new StringBuilder();
            sb.Append("Count: ").Append((t.Cells - t.Nulls).ToString("N0", CultureInfo.CurrentCulture));
            if (t.Nulls > 0)
                sb.Append("   NULL: ").Append(t.Nulls.ToString("N0", CultureInfo.CurrentCulture));
            if (t.Numeric > 0)
            {
                sb.Append("   Sum: ").Append(t.FormatSum());
                sb.Append("   Avg: ").Append(t.FormatAvg());
                sb.Append("   Min: ").Append(t.FormatMin());
                sb.Append("   Max: ").Append(t.FormatMax());
            }
            return sb.ToString();
        }

        public string ToDetailsText()
        {
            if (TooLarge)
                return ToStatusText();

            var sb = new StringBuilder();
            foreach (var c in Columns)
            {
                sb.AppendLine(c.Name);
                sb.AppendLine($"    Count: {c.Cells - c.Nulls:N0}    NULL: {c.Nulls:N0}    Distinct: {c.Distinct:N0}");
                if (c.Numeric > 0)
                    sb.AppendLine($"    Sum: {c.FormatSum()}    Avg: {c.FormatAvg()}    Min: {c.FormatMin()}    Max: {c.FormatMax()}");
                sb.AppendLine();
            }

            if (Columns.Count > 1)
                sb.AppendLine("All selected cells: " + ToStatusText());

            return sb.ToString().TrimEnd();
        }
    }

    internal static class GridAggregates
    {
        public const long MaxCells = 500000;

        /// <summary>Returns null when fewer than two cells are selected.</summary>
        public static AggregateResult Compute(GridControl grid)
        {
            var storage = grid?.GridStorage;
            if (storage == null)
                return null;

            var ranges = ResultsGridAccess.GetSelectedRanges(grid);
            long cells = ResultsGridAccess.CountCells(ranges);
            if (cells < 2)
                return null;

            var result = new AggregateResult { SelectedCells = cells };
            if (cells > MaxCells)
            {
                result.TooLarge = true;
                return result;
            }

            var meta = ResultsGridAccess.StorageMetadata.For(storage);
            var columns = new SortedDictionary<int, Accumulator>();
            var total = new Accumulator("All");

            foreach (var range in ranges)
            {
                try { storage.EnsureRowsInBuf(range.RowStart, range.RowEnd); } catch { }

                for (int col = Math.Max(1, range.ColStart); col <= range.ColEnd; col++)
                {
                    if (!columns.TryGetValue(col, out var acc))
                    {
                        acc = new Accumulator(meta.ColumnName(grid, col), meta.ColumnType(col));
                        columns.Add(col, acc);
                    }

                    for (long row = range.RowStart; row <= range.RowEnd; row++)
                    {
                        string value = meta.Cell(storage, row, col);
                        acc.Add(value);
                        total.Add(value, acc.NumericAllowed);
                    }
                }
            }

            if (columns.Count == 0)
                return null;

            result.Total = total.Build();
            foreach (var acc in columns.Values)
                result.Columns.Add(acc.Build());
            return result;
        }

        private sealed class Accumulator
        {
            private readonly AggregateValues _v;
            private readonly HashSet<string> _distinct = new HashSet<string>(StringComparer.Ordinal);

            public Accumulator(string name, string sqlType = null)
            {
                _v = new AggregateValues { Name = name };

                // With a known type only numeric columns are summed (so "2024" in a varchar column is not).
                // Without type information any value that parses as a number counts.
                NumericAllowed = sqlType == null || GridExport.IsNumericType(sqlType);
            }

            public bool NumericAllowed { get; }

            public void Add(string value, bool numericAllowed = true)
            {
                _v.Cells++;
                if (value == null)
                {
                    _v.Nulls++;
                    return;
                }

                if (_distinct.Count < 100000)
                    _distinct.Add(value);

                if (!numericAllowed || !NumericAllowed || !GridExport.IsNumericLiteral(value))
                    return;

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return;

                _v.Numeric++;
                _v.SumDouble += d;
                _v.MinDouble = _v.MinDouble.HasValue ? Math.Min(_v.MinDouble.Value, d) : d;
                _v.MaxDouble = _v.MaxDouble.HasValue ? Math.Max(_v.MaxDouble.Value, d) : d;

                if (_v.Overflow)
                    return;

                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal m))
                {
                    _v.Overflow = true;
                    return;
                }

                try
                {
                    _v.Sum += m;
                    _v.Min = _v.Min.HasValue ? Math.Min(_v.Min.Value, m) : m;
                    _v.Max = _v.Max.HasValue ? Math.Max(_v.Max.Value, m) : m;
                }
                catch (OverflowException)
                {
                    _v.Overflow = true;
                }
            }

            public AggregateValues Build()
            {
                _v.Distinct = _distinct.Count;
                return _v;
            }
        }
    }
}
