namespace SSMSPlusCore.Integration
{
    using Microsoft.VisualStudio;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.VisualStudio.Shell.Interop;
    using System;
    using System.Reflection;

    /// <summary>
    /// Access to the active SQL query editor of SSMS through its SqlEditorService (SQLEditors.dll), by reflection:
    /// the database currently selected in the window (what the database list of the toolbar shows) is not exposed
    /// by ISqlEditorService. GetCurrentConnection() is no substitute: it returns a new, closed SqlConnection built
    /// from the connection string, i.e. the database the window was opened with.
    /// </summary>
    public static class SqlEditorAccess
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The active query editor control (SqlEditorService._currentEditor), or null.</summary>
        public static object GetCurrentEditor(object sqlEditorService)
        {
            // SSMS 18 - 20 have no SqlEditorService: the editor is the document view of the active window.
            if (sqlEditorService == null)
                return GetActiveEditorFromFrame();
            return sqlEditorService.GetType().GetField("_currentEditor", Instance)?.GetValue(sqlEditorService);
        }

        /// <summary>
        /// The query editor shown in the active document window (its document view, SqlScriptEditorControl), or
        /// null when the active document is not a query window. Main thread only.
        /// </summary>
        public static object GetActiveEditorFromFrame()
        {
            if (!ThreadHelper.CheckAccess())
                return null;
            try
            {
                var selection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
                if (selection == null)
                    return null;
                if (selection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object value) != VSConstants.S_OK)
                    return null;
                if (!(value is IVsWindowFrame frame))
                    return null;
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object view) != VSConstants.S_OK || view == null)
                    return null;
                return GetProperty(view.GetType(), "CurrentDB") != null ? view : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Database selected in the editor; null when the editor is not connected.</summary>
        public static string GetCurrentDatabase(object editor)
        {
            if (editor == null)
                return null;
            try
            {
                var value = GetProperty(editor.GetType(), "CurrentDB")?.GetValue(editor) as string;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

        public static string GetMoniker(object editor)
        {
            return editor == null ? null : GetProperty(editor.GetType(), "Moniker")?.GetValue(editor) as string;
        }

        /// <summary>Selects a database in the editor, like the database list of the toolbar.</summary>
        public static bool ChangeDatabase(object editor, string database)
        {
            if (editor == null)
                return false;

            // SSMS 21+: public ChangeDatabase(string).
            var method = editor.GetType().GetMethod("ChangeDatabase", Instance, null, new[] { typeof(string) }, null);
            if (method != null)
                return method.Invoke(editor, new object[] { database }) is bool ok && ok;

            // SSMS 18 - 20: the CurrentDB setter changes the database of the connection and updates the toolbar.
            var property = GetProperty(editor.GetType(), "CurrentDB");
            if (property == null || property.GetSetMethod(true) == null)
                return false;
            property.SetValue(editor, database);
            return string.Equals(GetCurrentDatabase(editor), database, StringComparison.OrdinalIgnoreCase);
        }

        private static PropertyInfo GetProperty(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                if (p != null)
                    return p;
            }
            return null;
        }
    }
}
