// Statement detection is adapted from SSMS Executor (MIT License)
// Copyright (c) 2017 Stanislav Stoyanov, Copyright (c) 2025-2026 Tom Willwerth
// https://github.com/tkwj/ssms-executor
namespace SSMSPlus.Features.ExecuteStatement
{
    using EnvDTE;
    using EnvDTE80;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// Executes only the T-SQL statement under the caret: the statement is located with ScriptDom,
    /// selected, executed with the standard Query.Execute command, and the original selection is restored.
    /// When text is already selected, the selection is executed unchanged.
    /// </summary>
    internal sealed class StatementExecutor
    {
        private const string CmdQueryExecute = "Query.Execute";
        private const string CmdQueryParse = "Query.Parse";

        private readonly DTE2 _dte;

        public StatementExecutor(DTE2 dte)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        }

        public static bool IsQueryWindowActive(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = dte?.ActiveDocument;
                if (doc == null || dte.ActiveWindow == null || doc.ActiveWindow == null)
                    return false;

                return dte.ActiveWindow.HWnd == doc.ActiveWindow.HWnd && CanExecute(dte);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanExecute(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return dte.Commands.Item(CmdQueryExecute).IsAvailable;
            }
            catch
            {
                return false;
            }
        }

        /// <returns>A short status message describing what happened.</returns>
        public string ExecuteCurrentStatement()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var document = _dte.ActiveDocument;
            if (document == null || !CanExecute(_dte))
                return "SSMS Plus: no query window is active.";

            var selection = (TextSelection)document.Selection;
            if (!selection.IsEmpty)
            {
                _dte.ExecuteCommand(CmdQueryExecute);
                return null;
            }

            var textDocument = document.Object("TextDocument") as TextDocument;
            if (textDocument == null)
                return "SSMS Plus: the active document is not a text document.";

            string script = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
            var caret = new Position(selection.ActivePoint.Line, selection.ActivePoint.LineCharOffset);

            TSqlScript parsed;
            if (!TryParse(script, out parsed))
            {
                // Let SSMS show the syntax error in the Messages pane.
                _dte.ExecuteCommand(CmdQueryParse);
                return "SSMS Plus: the script has syntax errors, nothing was executed.";
            }

            Range statement = null;
            if (parsed?.Batches != null)
            {
                foreach (var batch in parsed.Batches)
                {
                    statement = FindStatement(batch.Statements, caret);
                    if (statement != null)
                        break;
                }
            }

            if (statement == null)
                return "SSMS Plus: place the cursor inside a statement to execute it.";

            var anchor = selection.AnchorPoint.CreateEditPoint();
            var active = selection.ActivePoint.CreateEditPoint();

            Select(selection, statement.Start, statement.End);
            try
            {
                _dte.ExecuteCommand(CmdQueryExecute);
            }
            finally
            {
                Select(selection, new Position(anchor.Line, anchor.LineCharOffset), new Position(active.Line, active.LineCharOffset));
            }

            return null;
        }

        private static bool TryParse(string script, out TSqlScript parsed)
        {
            var parser = SqlParsers.Create();
            IList<ParseError> errors;
            using (var reader = new StringReader(script))
            {
                parsed = parser.Parse(reader, out errors) as TSqlScript;
            }

            return errors == null || errors.Count == 0;
        }

        private static Range FindStatement(IList<TSqlStatement> statements, Position caret)
        {
            if (statements == null)
                return null;

            foreach (var statement in statements)
            {
                if (statement == null || statement.FirstTokenIndex < 0 || statement.LastTokenIndex < 0)
                    continue;

                var first = statement.ScriptTokenStream[statement.FirstTokenIndex];
                var last = statement.ScriptTokenStream[statement.LastTokenIndex];

                var start = new Position(first.Line, first.Column);
                var end = new Position(last.Line, last.Column + (last.Text?.Length ?? 0));

                if (caret.CompareTo(start) >= 0 && caret.CompareTo(end) <= 0)
                    return new Range(start, end);
            }

            return null;
        }

        private static void Select(TextSelection selection, Position start, Position end)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            selection.MoveToLineAndOffset(start.Line, start.Column);
            selection.SwapAnchor();
            selection.MoveToLineAndOffset(end.Line, end.Column, true);
        }

        private sealed class Position : IComparable<Position>
        {
            public Position(int line, int column)
            {
                Line = line;
                Column = column;
            }

            public int Line { get; }
            public int Column { get; }

            public int CompareTo(Position other)
            {
                if (Line != other.Line)
                    return Line.CompareTo(other.Line);
                return Column.CompareTo(other.Column);
            }
        }

        private sealed class Range
        {
            public Range(Position start, Position end)
            {
                Start = start;
                End = end;
            }

            public Position Start { get; }
            public Position End { get; }
        }
    }
}
