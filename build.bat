@echo off

if '%1'=='/?' goto help
if '%1'=='-help' goto help
if '%1'=='-h' goto help

powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0\build.ps1' %*; if ($psake.build_success -eq $false) { exit 1 } else { exit 0 }"
PAUSE
goto :eof

:help
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0\build.ps1' -help"
PAUSE