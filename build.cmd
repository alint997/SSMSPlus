@echo off
rem ---------------------------------------------------------------------------
rem  SSMSPlus - build script
rem
rem  Usage:  build.cmd [Release|Debug] [ssms20]      (default: Release, SSMS 21 / 22)
rem
rem  Requires Visual Studio 2022+ with the "Visual Studio extension development"
rem  workload. Output for SSMS 21 / 22:
rem     dist\SSMSPlus.vsix         - VSIX package
rem     dist\SSMSPlus\             - extension files, ready to copy into
rem                                  ...\SSMS 22\Release\Common7\IDE\Extensions\SSMSPlus
rem     dist\SSMSPlus-ssms22.zip   - release package: SSMSPlus\ + install-ssms22.cmd
rem  With "ssms20" the extension is built for SSMS 18 / 19 / 20 (32-bit, compiled
rem  against src\SSMSPlus\assemblies\ssms20, MSBuild property SsmsVersion=20):
rem     dist-ssms20\SSMSPlus\             - extension files for ...\SSMS 20\Common7\IDE\Extensions\SSMSPlus
rem     dist-ssms20\SSMSPlus-ssms20.zip   - release package: SSMSPlus\ + install-ssms20.cmd
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CONFIG=Release"
set "SSMS=22"
:args
if "%~1"=="" goto :argsdone
if /i "%~1"=="ssms20" (set "SSMS=20") else (set "CONFIG=%~1")
shift
goto :args
:argsdone

rem --- locate MSBuild through vswhere -----------------------------------------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [ERROR] vswhere.exe not found. Install Visual Studio 2022 or newer.
    exit /b 1
)

set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Workload.VisualStudioExtension -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not defined MSBUILD (
    echo [ERROR] MSBuild with the "Visual Studio extension development" workload was not found.
    exit /b 1
)
echo Using MSBuild: %MSBUILD%

rem --- restore NuGet packages (packages.config) ------------------------------
echo.
echo === Restore NuGet packages ===
"%MSBUILD%" "%ROOT%src\SSMSPlus.sln" /t:Restore /p:RestorePackagesConfig=true /p:Configuration=%CONFIG% /v:minimal /nologo
if errorlevel 1 goto :error

rem --- build ------------------------------------------------------------------
rem  CopyVsixExtensionFiles=false: do not deploy into Program Files during the
rem  build (that needs admin rights). Use install-ssms22.cmd for that.
echo.
echo === Build %CONFIG% for SSMS %SSMS% ===
"%MSBUILD%" "%ROOT%src\SSMSPlus.sln" /t:Rebuild /p:Configuration=%CONFIG% /p:SsmsVersion=%SSMS% /p:CopyVsixExtensionFiles=false /p:DeployExtension=false /m /v:minimal /nologo
if errorlevel 1 goto :error

set "VSIX=%ROOT%src\SSMSPlus\bin\%CONFIG%\SSMSPlus.vsix"
if not exist "%VSIX%" (
    echo [ERROR] %VSIX% was not produced.
    goto :error
)

rem --- package into dist\ ------------------------------------------------------
echo.
echo === Package ===
if "%SSMS%"=="20" goto :package20
set "DIST=%ROOT%dist"
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"
copy /y "%VSIX%" "%DIST%\SSMSPlus.vsix" >nul

rem The VSIX is a zip: its content is exactly what SSMS needs in Extensions\SSMSPlus
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "Copy-Item -LiteralPath '%DIST%\SSMSPlus.vsix' -Destination '%DIST%\tmp.zip';" ^
  "Expand-Archive -LiteralPath '%DIST%\tmp.zip' -DestinationPath '%DIST%\SSMSPlus' -Force;" ^
  "Remove-Item -LiteralPath '%DIST%\tmp.zip';" ^
  "Remove-Item -LiteralPath '%DIST%\SSMSPlus\[Content_Types].xml' -ErrorAction SilentlyContinue;" ^
  "Copy-Item -LiteralPath '%ROOT%install-ssms22.cmd' -Destination '%DIST%\install-ssms22.cmd';" ^
  "Compress-Archive -Path '%DIST%\SSMSPlus','%DIST%\install-ssms22.cmd' -DestinationPath '%DIST%\SSMSPlus-ssms22.zip' -Force"
