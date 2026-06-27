@echo off
setlocal
pushd "%~dp0"

echo Building ShortcutHookDaemon...
dotnet publish ..\ShortcutHookDaemon\ShortcutHookDaemon.csproj -c Release -v minimal || goto :err
if not exist Resources mkdir Resources
copy /Y "..\ShortcutHookDaemon\bin\Release\net8.0-windows\win-x64\publish\ShortcutHookDaemon.exe" "Resources\ShortcutHookDaemon.exe" >nul || goto :err

echo Building ShortcutHookUI...
dotnet publish -c Release -v minimal || goto :err
copy /Y "bin\Release\net8.0-windows\win-x64\publish\ShortcutHookUI.exe" "..\build\ShortcutHookUI.exe" >nul || goto :err

echo.
echo Published: %~dp0..\build\ShortcutHookUI.exe
echo.
echo Removing stale daemon...
if exist "C:\Tools\ShortcutHook\ShortcutHookDaemon.exe" (
    del /F /Q "C:\Tools\ShortcutHook\ShortcutHookDaemon.exe"
    echo Deleted: C:\Tools\ShortcutHook\ShortcutHookDaemon.exe
)
if exist "C:\Tools\ShortcutHook\ShortcutHook.ps1" (
    del /F /Q "C:\Tools\ShortcutHook\ShortcutHook.ps1"
    echo Deleted stale: C:\Tools\ShortcutHook\ShortcutHook.ps1
)
echo.
echo Launching new build...
start "" "..\build\ShortcutHookUI.exe"
popd
exit /b 0
:err
echo.
echo Publish failed.
popd
exit /b 1
