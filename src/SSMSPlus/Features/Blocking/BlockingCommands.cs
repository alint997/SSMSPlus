namespace SSMSPlus.Features.Blocking
{
    using EnvDTE;
    using EnvDTE80;
    using Microsoft.SqlServer.Management.UI.VSIntegration;
    using Microsoft.VisualStudio.Shell;
    using SSMSPlus.Features.ExecutionPlan;
    using SSMSPlusCore.Integration;
    using SSMSPlusCore.Ui.Dialogs;
    using System;
    using System.ComponentModel.Design;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using Task = System.Threading.Tasks.Task;
    using System.Windows;

    /// <summary>Tool window hosting the blocking monitor.</summary>
    [Guid("8e4c1f2a-6b3d-4c59-9e71-2f0a5d3b7c64")]
    public sealed class BlockingToolWindow : ToolWindowPane
    {
        public BlockingToolWindow()
            : base(null)
        {
            Caption = "Blocking Monitor";
            Content = new BlockingMonitorControl();
        }

        internal BlockingMonitorControl Monitor => (BlockingMonitorControl)Content;
    }

    /// <summary>"Blocking Monitor" command: opens the window on the server of the active query window.</summary>
    internal sealed class BlockingCommands
    {
        public const int BlockingMonitorCmd = 3301;

        private readonly AsyncPackage _package;
        private readonly DTE2 _dte;

        private BlockingCommands(AsyncPackage package, DTE2 dte)
        {
            _package = package;
            _dte = dte;
        }

        public static BlockingCommands Register(AsyncPackage package, DTE2 dte, OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var instance = new BlockingCommands(package, dte);
            if (commandService != null)
            {
                var command = new OleMenuCommand((s, e) => instance.Open(), new CommandID(MenuHelper.CommandSet, BlockingMonitorCmd));
                commandService.AddCommand(command);
            }
            return instance;
        }

        private void Open()
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var monitor = await GetMonitorAsync();
                    await UseActiveConnectionAsync(monitor, onlyIfNotSet: true);
                }
                catch (Exception ex)
                {
                    SsmsPlusMessageBox.ShowError("Blocking Monitor", ex.Message);
                }
            });
        }

        private async Task<BlockingMonitorControl> GetMonitorAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = await _package.ShowToolWindowAsync(typeof(BlockingToolWindow), 0, true, _package.DisposalToken) as BlockingToolWindow;
            if (window == null)
                throw new InvalidOperationException("The Blocking Monitor window could not be created.");

            var monitor = window.Monitor;
            monitor.ChangeServerRequested = () => _ = _package.JoinableTaskFactory.RunAsync(() => UseActiveConnectionAsync(monitor, onlyIfNotSet: false));
            monitor.ShowPlanRequested = ShowPlanAsync;
            monitor.ScriptRequested = OpenScript;
            return monitor;
        }

        private bool _hasServer;

        private async Task UseActiveConnectionAsync(BlockingMonitorControl monitor, bool onlyIfNotSet)
        {
            if (onlyIfNotSet && _hasServer)
                return;

            var fallback = await GetEditorConnectionInfoAsync();
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var connection = QueryWindowConnection.GetActive(fallback);
            if (connection.Error != null)
            {
                monitor.ShowError(connection.Error);
                return;
            }

            _hasServer = true;
            monitor.SetServer(connection.ConnectionString, connection.Server);
        }

        private async Task<object> GetEditorConnectionInfoAsync()
        {
            try
            {
                var editorService = await SqlEditorServiceAccess.GetAsync(t => _package.GetServiceAsync(t));
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                return SqlEditorServiceAccess.GetActiveConnectionInfo(editorService);
            }
            catch
            {
            }

            return null;
        }

        private async Task ShowPlanAsync(string xml, string source)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            await PlanCommands.ShowPlanXmlAsync(_package, xml, source);
        }

        /// <summary>Opens a new query window (SSMS "New Query") and inserts the script; falls back to the clipboard.</summary>
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
                SsmsPlusMessageBox.ShowInfo("Blocking Monitor", "Could not open a new query window. The KILL script was copied to the clipboard.");
            }
            catch (Exception ex)
            {
                SsmsPlusMessageBox.ShowError("Blocking Monitor", ex.Message);
            }
        }
    }
}
