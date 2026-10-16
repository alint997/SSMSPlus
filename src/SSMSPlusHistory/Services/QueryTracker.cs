namespace SSMSPlusHistory.Services
{
    using EnvDTE;
    using Microsoft.Extensions.Logging;
    using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
    using Microsoft.SqlServer.Management.UI.VSIntegration;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlusCore.Integration;
    using SSMSPlusHistory.Entities;
    using SSMSPlusHistory.Repositories;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Task = System.Threading.Tasks.Task;

    public class QueryTracker
    {
        private static readonly string[] QueryExecuteCommandNames = new[]
        {
            "Query.Execute",
            "Query.ExecuteQuery",
            "Query.BatchExecute",
        };

        private static readonly HashSet<string> QueryExecuteCommandNamesSet =
            new HashSet<string>(QueryExecuteCommandNames, StringComparer.OrdinalIgnoreCase);

        private ILogger<QueryTracker> _logger;
        private QueryItemRepository _queryItemRepository;
        private PackageProvider _packageProvider;

        private bool isTracking = false;
        private ConcurrentQueue<QueryItem> itemsQueue = new ConcurrentQueue<QueryItem>();

        private List<CommandEvents> _trackedCommandEvents = new List<CommandEvents>();
        private CommandEvents _globalCommandEvents;
        private HashSet<string> _knownQueryCommandKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastTrackTimeUtc = DateTime.MinValue;
        private string _lastTrackText;
        private ISqlEditorService _editorService;

        /// <summary>Raised (on a background thread) after new history items were saved.</summary>
        public static event EventHandler ItemsSaved;

        public CommandEvents QueryExecuteEvent { get; private set; }

        public QueryTracker(PackageProvider packageProvider, ILogger<QueryTracker> logger, QueryItemRepository queryItemRepository)
        {
            _logger = logger;
            _queryItemRepository = queryItemRepository;
            _packageProvider = packageProvider;
        }

        public void StartTraking()
        {
            if (isTracking)
            {
                throw new Exception("HistoryPlugin is already registred");
            }

            isTracking = true;
            var dte2 = _packageProvider.Dte2;

            bool hooked = false;
            foreach (var cmdName in QueryExecuteCommandNames)
            {
                try
                {
                    var command = dte2.Commands.Item(cmdName);
                    var cmdEvents = dte2.Events.get_CommandEvents(command.Guid, command.ID);
                    cmdEvents.BeforeExecute += this.CommandEvents_BeforeExecute;
                    _trackedCommandEvents.Add(cmdEvents);
                    _knownQueryCommandKeys.Add(command.Guid + "|" + command.ID);

                    if (QueryExecuteEvent == null)
                        QueryExecuteEvent = cmdEvents;

                    hooked = true;
                }
                catch (ArgumentException)
                {
                    // The command does not exist in this SSMS version (SSMS 22 only has Query.Execute): expected.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not hook command {CommandName}", cmdName);
                }
            }

            // Always subscribe to global command events as fallback for SSMS 22+
            // where specific command events may not fire
            try
            {
                _globalCommandEvents = dte2.Events.get_CommandEvents("{00000000-0000-0000-0000-000000000000}", 0);
                _globalCommandEvents.BeforeExecute += GlobalCommandEvents_BeforeExecute;
            }
            catch (Exception ex)
            {
                _globalCommandEvents = null;
                _logger.LogWarning(ex, "Could not hook global command events");
            }

            if (!hooked && _globalCommandEvents == null)
                _logger.LogError("Could not hook any query execution command: the execution history will not record queries.");
        }

        private void GlobalCommandEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Fast check: if this command is already tracked by specific hooks,
                // skip it here to avoid duplicates (specific hooks will handle it in SSMS 20)
                string key = Guid + "|" + ID;
                if (_knownQueryCommandKeys.Contains(key))
                    return;

                // Check if this is a query execution command by name
                try
                {
                    var cmd = _packageProvider.Dte2.Commands.Item(Guid, ID);
                    if (cmd != null && IsQueryExecuteCommand(cmd.Name))
                    {
                        _logger.LogInformation("Global fallback caught query command: {Name} (Guid={Guid}, ID={ID})", cmd.Name, Guid, ID);
                        _knownQueryCommandKeys.Add(key);
                        TrackQueryExecution();
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in global command handler");
            }
        }

        private static bool IsQueryExecuteCommand(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                return false;
            return QueryExecuteCommandNamesSet.Contains(commandName)
                || commandName.StartsWith("Query.Execute", StringComparison.OrdinalIgnoreCase);
        }

        private void CommandEvents_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TrackQueryExecution();
        }

        private void TrackQueryExecution()
        {
            try
            {
                string queryText = GetQueryText();

                if (string.IsNullOrWhiteSpace(queryText))
                {
                    _logger.LogWarning("Query execution detected but the query text is empty, skipping history entry");
                    return;
                }

                // Deduplication: the same execution can be reported by both the specific and the global hook.
                // Only the same text within 500 ms is a duplicate; different queries are always recorded.
                var now = DateTime.UtcNow;
                if ((now - _lastTrackTimeUtc).TotalMilliseconds < 500 && string.Equals(queryText, _lastTrackText, StringComparison.Ordinal))
                    return;
                _lastTrackTimeUtc = now;
                _lastTrackText = queryText;

                // Try primary method: ServiceCache
                UIConnectionInfo connInfo = null;
                try
                {
                    var activeConnInfo = ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo;
                    connInfo = activeConnInfo?.UIConnectionInfo;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo failed, trying fallback");
                }

                // Fallback: ISqlEditorService
                if (connInfo == null)
                {
                    try
                    {
                        connInfo = GetConnectionInfoFromEditorService();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ISqlEditorService fallback also failed");
                    }
                }

                if (connInfo == null)
                {
                    _logger.LogWarning("Could not obtain connection info from any source, skipping history entry");
                    return;
                }

                // The database selected in the query window right now (changes with the database list and USE);
                // the connection options only keep the database the window was opened with.
                string database = GetCurrentDatabase();
                if (string.IsNullOrEmpty(database))
                {
                    try
                    {
                        database = connInfo.AdvancedOptions?["DATABASE"];
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read DATABASE from AdvancedOptions");
                    }
                }

                var queryItem = new QueryItem()
                {
                    Query = queryText,
                    Server = connInfo.ServerName,
                    Username = connInfo.UserName,
                    Database = database ?? string.Empty,
                    ExecutionDateUtc = DateTime.UtcNow
                };

                _logger.LogDebug("Enqueued {Query}", queryItem.Query);

                itemsQueue.Enqueue(queryItem);

                _ = SaveSoonAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error on TrackQueryExecution");
            }
        }

        private ISqlEditorService GetEditorService()
        {
            if (_editorService == null)
                _editorService = _packageProvider.AsyncPackage.GetServiceAsync(typeof(SSqlEditorService)).Result as ISqlEditorService;
            return _editorService;
        }

        private string GetCurrentDatabase()
        {
            try
            {
                // The database selected in the active editor (toolbar list / USE). GetCurrentConnection() would
                // only give the database of the connection string, i.e. the one the window was opened with.
                return SqlEditorAccess.GetCurrentDatabase(SqlEditorAccess.GetCurrentEditor(GetEditorService()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the current database of the query window");
                return null;
            }
        }

        private UIConnectionInfo GetConnectionInfoFromEditorService()
        {
            var editorService = GetEditorService();
            if (editorService == null)
                return null;

            var details = editorService.GetActiveEditorDetails();
            if (details == null || string.IsNullOrEmpty(details.Moniker))
                return null;

            return editorService.GetUIConnectionInfoForSpecificQueryEditor(details.Moniker);
        }

        private string GetQueryText()
        {
            Document document = _packageProvider.Dte2.ActiveDocument;
            if (document == null)
                return null;


            var textDocument = (TextDocument)document.Object("TextDocument");
            string queryText = textDocument.Selection.Text;

            if (string.IsNullOrEmpty(queryText))
            {
                EditPoint startPoint = textDocument.StartPoint.CreateEditPoint();
                queryText = startPoint.GetText(textDocument.EndPoint);
            }

            return queryText;
        }

        private async Task SaveSoonAsync()
        {
            try
            {
                await Task.Delay(1000).ConfigureAwait(false);
                await SavePendingItemsAsync(0).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while saving the execution history");
            }
        }

        /// <summary>
        /// Saves the queued items. A failed save (for example the history database locked by another SSMS
        /// instance) is logged and retried a few times instead of losing the items.
        /// </summary>
        private async Task SavePendingItemsAsync(int attempt)
        {
            List<QueryItem> pendingItems = new List<QueryItem>();
            lock (itemsQueue)
            {
                QueryItem queryItem;
                while (itemsQueue.TryDequeue(out queryItem))
                {
                    pendingItems.Add(queryItem);
                }
            }

            if (pendingItems.Count == 0)
                return;

            try
            {
                await _queryItemRepository.Insert(pendingItems).ConfigureAwait(false);
                ItemsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not save {Count} history item(s) (attempt {Attempt})", pendingItems.Count, attempt + 1);
                if (attempt >= 5)
                    return;

                foreach (var item in pendingItems)
                    itemsQueue.Enqueue(item);

                await Task.Delay(2000 * (attempt + 1)).ConfigureAwait(false);
                await SavePendingItemsAsync(attempt + 1).ConfigureAwait(false);
            }
        }
    }
}
