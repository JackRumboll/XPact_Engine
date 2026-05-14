@echo off
rem ## XPact Engine - GenerateProjectFiles.bat. Single-line wrapper that invokes
rem ## XBT.exe -genproject via CallBuildTool.bat (which builds XBT if needed).
call "%~dp0Scripts\Windows\CallBuildTool.bat" -genproject %* & exit /B %ERRORLEVEL%
