@echo off
setlocal
set "NEXUSBGI_DATA_ROOT="
powershell.exe -NoProfile -WindowStyle Hidden -File "%~dp0Start-Mistletoe.ps1"
endlocal
