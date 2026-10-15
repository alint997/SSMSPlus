namespace SSMSPlus.Services
{
    using SSMSPlusCore.App;
    using System;

    public class VersionProvider : IVersionProvider
    {
        public int GetBuild()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            return assembly.GetName().Version.Build;
        }

        /// <summary>
        /// Version shown in the About window: major.minor of AssemblyFileVersion (e.g. 7.0).
        /// AssemblyVersion is not used here because its build number is the local database version.
        /// </summary>
        public int[] GetBuildAndRevision()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var fileVersion = (System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                assembly, typeof(System.Reflection.AssemblyFileVersionAttribute));

            Version parsed;
            if (fileVersion != null && Version.TryParse(fileVersion.Version, out parsed))
                return new int[] { parsed.Major, parsed.Minor };

            var version = assembly.GetName().Version;
            return new int[] { version.Build, version.Revision };
        }
    }
}

