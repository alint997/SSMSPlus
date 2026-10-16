namespace SSMSPlusCore.Integration
{
    public interface IServiceCacheIntegration
    {
        void OpenScriptInNewWindow(string script);

        /// <summary>
        /// Opens the script in a new query window. When the new window has no database selected (for example
        /// New Query on a server node) and it is connected to <paramref name="server"/>, the window is opened
        /// on <paramref name="database"/> instead.
        /// </summary>
        void OpenScriptInNewWindow(string script, string server, string database);
    }
}
