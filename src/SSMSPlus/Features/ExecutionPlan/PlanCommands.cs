namespace SSMSPlus.Features.ExecutionPlan
{
    using EnvDTE;
    using EnvDTE80;
    using Microsoft.SqlServer.Management.UI.VSIntegration;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlusCore.Integration;
    using SSMSPlusCore.Ui.Dialogs;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Design;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;

    /// <summary>
    /// Commands of the execution plan viewer: estimated plan, actual plan and "open the viewer".
    /// When the active document is itself a plan XML (SSMS: right click a plan > Show Execution Plan XML,
    /// or an opened .sqlplan file), that plan is shown instead of asking the server.
    /// </summary>
    internal sealed class PlanCommands
    {
        public const int EstimatedPlanCmd = 3201;
        public const int ActualPlanCmd = 3202;
        public const int OpenPlanViewerCmd = 3203;

        private readonly AsyncPackage _package;
        private readonly DTE2 _dte;
        private CancellationTokenSource _running;
        private static PlanCommands _instance;

        private PlanCommands(AsyncPackage package, DTE2 dte)
        {
            _package = package;
            _dte = dte;
        }

        public static PlanCommands Register(AsyncPackage package, DTE2 dte, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = new PlanCommands(package, dte);
            _instance = instance;
            if (commandService != null)
            {
                Add(commandService, EstimatedPlanCmd, (s, e) => instance.Run(false));
                Add(commandService, ActualPlanCmd, (s, e) => instance.Run(true));
                Add(commandService, OpenPlanViewerCmd, (s, e) => instance.OpenViewer());
            }
            return instance;
        }

        private static void Add(OleMenuCommandService service, int id, EventHandler handler)
        {
            var command = new OleMenuCommand(handler, new CommandID(MenuHelper.CommandSet, id));
            command.BeforeQueryStatus += (s, e) =>
            {
                if (s is OleMenuCommand c)
                {
                    c.Supported = true;
                    c.Visible = true;
                    c.Enabled = true;
                }
            };
            service.AddCommand(command);
        }

        private void OpenViewer()
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var viewer = await GetViewerAsync();
                    var fromDocument = TryReadPlanFromActiveDocument();
                    if (fromDocument != null)
                        viewer.ShowDocument(fromDocument);
                }
                catch (Exception ex)
                {
                    SsmsPlusMessageBox.ShowError("Execution Plan Tree", ex.Message);
                }
            });
        }

        private void Run(bool actual)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                PlanViewerControl viewer = null;
                try
                {
                    // A plan XML document (SSMS "Show Execution Plan XML", .sqlplan): show it directly.
                    var fromDocument = TryReadPlanFromActiveDocument();
                    if (fromDocument != null)
                    {
                        viewer = await GetViewerAsync();
                        viewer.ShowDocument(fromDocument);
                        return;
                    }

                    string script = GetQueryText(out string documentName);
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        viewer = await GetViewerAsync();
                        viewer.ShowError("Open a query window with a query (or select part of it) and try again.");
                        return;
                    }

                    if (actual && ModifiesData(script))
                    {
                        var answer = MessageBox.Show(
                            "The actual plan is obtained by EXECUTING the query on the server.\r\n\r\n" +
                            "The script contains statements that may change data or objects. They run inside a transaction " +
                            "that SSMS Plus rolls back afterwards, but statements that commit by themselves (COMMIT, some DDL, " +
                            "BACKUP/RESTORE, statements on linked servers...) cannot be undone.\r\n\r\nContinue?",
                            "Actual execution plan",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            MessageBoxResult.No);
                        if (answer != MessageBoxResult.Yes)
                            return;
                    }

                    var connection = QueryWindowConnection.GetActive(await GetEditorConnectionInfoAsync());
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    viewer = await GetViewerAsync();
                    if (connection.Error != null)
                    {
                        viewer.ShowError(connection.Error);
                        return;
                    }

                    _running?.Cancel();
                    var cts = new CancellationTokenSource();
                    _running = cts;

                    string target = connection.Server + (string.IsNullOrEmpty(connection.Database) ? string.Empty : " / " + connection.Database);
                    viewer.SetBusy((actual ? "Executing the query for the actual plan on " : "Getting the estimated plan from ") + target + "...", true);

                    List<string> plans = await Task.Run(() => PlanRunner.GetPlansAsync(connection.ConnectionString, script, actual, cts.Token), cts.Token);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (cts.IsCancellationRequested)
                    {
                        viewer.ShowError("Cancelled.");
                        return;
                    }

                    if (plans.Count == 0)
                    {
                        viewer.ShowError("SQL Server returned no execution plan for this script.");
                        return;
                    }

                    var document = ShowPlanParser.Parse(plans, documentName + " – " + target);
                    viewer.ShowDocument(document);
                }
                catch (OperationCanceledException)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    viewer?.ShowError("Cancelled.");
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (viewer != null)
                        viewer.ShowError(ex.Message);
                    else
                        SsmsPlusMessageBox.ShowError("Execution Plan Tree", ex.Message);
                }
            });
        }

        /// <summary>Shows a plan XML in the Execution Plan Tree window (used by the Blocking Monitor).</summary>
        public static async Task ShowPlanXmlAsync(AsyncPackage package, string xml, string source)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            PlanViewerControl viewer;
            if (_instance != null)
            {
                viewer = await _instance.GetViewerAsync();
            }
            else
            {
                var window = await package.ShowToolWindowAsync(typeof(PlanToolWindow), 0, true, package.DisposalToken) as PlanToolWindow;
                if (window == null)
                    return;
                viewer = window.Viewer;
            }

            viewer.ShowDocument(ShowPlanParser.Parse(xml, source));
        }

        private async Task<PlanViewerControl> GetViewerAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(typeof(PlanToolWindow), 0, true, _package.DisposalToken) as PlanToolWindow;
            if (window == null)
                throw new InvalidOperationException("The Execution Plan Tree window could not be created.");

            var viewer = window.Viewer;
            viewer.RequestPlan = actual => Run(actual);
            viewer.CancelRequested = () => _running?.Cancel();
            return viewer;
        }

        /// <summary>UIConnectionInfo of the active query editor through ISqlEditorService (SSMS 22), or null.</summary>
        private async Task<object> GetEditorConnectionInfoAsync()
        {
            try
            {
                var editorService = await _package.GetServiceAsync(typeof(SSqlEditorService)) as ISqlEditorService;
                if (editorService == null)
                    return null;

                var details = editorService.GetActiveEditorDetails();
                if (details != null && !string.IsNullOrEmpty(details.Moniker))
                    return editorService.GetUIConnectionInfoForSpecificQueryEditor(details.Moniker);
            }
            catch
            {
            }

            return null;
        }

        private string GetQueryText(out string documentName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            documentName = null;
            var document = _dte.ActiveDocument;
            if (document == null)
                return null;

            documentName = document.Name;
            if (!(document.Object("TextDocument") is TextDocument textDocument))
                return null;

            var selection = textDocument.Selection;
            if (selection != null && !selection.IsEmpty)
                return selection.Text;

            return textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
        }

        private PlanDocument TryReadPlanFromActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var document = _dte.ActiveDocument;
                if (document == null)
                    return null;

                string text = null;
                if (document.Object("TextDocument") is TextDocument textDocument)
                    text = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
                else if (!string.IsNullOrEmpty(document.FullName) && File.Exists(document.FullName)
                    && document.FullName.EndsWith(".sqlplan", StringComparison.OrdinalIgnoreCase))
                    text = File.ReadAllText(document.FullName);

                if (!ShowPlanParser.LooksLikeShowPlan(text))
                    return null;

                return ShowPlanParser.Parse(text, document.Name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True unless every statement is a plain SELECT (no INTO) or a variable / SET statement.</summary>
        internal static bool ModifiesData(string script)
        {
            try
            {
                var parser = new TSql170Parser(true);
                IList<ParseError> errors;
                TSqlScript parsed;
                using (var reader = new StringReader(script))
                    parsed = parser.Parse(reader, out errors) as TSqlScript;

                if (parsed == null || (errors != null && errors.Count > 0))
                    return true;

                var statements = parsed.Batches.SelectMany(b => b.Statements).ToList();
                return statements.Any(s =>
                    !(s is SelectStatement select && select.Into == null)
                    && !(s is DeclareVariableStatement)
                    && !(s is SetVariableStatement)
                    && !(s is PredicateSetStatement)
                    && !(s is SetStatisticsStatement)
                    && !(s is UseStatement)
                    && !(s is PrintStatement));
            }
            catch
            {
                return true;
            }
        }
    }
}
