@echo off
rem ---------------------------------------------------------------------------
rem  SSMSPlus - install / update into SQL Server Management Studio 22
rem  (for SSMS 18 / 19 / 20 use install-ssms20.cmd)
rem
rem  Run as Administrator, with SSMS closed:
rem     install-ssms22.cmd ["path to SSMS Common7\IDE"]
rem
rem  Default SSMS path:
rem     C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE
rem
rem  Source of the files (first one found):
rem     SSMSPlus\                         (extracted release zip)
rem     dist\SSMSPlus\                    (created by build.cmd)
rem     src\SSMSPlus\bin\Release\
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "ROOT=%~dp0"
set "IDE=%~1"
if "%IDE%"=="" set "IDE=%ProgramFiles%\Microsoft SQL Server Management Studio 22\Release\Common7\IDE"

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
if not exist "%SRC%\SSMSPlus.dll" set "SRC=%ROOT%dist\SSMSPlus"
if not exist "%SRC%\SSMSPlus.dll" set "SRC=%ROOT%src\SSMSPlus\bin\Release"
if not exist "%SRC%\SSMSPlus.dll" (
    echo [ERROR] Build output not found. Run build.cmd first.
    exit /b 1
)
echo Source : %SRC%

set "TARGET=%IDE%\Extensions\SSMSPlus"
echo Target : %TARGET%

rem --- remove previous version (old SQLitePCLRaw / Microsoft.Data.Sqlite dlls) ---
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
echo Updating SSMS configuration (this can take a minute)...
copy /b "%IDE%\Extensions\extensions.configurationchanged" +,, "%IDE%\Extensions\extensions.configurationchanged" >nul 2>&1
"%IDE%\SSMS.exe" /updateconfiguration

echo.
echo SSMSPlus installed. Start SSMS and open Tools ^> SSMS Plus.
exit /b 0
