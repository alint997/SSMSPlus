namespace SSMSPlusCore.Integration
{
    using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
#if !SSMS20
    using Microsoft.SqlServer.Management.UI.VSIntegration;
#endif
    using Microsoft.VisualStudio.Shell;
    using System;
    using System.Threading.Tasks;
    using Task = System.Threading.Tasks.Task;

    /// <summary>
    /// SSMS 21+ ISqlEditorService (SqlWorkbench.Interfaces) behind an untyped handle, so that the code also compiles
    /// against SSMS 18 - 20 (build with SsmsVersion=20, symbol SSMS20), which do not have it. There every method
    /// returns null and the callers fall back to ServiceCache.ScriptFactory and the active window frame.
    /// </summary>
    public static class SqlEditorServiceAccess
    {
#if SSMS20
        public static Task<object> GetAsync(Func<Type, Task<object>> getServiceAsync) => Task.FromResult<object>(null);

        public static object Get(IServiceProvider provider) => null;

        public static string GetActiveMoniker(object service) => null;

        public static UIConnectionInfo GetConnectionInfo(object service, string moniker) => null;
#else
        /// <summary>The ISqlEditorService, or null.</summary>
        /// <param name="getServiceAsync">GetServiceAsync of the package, e.g. <c>t =&gt; package.GetServiceAsync(t)</c>.</param>
        public static async Task<object> GetAsync(Func<Type, Task<object>> getServiceAsync)
        {
            if (getServiceAsync == null)
                return null;
            var service = await getServiceAsync(typeof(SSqlEditorService)).ConfigureAwait(false) as ISqlEditorService;
            if (service == null)
                return null;
            return service;
        }

        public static object Get(IServiceProvider provider)
        {
            if (provider == null)
                return null;
            var service = provider.GetService(typeof(SSqlEditorService)) as ISqlEditorService;
            if (service == null)
                return null;
            return service;
        }

        /// <summary>Moniker of the active query editor, or null.</summary>
        public static string GetActiveMoniker(object service)
        {
            var details = (service as ISqlEditorService)?.GetActiveEditorDetails();
            return details == null || string.IsNullOrEmpty(details.Moniker) ? null : details.Moniker;
        }

        /// <summary>Connection of the query editor with this moniker, or null.</summary>
        public static UIConnectionInfo GetConnectionInfo(object service, string moniker)
        {
            if (!(service is ISqlEditorService editorService) || string.IsNullOrEmpty(moniker))
                return null;
            return editorService.GetUIConnectionInfoForSpecificQueryEditor(moniker);
        }
#endif

        /// <summary>Connection of the active query editor, or null.</summary>
        public static UIConnectionInfo GetActiveConnectionInfo(object service) => GetConnectionInfo(service, GetActiveMoniker(service));
    }
}
