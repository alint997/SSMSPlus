@echo off
rem ---------------------------------------------------------------------------
rem  SSMSPlus - build script (SSMS 21 / 22)
rem
rem  Usage:  build.cmd [Release|Debug]        (default: Release)
rem
rem  Requires Visual Studio 2022+ with the "Visual Studio extension development"
rem  workload. Output:
rem     dist\SSMSPlus.vsix         - VSIX package
rem     dist\SSMSPlus\             - extension files, ready to copy into
rem                                  ...\SSMS 22\Release\Common7\IDE\Extensions\SSMSPlus
rem     dist\SSMSPlus-ssms22.zip   - zip of dist\SSMSPlus
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

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
echo === Build %CONFIG% ===
"%MSBUILD%" "%ROOT%src\SSMSPlus.sln" /t:Rebuild /p:Configuration=%CONFIG% /p:CopyVsixExtensionFiles=false /p:DeployExtension=false /m /v:minimal /nologo
if errorlevel 1 goto :error

set "VSIX=%ROOT%src\SSMSPlus\bin\%CONFIG%\SSMSPlus.vsix"
if not exist "%VSIX%" (
    echo [ERROR] %VSIX% was not produced.
    goto :error
)

rem --- package into dist\ ------------------------------------------------------
echo.
echo === Package ===
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
  "Compress-Archive -Path '%DIST%\SSMSPlus' -DestinationPath '%DIST%\SSMSPlus-ssms22.zip' -Force"
if errorlevel 1 goto :error

echo.
echo Build OK.
echo   VSIX      : %DIST%\SSMSPlus.vsix
echo   Files     : %DIST%\SSMSPlus\
echo   Zip       : %DIST%\SSMSPlus-ssms22.zip
echo.
echo To install into SSMS 22 run (as Administrator):  install-ssms22.cmd
exit /b 0

:error
echo.
echo [ERROR] Build failed.
exit /b 1
