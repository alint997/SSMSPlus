using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SSMSPlusDocument.UI;
using SSMSPlusHistory.UI;
using SSMSPlusSearch.UI;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace SSMSPlus
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = false, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 7)]
    [Guid(Command1Package.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(HistoryToolWindow), Style = VsDockStyle.Tabbed, Window = "00000000-0000-0000-0000-000000000001")]
    [ProvideToolWindow(typeof(SearchToolWindow), Window = "00000000-0000-0000-0000-000000000002", MultiInstances = true, Transient = true)]
    [ProvideToolWindow(typeof(SSMSPlus.Features.Blocking.BlockingToolWindow), Style = VsDockStyle.MDI, Transient = true)]
    [ProvideToolWindow(typeof(SSMSPlus.Features.ExecutionPlan.PlanToolWindow), Style = VsDockStyle.MDI, Transient = true)]
    [ProvideToolWindow(typeof(ExportDocumentsWindow), Window = "00000000-0000-0000-0000-000000000003", MultiInstances = true, Transient = true)]
    public sealed class Command1Package : AsyncPackage
    {
        /// <summary>
        /// Command1Package GUID string.
        /// </summary>
        public const string PackageGuidString = "d09fc137-a22d-446b-89ce-1d2667f49364";

        /// <summary>
        /// Initializes a new instance of the <see cref="Command1Package"/> class.
        /// </summary>
        public Command1Package()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static readonly string[] SsmsProvidedAssemblies = new[]
        {
            "SqlWorkbench.Interfaces",
            "SqlPackageBase",
            "Microsoft.SqlServer.ConnectionInfo",
            "Microsoft.SqlServer.RegSvrEnum",
            "Microsoft.SqlServer.GridControl",
            "Microsoft.SqlServer.TransactSql.ScriptDom",
        };

        // Folders (relative to the SSMS IDE folder) where SSMS keeps the assemblies above.
        private static readonly string[] SsmsAssemblyFolders = new[]
        {
            "",
            @"Extensions\Application",
            "PrivateAssemblies",
            "PublicAssemblies",
        };

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);

            // For SSMS-provided assemblies, always load from SSMS IDE folder
            foreach (var ssmsAsm in SsmsProvidedAssemblies)
            {
                if (assemblyName.Name.Equals(ssmsAsm, StringComparison.OrdinalIgnoreCase))
                {
                    // Reuse the copy SSMS already loaded, if any.
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                            return loaded;
                    }

                    var ideDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    foreach (var folder in SsmsAssemblyFolders)
                    {
                        var idePath = Path.Combine(ideDir, folder, assemblyName.Name + ".dll");
                        if (File.Exists(idePath))
                            return Assembly.LoadFrom(idePath);
                    }
                    return null;
                }
            }

            // For extension assemblies, load from extension folder
            var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var candidatePath = Path.Combine(extensionDir, assemblyName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                return Assembly.LoadFrom(candidatePath);
            }
            return null;
        }

        #region Package Members

        private DTE2 _dte;

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await base.InitializeAsync(cancellationToken, progress);
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = (DTE2)await GetServiceAsync(typeof(EnvDTE.DTE));

            await Command1.InitializeAsync(this, _dte);
        }
    }

    #endregion
}