if errorlevel 1 goto :error

echo.
echo Build OK.
echo   VSIX      : %DIST%\SSMSPlus.vsix
echo   Files     : %DIST%\SSMSPlus\
echo   Zip       : %DIST%\SSMSPlus-ssms22.zip
echo.
echo To install into SSMS 22 run (as Administrator):  install-ssms22.cmd
exit /b 0

rem --- SSMS 18 / 19 / 20: the files of the VSIX with a manifest for the Visual Studio 2017 shell ---
rem  Runtime libraries: SSMS 18 - 20 have older System.Threading.Tasks.Extensions, Microsoft.Bcl.AsyncInterfaces,
rem  System.Memory, System.Buffers, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe and
rem  System.ValueTuple. A second, newer copy next to the extension would duplicate types such as ValueTask, so
rem  they are left out and Command1Package.OnAssemblyResolve hands out the copy SSMS uses. Only
rem  System.Diagnostics.DiagnosticSource (needed in version 10 by Microsoft.Extensions.*, left out of the VSIX
rem  because Visual Studio ships it) is copied next to the extension.
:package20
set "DIST=%ROOT%dist-ssms20"
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "Copy-Item -LiteralPath '%VSIX%' -Destination '%DIST%\tmp.zip';" ^
  "Expand-Archive -LiteralPath '%DIST%\tmp.zip' -DestinationPath '%DIST%\SSMSPlus' -Force;" ^
  "Remove-Item -LiteralPath '%DIST%\tmp.zip';" ^
  "foreach ($f in '[Content_Types].xml','manifest.json','catalog.json') { Remove-Item -LiteralPath ('%DIST%\SSMSPlus\' + $f) -ErrorAction SilentlyContinue };" ^
  "foreach ($f in 'Microsoft.Bcl.AsyncInterfaces.dll','System.Memory.dll','System.Buffers.dll','System.Numerics.Vectors.dll','System.Threading.Tasks.Extensions.dll','System.Runtime.CompilerServices.Unsafe.dll','System.ValueTuple.dll') { Remove-Item -LiteralPath ('%DIST%\SSMSPlus\' + $f) -ErrorAction SilentlyContinue };" ^
  "Copy-Item -LiteralPath '%ROOT%src\SSMSPlus\bin\%CONFIG%\System.Diagnostics.DiagnosticSource.dll' -Destination '%DIST%\SSMSPlus\';" ^
  "$q = [char]34; $m = '%DIST%\SSMSPlus\extension.vsixmanifest'; $x = [IO.File]::ReadAllText($m);" ^
  "$x = [regex]::Replace($x, '(?s)<Installation[^>]*>.*?</Installation>', '<Installation><InstallationTarget Id=' + $q + 'ssms' + $q + ' Version=' + $q + '[15.0,16.0)' + $q + ' /></Installation>');" ^
  "$x = [regex]::Replace($x, '(<Prerequisite [^>]*Version=.)[^' + $q + ']*', '${1}[15.0,16.0)');" ^
  "$x = $x.Replace('SSMS 21 / 22', 'SSMS 18 / 19 / 20');" ^
  "[IO.File]::WriteAllText($m, $x, (New-Object Text.UTF8Encoding($false)));" ^
  "Copy-Item -LiteralPath '%ROOT%install-ssms20.cmd' -Destination '%DIST%\install-ssms20.cmd';" ^
  "Compress-Archive -Path '%DIST%\SSMSPlus','%DIST%\install-ssms20.cmd' -DestinationPath '%DIST%\SSMSPlus-ssms20.zip' -Force"
if errorlevel 1 goto :error

echo.
echo Build OK (SSMS 18 / 19 / 20).
echo   Files     : %DIST%\SSMSPlus\
echo   Zip       : %DIST%\SSMSPlus-ssms20.zip
echo.
echo To install into SSMS 20 run (as Administrator):  install-ssms20.cmd
echo Note: src\*\bin now hold the SSMS 20 build; run build.cmd again for SSMS 22.
exit /b 0

:error
echo.
echo [ERROR] Build failed.
exit /b 1
