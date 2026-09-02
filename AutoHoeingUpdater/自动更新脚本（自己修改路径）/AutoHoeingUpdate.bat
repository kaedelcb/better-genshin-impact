@echo off
:: 设置代码页为UTF-8
chcp 65001

::管理员权限检测
NET SESSION >nul 2>&1
if %errorlevel% neq 0 (
    echo 请右键以管理员身份运行此脚本！
    pause
    exit
)

:: 执行更新程序
D:\AAA挂机专区\AutoHoeingUpdater\AutoHoeingUpdater.exe --silent --all

exit /b
