using Microsoft.VisualStudio.Shell.Interop;
using SSMSPlusCore.Integration;
using SSMSPlusCore.Integration.Connection;
using SSMSPlusDocument.UI;
using System;
using System.ComponentModel.Design;

namespace SSMSPlusDocument
{
    public class DocumentUi
    {
        public const int MenuCommandId = 1201;

        private bool isRegistred = false;
        private int id;

        private PackageProvider _packageProvider;
        DbConnectionProvider _dbConnectionProvider;

        public DocumentUi(PackageProvider packageProvider, DbConnectionProvider dbConnectionProvider)
        {
            _packageProvider = packageProvider;
            _dbConnectionProvider = dbConnectionProvider;
        }

        public void Register()
        {
            if (isRegistred)
            {
                throw new Exception("DocumentUi is already registred");
            }

            isRegistred = true;

            var menuItem = new MenuCommand(this.ExecuteFromMenu, new CommandID(MenuHelper.CommandSet, MenuCommandId));
            _packageProvider.CommandService.AddCommand(menuItem);
        }

        private void ExecuteFromMenu(object sender, EventArgs e)
        {
            var cnx = _dbConnectionProvider.GetFromSelectedDatabase();
            if (cnx == null)
            {
                cnx = _dbConnectionProvider.GetFromActiveConnection();
                if (cnx == null)
                {
                    SSMSPlusCore.Ui.Dialogs.SsmsPlusMessageBox.ShowInfo(
                        "No user database selected",
                        "Select a user database in Object Explorer, or open a query window connected to a user database, then try again.");
                    return;
                }
            }

            Launch(cnx);
        }

        private void Launch(DbConnectionString dbConnectionString)
        {
            var toolWindow = _packageProvider.AsyncPackage.FindToolWindow(typeof(ExportDocumentsWindow), id++, true) as ExportDocumentsWindow;
            toolWindow.Intialize(dbConnectionString);

            var frame = (IVsWindowFrame)toolWindow.Frame;
            frame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
        }
    }
}
