using Dapper;
using SSMSPlusCore.App;
using SSMSPlusCore.Dab.Dapper;
using System;
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SSMSPlusCore.Database
{
    /// <summary>
    /// SQLite connection factory.
    /// SSMS 22 (22.10+) ships its own SQLitePCLRaw 3.x and redirects every SQLitePCLRaw / batteries
    /// version to it (see SSMS.exe.config), which breaks Microsoft.Data.Sqlite inside the extension.
    /// System.Data.SQLite 1.0.119 is self-contained (managed + SQLite.Interop.dll in x64/x86) and
    /// is outside the version range SSMS redirects (0.0.0.0-1.0.109.0), so it does not collide with the host.
    /// The database file format is unchanged, so the existing SSMS-Plus.sqlite keeps working.
    /// </summary>
    public class SqliteConnectionFactory
    {
        // Same text format Microsoft.Data.Sqlite used, so existing rows compare/sort correctly.
        private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        static SqliteConnectionFactory()
        {
            PreloadNativeInterop();
            SqlMapper.AddTypeHandler(new NullableLongHandler());
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        /// <summary>
        /// Loads x64\SQLite.Interop.dll (or x86) from the extension folder before System.Data.SQLite needs it.
        /// System.Data.SQLite does this by itself too; this is only a safety net.
        /// </summary>
        private static void PreloadNativeInterop()
        {
            try
            {
                var baseDir = Path.GetDirectoryName(typeof(SQLiteConnection).Assembly.Location);
                if (string.IsNullOrEmpty(baseDir))
                    baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                var interop = Path.Combine(baseDir, Environment.Is64BitProcess ? "x64" : "x86", "SQLite.Interop.dll");
                if (File.Exists(interop))
                    LoadLibrary(interop);
            }
            catch
            {
                // System.Data.SQLite will try its own pre-load logic.
            }
        }

        private readonly IWorkingDirProvider _workingDirProvider;

        public SqliteConnectionFactory(IWorkingDirProvider workingDirProvider)
        {
            _workingDirProvider = workingDirProvider;
        }

        public DbConnection GetConnection()
        {
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = Path.Combine(_workingDirProvider.GetWorkingDir(), "SSMS-Plus.sqlite"),
                Version = 3,
                DateTimeFormat = SQLiteDateFormats.ISO8601,
                DateTimeKind = DateTimeKind.Unspecified,
                DateTimeFormatString = DateTimeFormat,
                BusyTimeout = 5000,
            };

            return new SQLiteConnection(builder.ConnectionString);
        }
    }
}
