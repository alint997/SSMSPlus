Reference assemblies copied from SSMS 20 (32-bit, Visual Studio 2017 shell):
C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE

Used only to compile the SSMS 18 - 20 variant (build.cmd ssms20, MSBuild property SsmsVersion=20);
at runtime SSMS loads its own copies. Same layout as the SSMS 22 assemblies one folder up, except:
  envdte.dll, envdte80.dll (PublicAssemblies) instead of Microsoft.VisualStudio.Interop.dll
  Microsoft.SqlServer.TransactSql.ScriptDom.dll from ...\Common7\IDE\Extensions\Application
  Microsoft.VisualStudio.Shell.15.0 / Shell.Framework / envdte from PublicAssemblies,
  Threading / Validation from PrivateAssemblies, the others from the IDE folder.
