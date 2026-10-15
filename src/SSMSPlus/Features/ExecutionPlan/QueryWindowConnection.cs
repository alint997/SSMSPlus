namespace SSMSPlus.Features.ExecutionPlan
{
    using System;
    using System.Data.SqlClient;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Security;

    /// <summary>
    /// Builds a connection string for the active query window from SSMS's UIConnectionInfo
    /// (ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo), read by reflection so a change in SSMS
    /// internals degrades to a message instead of an exception.
    /// </summary>
    internal static class QueryWindowConnection
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public sealed class Result
        {
            public string ConnectionString { get; set; }
            public string Server { get; set; }
            public string Database { get; set; }
            public string Error { get; set; }
        }

        /// <param name="fallbackConnectionInfo">UIConnectionInfo from ISqlEditorService, used when ServiceCache has none.</param>
        public static Result GetActive(object fallbackConnectionInfo = null)
        {
            object uiConnectionInfo;
            try
            {
                uiConnectionInfo = GetUIConnectionInfo() ?? fallbackConnectionInfo;
            }
            catch (Exception ex)
            {
                return new Result { Error = "Could not read the connection of the query window: " + ex.Message };
            }

            if (uiConnectionInfo == null)
                return new Result { Error = "The active query window is not connected. Connect it to a server first." };

            var type = uiConnectionInfo.GetType();
            string server = GetString(type, uiConnectionInfo, "ServerName");
            if (string.IsNullOrEmpty(server))
                return new Result { Error = "The active query window is not connected to a server." };

            if (server.StartsWith("ADMIN:", StringComparison.OrdinalIgnoreCase))
                server = server.Substring("ADMIN:".Length);

            string database = GetIndexed(type, uiConnectionInfo, "AdvancedOptions", "DATABASE") ?? GetString(type, uiConnectionInfo, "DatabaseName");

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                ApplicationName = "SSMS Plus - Execution Plan",
                ConnectTimeout = 15,
                Encrypt = true,
                TrustServerCertificate = true,
                Pooling = false,
            };
            if (!string.IsNullOrEmpty(database))
                builder.InitialCatalog = database;

            if (GetValue(type, uiConnectionInfo, "RenewableToken") != null)
            {
                return new Result
                {
                    Server = server,
                    Database = database,
                    Error = "The query window uses Microsoft Entra authentication, which SSMS Plus cannot reuse. Use SSMS's own plan (Ctrl+L / Ctrl+M), then right click it > Show Execution Plan XML and use \"Paste XML\" or run the command on that XML document.",
                };
            }

            string password = GetString(type, uiConnectionInfo, "Password");
            if (string.IsNullOrEmpty(password))
                password = ReadSecure(GetValue(type, uiConnectionInfo, "InMemoryPassword") as SecureString);
            string user = GetString(type, uiConnectionInfo, "UserName");

            if (!string.IsNullOrEmpty(password))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = user ?? string.Empty;
                builder.Password = password;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            return new Result { ConnectionString = builder.ConnectionString, Server = server, Database = database };
        }

        private static object GetUIConnectionInfo()
        {
            Type serviceCache = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.Equals("SqlPackageBase", StringComparison.OrdinalIgnoreCase))
                {
                    serviceCache = assembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                    break;
                }
            }

            object scriptFactory = serviceCache?.GetProperty("ScriptFactory", Any)?.GetValue(null);
            if (scriptFactory == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name.Equals("SQLEditors", StringComparison.OrdinalIgnoreCase))
                    {
                        var type = assembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptFactory");
                        scriptFactory = type?.GetProperty("Instance", Any)?.GetValue(null);
                        break;
                    }
                }
            }

            if (scriptFactory == null)
                return null;

            object wrapper = scriptFactory.GetType().GetProperty("CurrentlyActiveWndConnectionInfo", Any)?.GetValue(scriptFactory);
            return wrapper?.GetType().GetProperty("UIConnectionInfo", Any)?.GetValue(wrapper);
        }

        private static object GetValue(Type type, object instance, string name)
        {
            try
            {
                return type.GetProperty(name, Any)?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(Type type, object instance, string name) => GetValue(type, instance, name) as string;

        private static string GetIndexed(Type type, object instance, string property, string key)
        {
            try
            {
                var collection = GetValue(type, instance, property);
                if (collection is System.Collections.Specialized.NameValueCollection nvc)
                    return string.IsNullOrEmpty(nvc[key]) ? null : nvc[key];

                var indexer = collection?.GetType().GetProperty("Item", new[] { typeof(string) });
                var value = indexer?.GetValue(collection, new object[] { key }) as string;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadSecure(SecureString secure)
        {
            if (secure == null || secure.Length == 0)
                return null;

            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.SecureStringToGlobalAllocUnicode(secure);
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.ZeroFreeGlobalAllocUnicode(buffer);
            }
        }
    }
}
