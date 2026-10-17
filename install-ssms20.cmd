@echo off
rem ---------------------------------------------------------------------------
rem  SSMSPlus - install / update into SQL Server Management Studio 18 / 19 / 20
rem  (for SSMS 21 / 22 use install-ssms22.cmd)
rem
rem  Run as Administrator, with SSMS closed:
rem     install-ssms20.cmd ["path to SSMS Common7\IDE"]
rem
rem  Default SSMS path:
rem     C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE
rem  SSMS 19 / 18: pass ...\Microsoft SQL Server Management Studio 19 (or 18)\Common7\IDE
rem
rem  Source of the files (first one found):
rem     SSMSPlus\                         (extracted release zip)
rem     dist-ssms20\SSMSPlus\             (created by build.cmd ssms20)
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "ROOT=%~dp0"
set "IDE=%~1"
if "%IDE%"=="" set "IDE=%ProgramFiles(x86)%\Microsoft SQL Server Management Studio 20\Common7\IDE"

if not exist "%IDE%\SSMS.exe" (
    echo [ERROR] SSMS.exe not found in "%IDE%".
    echo Pass the SSMS Common7\IDE folder as parameter.
    exit /b 1
)

rem --- admin rights ----------------------------------------------------------
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Run this script as Administrator ^(right click ^> Run as administrator^).
    exit /b 1
)

rem --- SSMS must be closed ---------------------------------------------------
tasklist /fi "imagename eq SSMS.exe" | find /i "SSMS.exe" >nul
if not errorlevel 1 (
    echo [ERROR] SSMS is running. Close all SSMS windows and run the script again.
    exit /b 1
)

rem --- source files -----------------------------------------------------------
set "SRC=%ROOT%SSMSPlus"
if not exist "%SRC%\SSMSPlus.dll" set "SRC=%ROOT%dist-ssms20\SSMSPlus"
if not exist "%SRC%\SSMSPlus.dll" (
    echo [ERROR] Build output not found. Run build.cmd ssms20 first.
    exit /b 1
)
rem the files must come from the SSMS 20 build (manifest for the Visual Studio 2017 shell)
findstr /i /c:"Microsoft.VisualStudio.Ssms" "%SRC%\extension.vsixmanifest" >nul
if not errorlevel 1 (
    echo [ERROR] "%SRC%" is not the SSMS 18 - 20 build. Run build.cmd ssms20.
    exit /b 1
)
echo Source : %SRC%

set "TARGET=%IDE%\Extensions\SSMSPlus"
echo Target : %TARGET%

rem --- remove previous version (also SSMS Plus 6.x: Microsoft.Data.Sqlite / SQLitePCLRaw dlls) ---
if exist "%TARGET%" (
    echo Removing previous version...
    rmdir /s /q "%TARGET%"
    if exist "%TARGET%" (
        echo [ERROR] Could not delete "%TARGET%". Is SSMS still running?
        exit /b 1
    )
)

rem --- copy ---------------------------------------------------------------------
echo Copying files...
xcopy "%SRC%\*" "%TARGET%\" /e /i /y /q >nul
if errorlevel 1 (
    echo [ERROR] Copy failed.
    exit /b 1
)

rem --- refresh the SSMS extension / menu cache ------------------------------------
rem  SSMS 18 - 20 rebuild their extension cache when this file changes.
echo Updating SSMS configuration...
if exist "%IDE%\Extensions\extensions.configurationchanged" (
    copy /b "%IDE%\Extensions\extensions.configurationchanged" +,, "%IDE%\Extensions\extensions.configurationchanged" >nul 2>&1
) else (
    type nul > "%IDE%\Extensions\extensions.configurationchanged"
)

echo.
echo SSMSPlus installed. Start SSMS and open Tools ^> SSMS Plus.
exit /b 0
