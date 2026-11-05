@echo off
chcp 65001 >nul
cd /d "%~dp0"
python -X utf8 -B "tools\local-build\build.py"
set "bgi_build_exit=%ERRORLEVEL%"
echo.
pause
exit /b %bgi_build_exit%
