@echo off

rem ## XPact Engine - CallBuildTool.bat. Driver script for XBT (XPact Build Tool).
rem ## Copyright Epic Games, Inc. All Rights Reserved. Modified by Simgenics for XPact Engine.
rem ##
rem ## Builds XBT.csproj and XHT.csproj via MSBuild (or 'dotnet build' fallback)
rem ## if either .exe is missing or stale, then runs XBT.exe with the supplied
rem ## arguments. XBT itself shells out to XHT.exe later in the pipeline; we
rem ## build both up-front to keep "stale build tooling" failures off the hot path.
rem ## Expected location: Engine\Scripts\Windows.

setlocal enabledelayedexpansion

set ScriptDir=%~dp0
set EngineDir=%ScriptDir%..\..
set EngineDir=%EngineDir:\.\=\%
pushd %EngineDir%
set EngineDir=%CD%
popd

set XBTProj=%EngineDir%\Source\Programs\XBT\XBT.csproj
set XBTExe=%EngineDir%\Source\Programs\XBT\bin\Release\net9.0\XBT.exe

set XHTProj=%EngineDir%\Source\Programs\XHT\XHT.csproj
set XHTExe=%EngineDir%\Source\Programs\XHT\bin\Release\net9.0\XHT.exe

if not exist "%XBTProj%" (
    echo ERROR: XBT project not found at "%XBTProj%"
    exit /B 1
)
if not exist "%XHTProj%" (
    echo ERROR: XHT project not found at "%XHTProj%"
    exit /B 1
)

rem ## Find MSBuild once; both staleness branches reuse the discovered path.
call "%ScriptDir%GetMSBuildPath.bat"
set HAVE_MSBUILD=0
if not errorlevel 1 (
    if not "!MSBUILD_EXE!"=="" (
        set HAVE_MSBUILD=1
    )
)

rem ## ------------------------------------------------------------------
rem ## XBT staleness check
rem ## ------------------------------------------------------------------
set NEEDS_BUILD_XBT=0
if not exist "%XBTExe%" set NEEDS_BUILD_XBT=1

if "%NEEDS_BUILD_XBT%"=="0" (
    rem ## XBT also depends on XPact.Build (shared POCO UhtInputManifest.cs) and
    rem ## XPact.Core. Walk all three project trees in one PowerShell so we don't
    rem ## under-rebuild after a shared-POCO edit (mirrors XHT staleness below).
    for /f "delims=" %%s in ('powershell -NoProfile -Command "$exe=Get-Item -LiteralPath '%XBTExe%'; $sources=@('%EngineDir%\Source\Programs\XBT','%EngineDir%\Source\Programs\XPact.Build','%EngineDir%\Source\Programs\XPact.Core'); $latest=($sources | ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter *.cs -ErrorAction SilentlyContinue } | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1); if ($latest -and $latest.LastWriteTime -gt $exe.LastWriteTime) { 'STALE' } else { 'OK' }"') do (
        if "%%s"=="STALE" set NEEDS_BUILD_XBT=1
    )
)

if "%NEEDS_BUILD_XBT%"=="1" (
    echo [XBT] Building XBT from %XBTProj%...
    if "%HAVE_MSBUILD%"=="1" (
        !MSBUILD_EXE! "%XBTProj%" /t:"Restore;Build" /p:Configuration=Release /v:minimal /nologo
        if errorlevel 1 (
            echo ERROR: MSBuild of XBT failed.
            exit /B 1
        )
    ) else (
        echo [XBT] MSBuild not discovered; falling back to 'dotnet build'.
        dotnet build "%XBTProj%" -c Release -v minimal
        if errorlevel 1 (
            echo ERROR: dotnet build of XBT failed.
            exit /B 1
        )
    )
)

if not exist "%XBTExe%" (
    echo ERROR: XBT build succeeded but %XBTExe% does not exist.
    exit /B 1
)

rem ## ------------------------------------------------------------------
rem ## XHT staleness check (same pattern; XHT.csproj is shallow so adding
rem ## a parallel block keeps the script obvious over abstraction).
rem ## ------------------------------------------------------------------
set NEEDS_BUILD_XHT=0
if not exist "%XHTExe%" set NEEDS_BUILD_XHT=1

if "%NEEDS_BUILD_XHT%"=="0" (
    rem ## XHT also depends on XPact.Build/Manifest/UhtInputManifest.cs and
    rem ## XPact.Core. Walk both project trees in one PowerShell so we don't
    rem ## under-rebuild after a shared-POCO edit.
    for /f "delims=" %%s in ('powershell -NoProfile -Command "$exe=Get-Item -LiteralPath '%XHTExe%'; $sources=@('%EngineDir%\Source\Programs\XHT','%EngineDir%\Source\Programs\XPact.Build','%EngineDir%\Source\Programs\XPact.Core'); $latest=($sources | ForEach-Object { Get-ChildItem -Path $_ -Recurse -Filter *.cs -ErrorAction SilentlyContinue } | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1); if ($latest -and $latest.LastWriteTime -gt $exe.LastWriteTime) { 'STALE' } else { 'OK' }"') do (
        if "%%s"=="STALE" set NEEDS_BUILD_XHT=1
    )
)

if "%NEEDS_BUILD_XHT%"=="1" (
    echo [XBT] Building XHT from %XHTProj%...
    if "%HAVE_MSBUILD%"=="1" (
        !MSBUILD_EXE! "%XHTProj%" /t:"Restore;Build" /p:Configuration=Release /v:minimal /nologo
        if errorlevel 1 (
            echo ERROR: MSBuild of XHT failed.
            exit /B 1
        )
    ) else (
        echo [XBT] MSBuild not discovered; falling back to 'dotnet build'.
        dotnet build "%XHTProj%" -c Release -v minimal
        if errorlevel 1 (
            echo ERROR: dotnet build of XHT failed.
            exit /B 1
        )
    )
)

if not exist "%XHTExe%" (
    echo ERROR: XHT build succeeded but %XHTExe% does not exist.
    exit /B 1
)

"%XBTExe%" %*
exit /B %ERRORLEVEL%
