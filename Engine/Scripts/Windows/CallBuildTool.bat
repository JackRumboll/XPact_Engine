@echo off

rem ## XPact Engine - CallBuildTool.bat. Driver script for XBT (XPact Build Tool).
rem ## Copyright Epic Games, Inc. All Rights Reserved. Modified by Simgenics for XPact Engine.
rem ##
rem ## Builds XBT.csproj via MSBuild (or 'dotnet build' fallback) if XBT.exe is
rem ## missing or stale, then runs XBT.exe with the supplied arguments. The
rem ## expected location for this script is Engine\Scripts\Windows.

setlocal enabledelayedexpansion

set ScriptDir=%~dp0
set EngineDir=%ScriptDir%..\..
set EngineDir=%EngineDir:\.\=\%
pushd %EngineDir%
set EngineDir=%CD%
popd

set XBTProj=%EngineDir%\Source\Programs\XBT\XBT.csproj
set XBTExe=%EngineDir%\Source\Programs\XBT\bin\Release\net9.0\XBT.exe

if not exist "%XBTProj%" (
    echo ERROR: XBT project not found at "%XBTProj%"
    exit /B 1
)

rem ## Staleness check: rebuild XBT if the .exe is missing or any XBT source is
rem ## newer than the .exe. We rebuild on every miss; faster heuristics belong
rem ## in Task 0.4.
set NEEDS_BUILD=0
if not exist "%XBTExe%" set NEEDS_BUILD=1

if "%NEEDS_BUILD%"=="0" (
    rem ## Use one PowerShell invocation that walks the source tree and prints STALE only if any file is newer.
    for /f "delims=" %%s in ('powershell -NoProfile -Command "$exe=Get-Item -LiteralPath '%XBTExe%'; $latest=(Get-ChildItem -Path '%EngineDir%\Source\Programs\XBT' -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1); if ($latest -and $latest.LastWriteTime -gt $exe.LastWriteTime) { 'STALE' } else { 'OK' }"') do (
        if "%%s"=="STALE" set NEEDS_BUILD=1
    )
)

if "%NEEDS_BUILD%"=="1" (
    echo [XBT] Building XBT from %XBTProj%...
    call "%ScriptDir%GetMSBuildPath.bat"
    if errorlevel 1 (
        echo [XBT] MSBuild discovery failed; falling back to 'dotnet build'.
        dotnet build "%XBTProj%" -c Release -v minimal
        if errorlevel 1 (
            echo ERROR: dotnet build of XBT failed.
            exit /B 1
        )
    ) else (
        !MSBUILD_EXE! "%XBTProj%" /t:"Restore;Build" /p:Configuration=Release /v:minimal /nologo
        if errorlevel 1 (
            echo ERROR: MSBuild of XBT failed.
            exit /B 1
        )
    )
)

if not exist "%XBTExe%" (
    echo ERROR: XBT build succeeded but %XBTExe% does not exist.
    exit /B 1
)

"%XBTExe%" %*
exit /B %ERRORLEVEL%
