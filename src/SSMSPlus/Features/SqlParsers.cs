namespace SSMSPlus.Features
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// T-SQL parser of the newest SQL Server version that the ScriptDom loaded by SSMS knows (TSql170Parser in
    /// SSMS 22, TSql160Parser in SSMS 20, ...), so the same code works with every SSMS version.
    /// </summary>
    internal static class SqlParsers
    {
        private static readonly Lazy<Type> ParserType = new Lazy<Type>(() =>
            typeof(TSqlParser).Assembly.GetExportedTypes()
                .Select(t => new { Type = t, Match = Regex.Match(t.Name, @"^TSql(\d+)Parser$") })
                .Where(x => x.Match.Success && typeof(TSqlParser).IsAssignableFrom(x.Type) && !x.Type.IsAbstract)
                .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
                .Select(x => x.Type)
                .FirstOrDefault() ?? typeof(TSql150Parser));

        /// <summary>A parser with QUOTED_IDENTIFIER ON.</summary>
        public static TSqlParser Create()
        {
            return (TSqlParser)Activator.CreateInstance(ParserType.Value, true);
        }
    }
}
