using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Design;
using System.Windows.Input;

namespace SSMSPlusCore.Integration
{
    /// <summary>
    /// Routes the shell Edit.Cut / Copy / Paste / Select All commands (Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A)
    /// to the focused WPF element of an SSMS Plus tool window.
    /// Without it SSMS routes them to the active query document, which fails with
    /// "Unsaved documents cannot be cut or copied to the clipboard from the Miscellaneous Files project"
    /// (upstream issues akarzazi/SSMSPlus#16 and #26).
    /// </summary>
    public static class ToolWindowClipboardCommands
    {
        /// <param name="paneCommandService">The tool window pane own command service: GetService(typeof(IMenuCommandService)) called from the pane.</param>
        public static void Register(OleMenuCommandService paneCommandService)
        {
            if (paneCommandService == null)
                return;

            Add(paneCommandService, VSConstants.VSStd97CmdID.Cut, ApplicationCommands.Cut);
            Add(paneCommandService, VSConstants.VSStd97CmdID.Copy, ApplicationCommands.Copy);
            Add(paneCommandService, VSConstants.VSStd97CmdID.Paste, ApplicationCommands.Paste);
            Add(paneCommandService, VSConstants.VSStd97CmdID.SelectAll, ApplicationCommands.SelectAll);
        }

        private static void Add(OleMenuCommandService commandService, VSConstants.VSStd97CmdID shellCommand, RoutedUICommand wpfCommand)
        {
            var commandId = new CommandID(VSConstants.GUID_VSStandardCommandSet97, (int)shellCommand);
            if (commandService.FindCommand(commandId) != null)
                return;

            var command = new OleMenuCommand((sender, e) =>
            {
                var target = Keyboard.FocusedElement;
                if (target != null && wpfCommand.CanExecute(null, target))
                    wpfCommand.Execute(null, target);
            }, commandId);

            command.BeforeQueryStatus += (sender, e) =>
            {
                var target = Keyboard.FocusedElement;
                command.Visible = true;
                command.Enabled = target != null && wpfCommand.CanExecute(null, target);
            };

            commandService.AddCommand(command);
        }
    }
}
