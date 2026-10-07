@echo off
setlocal
if not exist "%~dp0BetterGI.exe" goto missing
if not exist "%~dp0Tools\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe" goto missing
set "NEXUSBGI_DATA_ROOT=E:\Program Files\better-genshin-impact-LCB\_workflow\final-ui-01a11220\own-runtime\assistant-data"
start "" /D "%~dp0" "%~dp0BetterGI.exe"
start "" /D "%~dp0Tools\MultiplayerHoeingAssistant" "%~dp0Tools\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe"
exit /b 0
:missing
echo The matching BetterGI and assistant files were not found.
pause
exit /b 1
