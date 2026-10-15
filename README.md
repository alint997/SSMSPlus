# SSMSPlus
SSMS Plus is a productivity extension for SQL Server Management Studio.

**Version 7.4 works with SSMS 21 and SSMS 22 (64-bit)**, tested on SSMS 22.10.1. Older SSMS versions (18 / 19 / 20) are supported by the previous releases, see [Download](#download).

It extends SSMS with a handful set of features:

    Query Execution History
    Schema Object Search
    Document Export for binary columns
    Execute Current Statement
    Results grid: Copy as INSERT / CSV / JSON, selection aggregates
    Execution Plan Tree (DB2 Visual Explain style)
    Blocking Monitor


### Table Of Contents

- [Getting started](#getting-started)
  * [Prerequisites](#prerequisites)
  * [Download](#download)
  * [Build](#build)
  * [Install](#install)
    + [SSMS 22](#ssms-22)
    + [SSMS 21](#ssms-21)
  * [Update](#update)
  * [Launch](#launch)
- [Features](#features)
  * [Query History](#query-history)
  * [Schema Object Search](#schema-object-search)
  * [Documents Export](#documents-export)
  * [Execute Current Statement](#execute-current-statement)
  * [Results Grid Tools](#results-grid-tools)
  * [Execution Plan Tree](#execution-plan-tree)
  * [Blocking Monitor](#blocking-monitor)
- [What's new in 7.4](#whats-new-in-74)
- [What's new in 7.3](#whats-new-in-73)
- [What's new in 7.2](#whats-new-in-72)
- [What's new in 7.1](#whats-new-in-71)
- [What's new in 7.0 (SSMS 22 support)](#whats-new-in-70-ssms-22-support)
- [Contributing](#contributing)
  * [How can I contribute ?](#how-can-i-contribute--)
  * [Project Goals](#project-goals)
    + [Goals](#goals)
    + [Non Goals](#non-goals)
  * [Debugging](#debugging)
    + [Debug the SSMS instance](#debug-the-ssms-instance)
    + [The Demo project](#the-demo-project)
    + [Troubleshooting](#troubleshooting)


# Getting started
## Prerequisites
* SQL Server Management Studio 21 / 22 (64-bit) for version 7.x
* To build from source: Visual Studio 2022 or newer with the **Visual Studio extension development** workload and .NET Framework 4.8

## Download
### For SSMS 21 / 22

Get the latest build from the [Releases](https://github.com/alint997/SSMSPlus/releases) page of this fork:

1. Download `SSMSPlus-ssms22.zip` and extract it.
2. Close all SSMS windows.
3. Right click `install-ssms22.cmd` > **Run as administrator**.
4. Start SSMS: the menu is under **Tools > SSMS Plus**.

You can also build it yourself, see [Build](#build) and [Install](#install).

### For SSMS 20 / 19 / 18

These older versions are only available on the original repository ([akarzazi/SSMSPlus](https://github.com/akarzazi/SSMSPlus)):

#### SSMS 20

Get this release:
https://github.com/akarzazi/SSMSPlus/releases/tag/6.0

#### SSMS 19

Get this release:
https://github.com/akarzazi/SSMSPlus/releases/tag/5.0

#### SSMS 18

Get this release:
https://github.com/akarzazi/SSMSPlus/releases/tag/4.0

## Build

### Requirements
* Windows with **Visual Studio 2022 or newer** and the **Visual Studio extension development** workload (it includes MSBuild, the VSSDK and .NET Framework 4.8 targeting)
* Internet access for the NuGet restore (packages go to `src/packages`)

### Option 1: build script (recommended)

From the repository root run:

```
build.cmd            (Release)
build.cmd Debug      (Debug)
```

The script finds MSBuild with `vswhere`, restores the NuGet packages, rebuilds `src/SSMSPlus.sln` and creates the `dist` folder:

| Output | Content |
| --- | --- |
| `dist\SSMSPlus.vsix` | VSIX package |
| `dist\SSMSPlus\` | extension files, ready to be copied into SSMS (used by `install-ssms22.cmd`) |
| `dist\SSMSPlus-ssms22.zip` | release package: `SSMSPlus\` folder + `install-ssms22.cmd` |

The script builds with `CopyVsixExtensionFiles=false`, so it does not need administrator rights.

The same script runs on GitHub Actions (`.github/workflows/build-release.yml`): every push to `master` produces a build artifact and every `v*` tag publishes a release, see [PublishRelease.md](PublishRelease.md).

### Option 2: Visual Studio

1. Open `src/SSMSPlus.sln`.
2. Right click on the solution > **Restore NuGet Packages**.
3. Select the `Release` configuration and run **Build > Rebuild Solution**.
4. The output is in `src/SSMSPlus/bin/Release` (`SSMSPlus.vsix` + the extension files).

Notes:
* The `Debug` configuration also copies the files into `...\SSMS 22\Release\Common7\IDE\Extensions\SSMSPlus` (Visual Studio must run as administrator for that).
* If the project files were changed outside Visual Studio, close and reopen the solution, then run **Clean** and **Rebuild**, otherwise Visual Studio may keep the old references in memory.

## Install

### SSMS 22

SSMS 22 is installed by default in:

`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`

#### Option 1: install script (recommended)

1. Download and extract `SSMSPlus-ssms22.zip` from [Releases](https://github.com/alint997/SSMSPlus/releases), or build the extension (see [Build](#build)).
2. **Close all SSMS windows.**
3. Right click on `install-ssms22.cmd` > **Run as administrator** (or run it from an administrator command prompt).

The script:
* checks that SSMS 22 exists and is not running;
* deletes the previous `Extensions\SSMSPlus` folder (old versions contain SQLitePCLRaw / Microsoft.Data.Sqlite dlls that must not be loaded anymore);
* copies the `SSMSPlus` folder next to it (release zip), or `dist\SSMSPlus`, or `src\SSMSPlus\bin\Release` to `Extensions\SSMSPlus`;
* runs `SSMS.exe /updateconfiguration` so SSMS reloads the extension and its menu.

If SSMS is installed in another folder, pass its `Common7\IDE` path:

```
install-ssms22.cmd "D:\Tools\SSMS 22\Release\Common7\IDE"
```

#### Option 2: manual install

1. Close SSMS.
2. Delete `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSPlus` if it exists.
3. Copy the content of `dist\SSMSPlus` (or `src\SSMSPlus\bin\Release`) to that folder (administrator rights are required).
4. From a command prompt **run as administrator**:

   ```
   cd /d "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE"
   SSMS.exe /updateconfiguration
   ```

#### Option 3: VSIX installer

From a command prompt (do not double click the `.vsix`, Windows associates it with Visual Studio, not SSMS):

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" dist\SSMSPlus.vsix
```

The VSIX installer puts the extension in `%LocalAppData%\Microsoft\SSMS\22.0_<id>\Extensions`. Do not combine it with options 1 / 2 (the package would be registered twice); to remove it:

```
"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" /uninstall:SSMSPlus.4018b534-92e2-4cc5-abf6-f526817da0d9
```

### SSMS 21

Same steps, using the SSMS 21 folder, e.g. `C:\Program Files\Microsoft SQL Server Management Studio 21\Release\Common7\IDE` (pass it as parameter to `install-ssms22.cmd`). SSMS 21 has not been tested with version 7.x.

### Check the installation

1. Start SSMS.
2. Open **Tools > SSMS Plus**.
3. Run a query, then open **Execution History**: the query must be listed.

If something goes wrong, see [Troubleshooting](#troubleshooting).

## Update

1. Build the new version (`build.cmd`).
2. Close SSMS.
3. Run `install-ssms22.cmd` as administrator (it removes the previous version first).

Your query history and search indexes are kept (they are stored in `%LocalAppData%\SSMS Plus\SSMS-Plus.sqlite`, not in the extension folder).

## Launch
Since version 7.0 the menu is no longer a top-level menu: it is available under **Tools > SSMS Plus** (between *Code Snippets Manager...* and *External Tools...*):

* Execution History (`Ctrl+Alt+H`)
* Schema Search (`Ctrl+Alt+,`)
* Document Export (`Ctrl+Alt+E`)
* Execute Current Statement (`Ctrl+Shift+E` in a query window)
* Execution Plan Tree, Execution Plan Tree (Estimated) / (Actual)
* Blocking Monitor
* About SSMSPlus (version, working directory with the local database and logs, link to report an issue)

![SSMS Plus Menu](docs/illustrations/menu-screen.png?raw=true "SSMS Plus Menu")

If the menu does not show up after an install or update, close SSMS and run `SSMS.exe /updateconfiguration` as administrator (see [Install](#install)).

# Features

## Query History

Every time you execute a query through the editor, SSMS Plus will save that query.

Open it from **Tools > SSMS Plus > Execution History** (`Ctrl+Alt+H`). Double click a row (or right click > *Open script*) to open the query in a new editor.

![SSMS Plus Query History](docs/illustrations/history-screen.png?raw=true "Query History")

## Schema Object Search

Use this tool to find quickly any user object in the database.

Select a user database in Object Explorer (or open a query window connected to it), then use **Tools > SSMS Plus > Schema Search** (`Ctrl+Alt+,`).

![SSMS Plus Schema Object Search](docs/illustrations/schema-search-screen.png?raw=true "Schema Object Search")

## Documents Export

This tool is handy when you want to preview or export files from the database.

Select a user database in Object Explorer (or open a query window connected to it), then use **Tools > SSMS Plus > Document Export** (`Ctrl+Alt+E`).

The query is expecting two columns corresponding respectively to file name and file content (`varbinary`; text columns are exported as UTF-8).

![SSMS Plus Documents Export](docs/illustrations/document-export-screen.png?raw=true "Documents Export")

## Execute Current Statement
Executes only the T-SQL statement under the cursor, without selecting it first: press `Ctrl+Shift+E` in a query window, or right click in the editor > **Execute Current Statement** (also under **Tools > SSMS Plus**).

* The statement is found with the T-SQL parser (ScriptDom) shipped with SSMS, so multi-line statements, `GO` batches, `IF` / `BEGIN ... END` blocks and CTEs are handled; the whole block containing the cursor is executed.
* If text is selected, the selection is executed as usual.
* If the script has syntax errors, nothing is executed and SSMS shows the error (Parse).
* The cursor and selection are restored after the execution starts.

If `Ctrl+Shift+E` is already used by another extension, change it in **Tools > Options > Environment > Keyboard** (command `SSMSPlus.ExecuteCurrentStatement`).

## Results Grid Tools
Right click in a results grid > **SSMS Plus**:

* **Copy as INSERT** - `CREATE TABLE #Results` with the column types of the result set, followed by `INSERT ... VALUES` statements (1000 rows per statement). NULLs, numbers, binary values and quotes are handled.
* **Copy as CSV** - comma separated, with a header row; values are quoted when needed.
* **Copy as JSON** - an array of objects; numbers, `bit` (true / false) and NULL keep their JSON types.
* **Selection Aggregates...** - Count, NULL count, Distinct, Sum, Avg, Min and Max per selected column.

The copy commands use the selected cells (several blocks are combined into their rows and columns, in the order the columns are displayed). When a single cell is selected, the whole grid is copied. Values are the ones shown in the grid, so very long text is limited by the SSMS option *Maximum Characters Retrieved*.

**Status bar aggregates**: when two or more cells are selected in a results grid, the SSMS status bar shows `Count`, `Sum`, `Avg`, `Min` and `Max` (like Excel). Only numeric columns are summed; selections over 500,000 cells are not computed.

## Execution Plan Tree
A different view of the execution plan, inspired by DB2 Visual Explain: the plan is drawn as a tree from top to bottom (the statement at the top, tables and indexes as leaves at the bottom) and every node shows its numbers without hovering.

How to open it:
* Right click in a query window > **Execution Plan Tree (Estimated)**: the plan of the selected text (or the whole script) is requested with `SET SHOWPLAN_XML ON`; nothing is executed.
* **Execution Plan Tree (Actual)**: the query is executed with `SET STATISTICS XML ON` inside a transaction that is always rolled back. When the script is not only `SELECT`s, a confirmation is shown first (statements that commit by themselves cannot be rolled back).
* **Tools > SSMS Plus > Execution Plan Tree** opens the window; if the active document is a plan XML (SSMS: right click a graphical plan > *Show Execution Plan XML...*) or a `.sqlplan` file, that plan is shown.
* In the window: *Open .sqlplan...* and *Paste XML* (a plan XML copied to the clipboard).

The plan is requested on a separate connection to the server and database of the query window (Windows or SQL Server authentication). For Microsoft Entra connections use SSMS's own plan (`Ctrl+L` / `Ctrl+M`), then *Show Execution Plan XML* and open it in the tree.

What the tree shows:
* Operators are octagons colored by family (scan, seek, lookup, join, sort, aggregate, spool / temp, parallelism, other), the statement is an ellipse, indexes are hexagons and tables are rectangles.
* Each operator shows its node id, the cumulative (subtree) cost with the operator's own share in %, and the estimated → actual rows.
* Red border: the operator costs 25% or more of the statement, orange: 10% or more. ⚠ marks warnings: spills to tempdb, row estimates off by 10x or more, implicit conversions, missing statistics, no join predicate, memory grant warnings, missing indexes.
* Edges get thicker with the number of rows; an overview map, zoom (`Ctrl` + mouse wheel, Fit, 100%) and a color legend help with large plans.
* Clicking a node shows all its properties on the right (costs, I/O, CPU, rows, executions, logical reads, seek predicates, predicates, output columns, warnings).
* The *Operators* tab lists the operators sorted by cost (double click to find a node in the tree); the *XML* tab shows the plan XML.
* When the optimizer suggests a missing index, *Copy missing index* copies the `CREATE INDEX` statement.

## Blocking Monitor
Shows who blocks whom right now: **Tools > SSMS Plus > Blocking Monitor** opens it on the server of the active query window (*Use active query window* switches to another server).

* The sessions are shown as a tree: head blockers at the top (⛔), the sessions waiting on them below (⏳), at any depth. Head blockers are highlighted, long waits (30 s or more) are marked.
* **Idle sessions with an open transaction** (💤) are shown even before they block anyone (option): the most common cause of blocking is a `BEGIN TRAN` without `COMMIT` / `ROLLBACK` left in a query window or an application. For an idle session the last batch it ran is shown.
* For each session: wait type, wait time and resource, open transactions and how long they have been open, status, login, host, program, database and the SQL (the running statement, or the last batch for idle sessions). Your own session is marked.
* The SQL is the text the client sent (`sys.dm_exec_input_buffer`, SQL Server 2016+), so auto-parameterized statements show their literal values. For parameterized text (`sp_executesql`, `(@1 int)SELECT ...`) *Copy SQL* produces a runnable script with `DECLARE`s: runtime values from the live plan (`sys.dm_exec_query_statistics_xml`), else the values the cached plan was compiled with (marked as such), else `NULL`.
* Details panel: *Copy SQL*, *Show plan* (the cached plan of the running statement, in Execution Plan Tree) and *Script KILL* (opens a new query window with `KILL <session>`; nothing is executed).
* Auto refresh (off, 2, 5, 10, 30 s; only while the window is visible) and F5.
* Read-only: it only queries the DMVs (`sys.dm_exec_sessions`, `sys.dm_exec_requests`, `sys.dm_exec_connections`, `sys.dm_tran_session_transactions`, `sys.dm_tran_active_transactions`, `sys.dm_exec_sql_text`); nothing is installed on the server.
* Permissions: without `VIEW SERVER STATE` (or `VIEW SERVER PERFORMANCE STATE` on SQL Server 2022+) SQL Server returns only your own session, so blocking by other users is not visible; the window shows a warning in that case. `sysadmin` logins (for example `sa`) see everything. On Azure SQL Database only the sessions of the current database are visible.

# What's new in 7.4

* **Blocking Monitor** (Tools > SSMS Plus): who blocks whom right now, as a tree with the head blockers first, including idle sessions that keep a transaction open (a forgotten `BEGIN TRAN`); details, cached plan in Execution Plan Tree, *Script KILL*, auto refresh and a warning when the login cannot see other sessions. See [Blocking Monitor](#blocking-monitor).

# What's new in 7.3

* **Execute Current Statement** (`Ctrl+Shift+E`): runs the statement under the cursor, see [Execute Current Statement](#execute-current-statement). Based on the idea and statement detection of [SSMS Executor](https://github.com/tkwj/ssms-executor) (MIT).
* **Execution Plan Tree**: a DB2 Visual Explain style view of estimated and actual plans, see [Execution Plan Tree](#execution-plan-tree).
* **Results grid tools**: Copy as INSERT / CSV / JSON and Selection Aggregates in the results grid context menu, plus Excel-like aggregates in the status bar, see [Results Grid Tools](#results-grid-tools). Grid selection handling adapted from [SQLExtended](https://github.com/jamtheradar/SQLExtended) (MIT).
* New compile-time references from SSMS 22 in `src/SSMSPlus/assemblies`: `Microsoft.SqlServer.GridControl` and `Microsoft.SqlServer.TransactSql.ScriptDom` (not shipped in the VSIX; loaded from the SSMS folders at runtime).
* Menus: the new commands are in **Tools > SSMS Plus**, in the query editor right-click menu (SQLEditors' `IDM_SQLWB_SQLSCRIPT_CONTEXT`) and in the results grid right-click menu (**SSMS Plus** submenu); they use theme-aware icons from the Visual Studio image catalog (KnownMonikers). The menu resource version was bumped so SSMS loads them; after an update run `install-ssms22.cmd` as administrator (it runs `SSMS.exe /updateconfiguration`).
* Removed the obsolete `InstalledProductRegistration` attribute and the old binding redirects to 15.0 from `app.config`; fixed the VS threading analyzer warnings (VSTHRD010 / VSTHRD001 / VSTHRD103, VSSDK006).
* Build: the Visual Studio SDK assemblies (`Shell.15.0`, `Shell.Framework`, `Imaging`, `Threading`, `Utilities`, `Validation`) are now referenced from SSMS 22 (`src/SSMSPlus/assemblies`) instead of the old 15.0 NuGet packages, which removes the MSB3277 version conflict warnings; they are not shipped in the VSIX (SSMS provides them).

# What's new in 7.2

* **SSMS theme support**: the SSMS Plus windows (History, Schema Search, Document Export, About, messages) use the SSMS theme colors (Light, Dark and the color themes of SSMS 22) and follow theme changes; themed buttons, check boxes, text boxes, grids, headers, scroll bars and context menus.
* The T-SQL preview (Execution History) switches to dark editor colors on dark themes; the search matches use a darker highlight there.
* Document Export console: normal messages use the theme text color.

# What's new in 7.1

**Fixes for issues reported on the original repository ([akarzazi/SSMSPlus](https://github.com/akarzazi/SSMSPlus/issues))**
* #16 / #26 - Ctrl+C (and Ctrl+X / Ctrl+V / Ctrl+A) inside the SSMS Plus tool windows no longer fails with *"Unsaved documents cannot be cut or copied to the clipboard from the Miscellaneous Files project"*: the tool windows now handle the shell clipboard commands themselves.
* #31 - Document Export: text columns are exported as UTF-8 files instead of failing with *"Unable to cast object of type 'System.String' to type 'System.Byte[]'"*; NULL contents are skipped with a warning.
* #15 - Schema Search: objects, columns or indexes whose parent is not indexed are ignored instead of throwing `KeyNotFoundException`.
* #4 - Query History: the query preview uses AvalonEdit with **T-SQL syntax highlighting** and line numbers; search matches are still highlighted in gold.
* About window: shows the extension version (7.1) and links to this fork for issues; messages use a dialog that follows the SSMS theme instead of the plain Windows message box.

**Other changes**
* The menu location (**Tools > SSMS Plus**) and the shortcuts are documented in [Launch](#launch); new menu screenshot.
* The VSIX publisher and the *More info* link point to this fork (`alint997/SSMSPlus`).
* New NuGet package: `AvalonEdit` 6.3.1.120 (query preview editor).
* Removed the unused `e_sqlite3.dll` files (left over from Microsoft.Data.Sqlite).
* GitHub Actions updated to Node.js 24 based actions (`checkout@v5`, `upload-artifact@v6`, `action-gh-release@v3`).

# What's new in 7.0 (SSMS 22 support)

**SSMS 21 / 22 compatibility**
* The extension targets `Microsoft.VisualStudio.Ssms` `[21.0,)` on **amd64** (SSMS 21+ is 64-bit and based on the Visual Studio 18 shell).
* Compile-time references now come from SSMS 22 (`src/SSMSPlus/assemblies`): `SqlWorkbench.Interfaces`, `SqlPackageBase`, `Microsoft.SqlServer.ConnectionInfo`, `Microsoft.SqlServer.RegSvrEnum` and `Microsoft.VisualStudio.Interop` (which replaces the old `EnvDTE*` interop assemblies, fixing *"Method not found: EnvDTE._dispCommandEvents_BeforeExecuteEventHandler..ctor"*, issue #33).
* The package resolves SSMS assemblies from the SSMS IDE folder and its own dependencies from the extension folder (`AssemblyResolve` in `Command1Package`).
* Query History hooks `Query.Execute` plus a global command-events fallback, and reads the connection info through `ISqlEditorService` when `ServiceCache.ScriptFactory` is not available.
* The menu was moved to **Tools > SSMS Plus** (VSCT parent group `IDG_VS_TOOLS_EXT_TOOLS`); the menu resource version was bumped so SSMS reloads it.
* New `build.cmd` (restore + build + `dist` package) and `install-ssms22.cmd` (install / update into SSMS 22) scripts at the repository root.
* Debug start program is `SSMS.exe` of SSMS 22 and the Debug build deploys to `...\SSMS 22\Release\Common7\IDE\Extensions\SSMSPlus`.

**Local storage (SQLite)**
* SSMS 22.10+ redirects every `SQLitePCLRaw` version to its own 3.x build (see `SSMS.exe.config`), which broke `Microsoft.Data.Sqlite` inside the extension (*"Method not found: SQLitePCL.ISQLite3Provider.sqlite3_win32_set_directory"*).
* The local store now uses **System.Data.SQLite 1.0.119** (outside the version range SSMS redirects). `SQLite.Interop.dll` is shipped in the `x64` / `x86` sub-folders of the extension.
* The database file (`%LocalAppData%\SSMS Plus\SSMS-Plus.sqlite`) and its date format are unchanged, existing history and search indexes are kept.

**Fixes for issues reported on the original repository (akarzazi/SSMSPlus)**
* #24 - Query History: tracking errors are now logged with the full exception.
* #33 - SSMS 22 load failure (`EnvDTE ... BeforeExecuteEventHandler..ctor`), #32 - SSMS 21 support.

**NuGet packages updated (security)**
* `System.Security.Cryptography.Xml` 4.5.0 → 10.0.13 (4.5.0 is affected by CVE-2022-34716; 10.0.8 – 10.0.12 are also flagged on nuget.org).
* `Microsoft.Extensions.*` 2.2.0 → 10.0.13 (aligned with the versions SSMS 22 loads at runtime), with `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Bcl.Cryptography`, `System.Formats.Asn1`, `System.Diagnostics.DiagnosticSource` 10.0.13.
* `System.Memory` 4.6.3, `System.Buffers` 4.6.1, `System.Numerics.Vectors` 4.6.1, `System.Runtime.CompilerServices.Unsafe` 6.1.2, `System.Threading.Tasks.Extensions` 4.6.3, `System.ValueTuple` 4.6.2, `System.ComponentModel.Annotations` 5.0.0.
* `Newtonsoft.Json` 13.0.4, `Dapper.StrongName` 2.1.89, `Microsoft.Xaml.Behaviors.Wpf` 1.1.161, `CsvHelper` 33.1.0 (the CSV logger was adapted to the new API).
* Removed `System.Security.AccessControl`, `System.Security.Permissions`, `System.Security.Principal.Windows` (only needed by the old `Cryptography.Xml`).
* Not changed on purpose: the `Microsoft.VisualStudio.*` 15.x SDK / interop packages and `Microsoft.VSSDK.BuildTools` (compile-time only, provided by SSMS at runtime).

# Contributing

We're glad to know you're interested in the project.

Your contributions are welcome !

## How can I contribute ?

You can contribute in the following ways : 

* Report an issue / Suggest a feature.
* Create a pull request.

## Project Goals
### Goals
* Performance: Provide a **lightweight** plugin to bring to SSMS those missing features.
* Performance: React to events in an async and **non blocking** way.
* Minimal:  Focus on **handy** features that matter for the community.
* Stable: Do not implement features using **`Reflection`** heavy stuff.
* Ever Green: Focus on the latest SSMS Major Editions.

### Non Goals
* Implement domain specific features. (ex: integrate with FTP, Azure Services)
* Implement heavy features like intellisense.
* Seek for compatibility with an old edition of SQL Server or SSMS.


## Debugging

### Debug the SSMS instance

This section explains how to setup Visual Studio to debug the plugin within an SSMS Instance. 

1.  In the Debug section of the main project "SSMSPlus", setup your SSMS path as the startup program (already set in the project for SSMS 22).

`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe`

![SSMS Plus Debug Startup](docs/illustrations/debug-vs-startup.png?raw=true "Documents Export")

2.  In the VSIX section of the main project "SSMSPlus", configure the deployment path for the plugin (already set for the Debug configuration).

`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSPlus`
![SSMS Plus Documents Export](docs/illustrations/debug-vs-copy-vsix.png?raw=true "Documents Export")

> Deploying to `Program Files` requires Visual Studio to run as administrator.

3. That's All ! You may start your the debugging session.

### The Demo project

A [Demo project](src/Demo) is available in the sources.

It is handy for faster UI prototyping, since it does not require to launch SSMS. 

### Troubleshooting

A log record in the folder corresponding to `Environment.SpecialFolder.LocalApplicationData + "\SSMS Plus"` which corresponds generally to `C:\Users\<USER>\AppData\Local\SSMS Plus`.

Theses logs are very useful for diagnostics.

Common SSMS 22 issues:
* **Menu not visible**: close SSMS, run `install-ssms22.cmd` again (or `SSMS.exe /updateconfiguration` as administrator), look under **Tools > SSMS Plus**.
* **"Could not load SSMSPlus" / SQLite errors**: delete the extension folder and reinstall, old `SQLitePCLRaw` / `Microsoft.Data.Sqlite` dlls must not stay next to the new ones.
