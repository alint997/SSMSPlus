namespace SSMSPlus.Features
{
    using EnvDTE80;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlus.Features.ExecuteStatement;
    using SSMSPlus.Features.ResultsGrid;
    using SSMSPlusCore.Integration;
    using SSMSPlusCore.Ui.Dialogs;
    using System;
    using System.ComponentModel.Design;
    using System.Windows;

    /// <summary>
    /// Query editor and results grid commands: Execute Current Statement, Copy as INSERT / CSV / JSON,
    /// selection aggregates (status bar and details dialog).
    /// </summary>
    internal sealed class QueryToolsCommands
    {
        public const int ExecuteCurrentStatementCmd = 3001;
        public const int CopyAsInsertCmd = 3101;
        public const int CopyAsCsvCmd = 3102;
        public const int CopyAsJsonCmd = 3103;
        public const int SelectionAggregatesCmd = 3104;

        private readonly DTE2 _dte;
        private GridAggregatesStatusBar _statusBar;

        private QueryToolsCommands(DTE2 dte)
        {
            _dte = dte;
        }

        public static QueryToolsCommands Register(DTE2 dte, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = new QueryToolsCommands(dte);

            if (commandService != null)
            {
                instance.Add(commandService, ExecuteCurrentStatementCmd, instance.ExecuteCurrentStatement, instance.QueryStatusEditor);
                instance.Add(commandService, CopyAsInsertCmd, (s, e) => instance.CopyGrid(GridFormat.Insert), instance.QueryStatusGrid);
                instance.Add(commandService, CopyAsCsvCmd, (s, e) => instance.CopyGrid(GridFormat.Csv), instance.QueryStatusGrid);
                instance.Add(commandService, CopyAsJsonCmd, (s, e) => instance.CopyGrid(GridFormat.Json), instance.QueryStatusGrid);
                instance.Add(commandService, SelectionAggregatesCmd, instance.ShowAggregates, instance.QueryStatusGrid);
            }

            instance._statusBar = new GridAggregatesStatusBar(dte);
            instance._statusBar.Start();
            return instance;
        }

        private void Add(OleMenuCommandService service, int id, EventHandler handler, EventHandler queryStatus)
        {
            var command = new OleMenuCommand(handler, new CommandID(MenuHelper.CommandSet, id));
            command.BeforeQueryStatus += queryStatus;
            service.AddCommand(command);
        }

        private void QueryStatusEditor(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is OleMenuCommand command)
            {
                command.Supported = true;
                command.Visible = true;
                command.Enabled = StatementExecutor.IsQueryWindowActive(_dte);
            }
        }

        private void QueryStatusGrid(object sender, EventArgs e)
        {
            if (sender is OleMenuCommand command)
            {
                command.Supported = true;
                command.Visible = true;
                command.Enabled = true; // a message explains when no grid is focused
            }
        }

        private void ExecuteCurrentStatement(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string message = new StatementExecutor(_dte).ExecuteCurrentStatement();
                if (!string.IsNullOrEmpty(message))
                    SetStatus(message);
            }
            catch (Exception ex)
            {
                SsmsPlusMessageBox.ShowError("Execute Current Statement", ex.Message);
            }
        }

        private enum GridFormat
        {
            Insert,
            Csv,
            Json,
        }

        private void CopyGrid(GridFormat format)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var grid = ResultsGridAccess.GetFocusedGrid();
                if (grid == null)
                {
                    SsmsPlusMessageBox.ShowInfo("SSMS Plus", "Click a cell in a results grid first, then use the command again.");
                    return;
                }

                var data = ResultsGridAccess.ReadSelection(grid, wholeGridIfSingleCell: true);
                string text;
                switch (format)
                {
                    case GridFormat.Insert: text = GridExport.ToInsert(data); break;
                    case GridFormat.Csv: text = GridExport.ToCsv(data); break;
                    default: text = GridExport.ToJson(data); break;
                }

                Clipboard.SetDataObject(text, true);
                SetStatus($"SSMS Plus: copied {data.Rows.Count:N0} row(s), {data.ColumnNames.Count} column(s) as {format.ToString().ToUpperInvariant()}.");
            }
            catch (Exception ex)
            {
                SsmsPlusMessageBox.ShowError("Copy results", ex.Message);
            }
        }

        private void ShowAggregates(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var grid = ResultsGridAccess.GetFocusedGrid();
                var result = grid == null ? null : GridAggregates.Compute(grid);
                if (result == null)
                {
                    SsmsPlusMessageBox.ShowInfo("Selection aggregates", "Select two or more cells in a results grid first.");
                    return;
                }

                SsmsPlusMessageBox.ShowInfo("Selection aggregates", result.ToDetailsText());
            }
            catch (Exception ex)
            {
                SsmsPlusMessageBox.ShowError("Selection aggregates", ex.Message);
            }
        }

        private void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (_dte?.StatusBar != null)
                    _dte.StatusBar.Text = text;
            }
            catch
            {
                // Status bar not available.
            }
        }
    }
}
