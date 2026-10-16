namespace SSMSPlusCore.Integration
{
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
            return sqlEditorService?.GetType().GetField("_currentEditor", Instance)?.GetValue(sqlEditorService);
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
            var method = editor?.GetType().GetMethod("ChangeDatabase", Instance, null, new[] { typeof(string) }, null);
            if (method == null)
                return false;
            return method.Invoke(editor, new object[] { database }) is bool ok && ok;
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
