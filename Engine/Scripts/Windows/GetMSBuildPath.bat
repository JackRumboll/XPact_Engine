@echo off

rem ## XPact Engine utility script - port of Unreal Engine's Engine\Build\BatchFiles\GetMSBuildPath.bat.
rem ## Copyright Epic Games, Inc. All Rights Reserved. Modified by Simgenics for XPact Engine.
rem ##
rem ## Locates an MSBuild.exe shipped with Visual Studio 2022+ via vswhere.exe.
rem ## On success, sets MSBUILD_EXE in the calling environment and exits 0.

set MSBUILD_EXE=

if not exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" goto no_vswhere
for /f "delims=" %%i in ('"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere" -prerelease -latest -products * -requires Microsoft.Component.MSBuild -property installationPath') do (
    if exist "%%i\MSBuild\Current\Bin\MSBuild.exe" (
        set MSBUILD_EXE="%%i\MSBuild\Current\Bin\MSBuild.exe"
        goto Succeeded
    )
)
:no_vswhere

echo ERROR: vswhere.exe was not found, or no MSBuild was located via vswhere. Install Visual Studio 2022 or later with the MSBuild component.
exit /B 1

:Succeeded
exit /B 0
