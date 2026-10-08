@echo off
rem Build a self-contained single exe: dist\SelfContained\TwitchDropsMiner.exe
rem Usage: build.bat  or  build.bat -Mode Framework
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
pause
