@echo off
setlocal
chcp 65001 >nul
python -X utf8 "%~dp0deploy.py" %*
set "deploy_result=%errorlevel%"
echo.
pause
exit /b %deploy_result%
