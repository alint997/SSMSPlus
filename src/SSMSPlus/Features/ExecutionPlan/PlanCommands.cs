namespace SSMSPlus.Features.ExecutionPlan
{
    using EnvDTE;
    using EnvDTE80;
    using Microsoft.SqlServer.Management.UI.VSIntegration;
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlus.Features.Advisor;
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

        // Connection that produced the last plan, reused by the advisor to read statistics and indexes.
        private PlanDocument _lastDocument;
        private QueryWindowConnection.Result _lastConnection;

        // Query window of the last plan request, for Refresh and for the Estimated / Actual buttons of the plan
        // window (when they are clicked the active document is not a query window).
        private string _lastQueryName;
        private string _lastQueryFullName;
        private string _lastMoniker;
        private bool? _lastActual;

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

        /// <summary>Gets the last plan again (same query window, same kind), with the current text or selection of that window.</summary>
        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Run(_lastActual ?? false, refresh: true);
        }

        /// <param name="refresh">Use the query window of the last request even when another query window is active.</param>
        private void Run(bool actual, bool refresh = false)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                PlanViewerControl viewer = null;
                try
                {
                    // A plan XML document (SSMS "Show Execution Plan XML", .sqlplan): show it directly.
                    var fromDocument = refresh ? null : TryReadPlanFromActiveDocument();
                    if (fromDocument != null)
                    {
                        viewer = await GetViewerAsync();
                        viewer.ShowDocument(fromDocument);
                        return;
                    }

                    // The active query window, or - for Refresh, and when the buttons of the plan window are used and
                    // the active document is not a query window - the query window of the last request.
                    string script = null;
                    string documentName = null;
                    bool fromLast = refresh;
                    var activeDocument = refresh ? null : _dte.ActiveDocument;
                    string activeFullName = SafeFullName(activeDocument);
                    if (!refresh)
                    {
                        script = GetQueryText(activeDocument, out documentName);
                        fromLast = string.IsNullOrWhiteSpace(script) && !IsQueryWindow(activeDocument);
                    }

                    if (fromLast)
                    {
                        var last = FindLastQueryDocument();
                        if (last == null)
                        {
                            viewer = await GetViewerAsync();
                            viewer.ShowError(_lastQueryName == null
                                ? "Open a query window with a query (or select part of it) and try again."
                                : "The query window " + _lastQueryName + " is no longer open. Open a query window with a query and try again.");
                            return;
                        }

                        script = GetQueryText(last, out documentName);
                    }

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

                    var connection = fromLast
                        ? await GetConnectionForMonikerAsync(_lastMoniker)
                        : await GetActiveConnectionAsync();
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
                    _lastDocument = document;
                    _lastConnection = connection;
                    _lastActual = actual;
                    if (!fromLast)
                    {
                        _lastQueryName = documentName;
                        _lastQueryFullName = activeFullName;
                        _lastMoniker = connection.Moniker;
                    }

                    viewer.ShowDocument(document, readServerInfo: true);
                    viewer.SetRefreshTarget((actual ? "actual" : "estimated") + " plan of " + _lastQueryName);
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
            viewer.RefreshRequested = Refresh;
            viewer.CancelRequested = () => _running?.Cancel();
            viewer.ReadServerInfo = ReadServerInfoAsync;
            viewer.OpenScript = OpenScript;
            return viewer;
        }

        /// <summary>
        /// Catalog information for the advisor: with the connection that produced the plan, or, for plans opened
        /// from a file / the clipboard / the Blocking Monitor, with the connection of the active query window.
        /// </summary>
        private async Task<AdvisorServerInfo> ReadServerInfoAsync(PlanDocument document, CancellationToken cancellationToken)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var connection = ReferenceEquals(document, _lastDocument) ? _lastConnection : null;
            if (connection == null)
            {
                connection = await GetActiveConnectionAsync();
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            }

            if (connection.Error != null)
            {
                var failed = new AdvisorServerInfo { Server = connection.Server };
                failed.Errors.Add(connection.Error);
                return failed;
            }

            return await Task.Run(() => AdvisorCatalogReader.ReadAsync(connection.ConnectionString, connection.Server, document, cancellationToken), cancellationToken);
        }

        /// <summary>Opens a new query window (SSMS "New Query") with the script; falls back to the clipboard.</summary>
        private void OpenScript(string script)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var before = _dte.ActiveDocument;
                _dte.ExecuteCommand("File.NewQuery");
                var document = _dte.ActiveDocument;
                if (document != null && !ReferenceEquals(document, before) && document.Object("TextDocument") is TextDocument text)
                {
                    text.Selection.Insert(script);
                    return;
                }
            }
            catch
            {
            }

            try
            {
                Clipboard.SetDataObject(script, true);
                SsmsPlusMessageBox.ShowInfo("Query Advisor", "Could not open a new query window. The script was copied to the clipboard.");
            }
            catch (Exception ex)
            {
                SsmsPlusMessageBox.ShowError("Query Advisor", ex.Message);
            }
        }

        /// <summary>
        /// Connection of the active query window, with the database currently selected in it (the database list of
        /// the toolbar), not only the one the window was opened with.
        /// </summary>
        private async Task<QueryWindowConnection.Result> GetActiveConnectionAsync()
        {
            object info = null;
            string moniker = null;
            ISqlEditorService editorService = null;
            try
            {
                editorService = await _package.GetServiceAsync(typeof(SSqlEditorService)) as ISqlEditorService;
                if (editorService != null)
                {
                    var details = editorService.GetActiveEditorDetails();
                    if (details != null && !string.IsNullOrEmpty(details.Moniker))
                    {
                        moniker = details.Moniker;
                        info = editorService.GetUIConnectionInfoForSpecificQueryEditor(moniker);
                    }
                }
            }
            catch
            {
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = QueryWindowConnection.GetActive(info);
            connection.Moniker = moniker;
            return QueryWindowConnection.WithDatabase(connection, CurrentDatabase(editorService, moniker));
        }

        /// <summary>Connection of a query window that is not necessarily the active one, by its moniker.</summary>
        private async Task<QueryWindowConnection.Result> GetConnectionForMonikerAsync(string moniker)
        {
            if (string.IsNullOrEmpty(moniker))
                return await GetActiveConnectionAsync();

            object info = null;
            ISqlEditorService editorService = null;
            try
            {
                editorService = await _package.GetServiceAsync(typeof(SSqlEditorService)) as ISqlEditorService;
                if (editorService != null)
                    info = editorService.GetUIConnectionInfoForSpecificQueryEditor(moniker);
            }
            catch
            {
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (info == null)
                return new QueryWindowConnection.Result { Error = "The query window " + _lastQueryName + " is not connected. Connect it to a server first." };

            var connection = QueryWindowConnection.FromConnectionInfo(info);
            connection.Moniker = moniker;
            return QueryWindowConnection.WithDatabase(connection, CurrentDatabase(editorService, moniker));
        }

        /// <summary>Database selected in the query window with this moniker; null when it cannot be read.</summary>
        private static string CurrentDatabase(object editorService, string moniker)
        {
            try
            {
                var editor = SqlEditorAccess.GetCurrentEditor(editorService);
                if (editor == null || string.IsNullOrEmpty(moniker)
                    || !string.Equals(SqlEditorAccess.GetMoniker(editor), moniker, StringComparison.OrdinalIgnoreCase))
                    return null;
                return SqlEditorAccess.GetCurrentDatabase(editor);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsQueryWindow(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return document?.Object("TextDocument") is TextDocument;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeFullName(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return document?.FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The query window of the last plan request, if it is still open (compared by name, not by reference).</summary>
        private Document FindLastQueryDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_lastQueryName == null)
                return null;

            try
            {
                Document byName = null;
                foreach (Document document in _dte.Documents)
                {
                    if (!string.IsNullOrEmpty(_lastQueryFullName)
                        && string.Equals(SafeFullName(document), _lastQueryFullName, StringComparison.OrdinalIgnoreCase))
                        return document;
                    if (byName == null && string.Equals(document.Name, _lastQueryName, StringComparison.OrdinalIgnoreCase))
                        byName = document;
                }

                return byName;
            }
            catch
            {
                return null;
            }
        }

        private static string GetQueryText(Document document, out string documentName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            documentName = null;
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
