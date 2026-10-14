using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using SSMSPlusCore.Integration.ObjectExplorer;
using SSMSPlusCore.SqlServer;
using System.Data.SqlClient;

namespace SSMSPlusCore.Integration.Connection
{
    public class DbConnectionProvider
    {
        PackageProvider _packageProvider;

        public DbConnectionProvider(PackageProvider packageProvider)
        {
            _packageProvider = packageProvider;
        }

        public DbConnectionString GetFromActiveConnection()
        {
            UIConnectionInfo connInfo = GetActiveUIConnectionInfo();
            if (connInfo == null)
                return null;

            string database = null;
            try { database = connInfo.AdvancedOptions?["DATABASE"]; } catch { }

            if (string.IsNullOrEmpty(database) || DbHelper.IsSystemDb(database))
            {
                return null;
            }

            return new DbConnectionString(GetConnectionString(connInfo), database);
        }

        private UIConnectionInfo GetActiveUIConnectionInfo()
        {
            // Primary: ServiceCache (works in SSMS 18/20)
            try
            {
                var connInfo = ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo?.UIConnectionInfo;
                if (connInfo != null)
                    return connInfo;
            }
            catch { }

            // Fallback: ISqlEditorService (more reliable in SSMS 22)
            try
            {
                var editorService = _packageProvider.AsyncPackage
                    .GetServiceAsync(typeof(SSqlEditorService)).Result as ISqlEditorService;
                if (editorService != null)
                {
                    var details = editorService.GetActiveEditorDetails();
                    if (details != null && !string.IsNullOrEmpty(details.Moniker))
                    {
                        return editorService.GetUIConnectionInfoForSpecificQueryEditor(details.Moniker);
                    }
                }
            }
            catch { }

            return null;
        }

        public DbConnectionString GetFromSelectedDatabase()
        {
            var objectExplorerService = _packageProvider.AsyncPackage.GetServiceAsync(typeof(IObjectExplorerService)).Result as IObjectExplorerService;

            var dbNode = ObjectExplorerHelper.FindSelectedDatabaseNode(objectExplorerService);
            if (dbNode == null)
            {
                return null;
            }
            return new DbConnectionString(dbNode.Connection.ConnectionString, dbNode.InvariantName);
        }

        internal static string GetConnectionString(UIConnectionInfo connection)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();

            builder.DataSource = connection.ServerName;
            builder.IntegratedSecurity = string.IsNullOrEmpty(connection.Password);
            builder.Password = connection.Password;
            builder.UserID = connection.UserName;
            builder.InitialCatalog = connection.AdvancedOptions["DATABASE"];
            builder.ApplicationName = "SSMS Plus";

            return builder.ToString();
        }
    }
}
