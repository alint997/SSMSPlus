namespace SSMSPlusCore.Integration
{
    using Microsoft.Extensions.Logging;
    using Microsoft.SqlServer.Management.UI.VSIntegration;
    using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
    using Microsoft.VisualStudio.Shell;
    using System;

    public class ServiceCacheIntegration : IServiceCacheIntegration
    {
        private readonly ILogger<ServiceCacheIntegration> _logger;

        public ServiceCacheIntegration(ILogger<ServiceCacheIntegration> logger)
        {
            _logger = logger;
        }

        public void OpenScriptInNewWindow(string script)
        {
            OpenScriptInNewWindow(script, null, null);
        }

        public void OpenScriptInNewWindow(string script, string server, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var editorService = string.IsNullOrEmpty(database) ? null : ServiceProvider.GlobalProvider.GetService(typeof(SSqlEditorService));
            var previousEditor = SqlEditorAccess.GetCurrentEditor(editorService);

            ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql);

            var document = ServiceCache.ExtensibilityModel.Application.ActiveDocument;
            EnvDTE.TextDocument doc = (EnvDTE.TextDocument)document.Object(null);
            doc.EndPoint.CreateEditPoint().Insert(script);

            if (!string.IsNullOrEmpty(database))
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => SelectDatabaseAsync(editorService, previousEditor, document, server, database));
        }

        /// <summary>
        /// When the new query window is on master (no user database selected, for example New Query on a server node
        /// in Object Explorer) and connected to the server the query ran on, selects the query's database like the
        /// database list of the toolbar. The window connects asynchronously: waits for it (up to ~10 s) and gives up
        /// if another document becomes active.
        /// </summary>
        private async System.Threading.Tasks.Task SelectDatabaseAsync(object editorService, object previousEditor, EnvDTE.Document document, string server, string database)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (editorService == null)
                    editorService = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SSqlEditorService));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (editorService == null)
                {
                    _logger?.LogWarning("Open from history: SqlEditorService not available, database not selected");
                    return;
                }

                if (editorService.GetType().GetField("_currentEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) == null)
                {
                    _logger?.LogWarning("Open from history: {Type} has no _currentEditor field, database not selected", editorService.GetType().FullName);
                    return;
                }

                string documentName = document?.Name;
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    if (attempt > 0)
                    {
                        await System.Threading.Tasks.Task.Delay(200);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    }

                    var active = ServiceCache.ExtensibilityModel.Application.ActiveDocument;
                    var editor = SqlEditorAccess.GetCurrentEditor(editorService);
                    string current = SqlEditorAccess.GetCurrentDatabase(editor);

                    // Compare by name: the DTE may hand out a different wrapper object for the same document.
                    if (active != null && documentName != null && !string.Equals(active.Name, documentName, StringComparison.OrdinalIgnoreCase))
                        return; // the user switched to another document

                    if (editor == null || ReferenceEquals(editor, previousEditor))
                        continue; // the editor service has not switched to the new window yet

                    if (current == null)
                        continue; // not connected yet

                    string windowServer = ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo?.UIConnectionInfo?.ServerName;
                    if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(windowServer)
                        && !string.Equals(windowServer, server, StringComparison.OrdinalIgnoreCase))
                        return; // another server: the database may not exist there

                    if (!string.Equals(current, "master", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(current, database, StringComparison.OrdinalIgnoreCase))
                        return; // a database is already selected

                    if (!SqlEditorAccess.ChangeDatabase(editor, database))
                        _logger?.LogWarning("Open from history: could not select database {Database}", database);
                    return;
                }

                _logger?.LogWarning("Open from history: the query window did not connect in time, database not selected");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Open from history: could not select database {Database}", database);
            }
        }
    }
}
