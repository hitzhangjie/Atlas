@echo off
rem 启动 Atlas（会以管理员权限运行，因为 app.manifest 要求）
rem 优先使用 dist\Atlas.exe（Release 构建），其次 app\Atlas.exe（开发构建）
cd /d "%~dp0"
if exist "dist\Atlas.exe" (
    start "" "dist\Atlas.exe"
) else if exist "app\Atlas.exe" (
    start "" "app\Atlas.exe"
) else (
    echo 没有找到 Atlas.exe。
    echo 先编译：
    echo   dotnet publish src\Atlas.App\Atlas.App.csproj -c Release -r win-x64 --self-contained false -o dist
    pause
)
