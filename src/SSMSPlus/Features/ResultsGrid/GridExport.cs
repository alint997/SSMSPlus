namespace SSMSPlus.Features.ResultsGrid
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    /// <summary>Formats a grid selection as INSERT statements, CSV or JSON.</summary>
    internal static class GridExport
    {
        private const int MaxRowsPerInsert = 1000; // T-SQL table value constructor limit

        private static readonly HashSet<string> IntegerTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tinyint", "smallint", "int", "bigint"
        };

        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real"
        };

        private static readonly HashSet<string> BinaryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "binary", "varbinary", "image", "timestamp", "rowversion"
        };

        public static string BaseType(string sqlType)
        {
            if (string.IsNullOrWhiteSpace(sqlType))
                return string.Empty;
            int paren = sqlType.IndexOf('(');
            return (paren < 0 ? sqlType : sqlType.Substring(0, paren)).Trim();
        }

        public static bool IsNumericType(string sqlType) => NumericTypes.Contains(BaseType(sqlType));

        public static bool IsBitType(string sqlType) => string.Equals(BaseType(sqlType), "bit", StringComparison.OrdinalIgnoreCase);

        public static bool IsNumericLiteral(string value)
        {
            return !string.IsNullOrEmpty(value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && value.All(ch => char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+' || ch == 'e' || ch == 'E');
        }

        private static bool IsHexLiteral(string value)
        {
            return value.Length >= 2 && value[0] == '0' && (value[1] == 'x' || value[1] == 'X')
                && value.Skip(2).All(Uri.IsHexDigit);
        }

        // ---------------------------------------------------------------- INSERT

        public static string ToInsert(GridSelectionData data, string tableName = "#Results")
        {
            var sb = new StringBuilder();
            int cols = data.ColumnNames.Count;
            var names = UniqueNames(data.ColumnNames);

            sb.AppendLine($"CREATE TABLE {tableName} (");
            for (int c = 0; c < cols; c++)
            {
                sb.Append($"    {Quote(names[c])} {ColumnDefinitionType(data.SqlTypes[c])} NULL");
                sb.AppendLine(c < cols - 1 ? "," : string.Empty);
            }
            sb.AppendLine(");");

            if (data.Rows.Count == 0)
                return sb.ToString();

            string columnList = string.Join(", ", names.Select(Quote));
            for (int start = 0; start < data.Rows.Count; start += MaxRowsPerInsert)
            {
                int count = Math.Min(MaxRowsPerInsert, data.Rows.Count - start);
                sb.AppendLine();
                sb.AppendLine($"INSERT INTO {tableName} ({columnList})");
                for (int r = 0; r < count; r++)
                {
                    sb.Append(r == 0 ? "VALUES (" : "       (");
                    var row = data.Rows[start + r];
                    for (int c = 0; c < cols; c++)
                    {
                        if (c > 0)
                            sb.Append(", ");
                        sb.Append(SqlLiteral(row[c], data.SqlTypes[c]));
                    }
                    sb.AppendLine(r < count - 1 ? ")," : ");");
                }
            }

            return sb.ToString();
        }

        private static string ColumnDefinitionType(string sqlType)
        {
            string baseType = BaseType(sqlType);
            if (baseType.Length == 0)
                return "nvarchar(max)";
            if (baseType.Equals("timestamp", StringComparison.OrdinalIgnoreCase) || baseType.Equals("rowversion", StringComparison.OrdinalIgnoreCase))
                return "binary(8)";
            return sqlType.Trim();
        }

        private static string SqlLiteral(string value, string sqlType)
        {
            if (value == null)
                return "NULL";

            if ((IsNumericType(sqlType) || IsBitType(sqlType)) && IsNumericLiteral(value))
                return value;

            if (BinaryTypes.Contains(BaseType(sqlType)) && IsHexLiteral(value))
                return value;

            return "N'" + value.Replace("'", "''") + "'";
        }

        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

        private static List<string> UniqueNames(List<string> names)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrWhiteSpace(name) || name == "(No column name)")
                    name = "Column" + (i + 1);

                string unique = name;
                for (int suffix = 2; !seen.Add(unique); suffix++)
                    unique = name + "_" + suffix;
                result.Add(unique);
            }
            return result;
        }

        // ---------------------------------------------------------------- CSV

        public static string ToCsv(GridSelectionData data, string separator = ",")
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(separator, data.ColumnNames.Select(n => CsvField(n, separator))));
            foreach (var row in data.Rows)
                sb.AppendLine(string.Join(separator, row.Select(v => CsvField(v, separator))));
            return sb.ToString();
        }

        private static string CsvField(string value, string separator)
        {
            if (value == null)
                return string.Empty;

            bool needsQuotes = value.Contains(separator) || value.Contains("\"") || value.Contains("\r") || value.Contains("\n")
                || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])));
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        // ---------------------------------------------------------------- JSON

        public static string ToJson(GridSelectionData data)
        {
            var names = UniqueNames(data.ColumnNames);
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int r = 0; r < data.Rows.Count; r++)
            {
                var row = data.Rows[r];
                sb.Append("  {");
                for (int c = 0; c < names.Count; c++)
                {
                    if (c > 0)
                        sb.Append(", ");
                    sb.Append(JsonString(names[c])).Append(": ").Append(JsonValue(row[c], data.SqlTypes[c]));
                }
                sb.Append('}');
                sb.AppendLine(r < data.Rows.Count - 1 ? "," : string.Empty);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonValue(string value, string sqlType)
        {
            if (value == null)
                return "null";

            if (IsBitType(sqlType))
            {
                if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return "true";
                if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return "false";
            }

            if (IsNumericType(sqlType) && IsNumericLiteral(value))
            {
                // JSON does not allow leading zeros or a leading '+'.
                string trimmed = value.TrimStart('+');
                if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) && !IntegerTypes.Contains(BaseType(sqlType)))
                    return d.ToString(CultureInfo.InvariantCulture);
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l.ToString(CultureInfo.InvariantCulture);
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && !double.IsInfinity(f) && !double.IsNaN(f))
                    return f.ToString("R", CultureInfo.InvariantCulture);
            }

            return JsonString(value);
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
