:: Reference from https://github.com/Raphire/Win11Debloat/blob/master/Run.bat licensed under MIT license.

@echo off

Powershell -ExecutionPolicy Bypass -Command "& {Start-Process Powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%~dp0tiny11maker.ps1""' -Verb RunAs}"
