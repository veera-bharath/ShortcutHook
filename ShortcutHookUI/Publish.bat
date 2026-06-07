@echo off
setlocal
pushd "%~dp0"
dotnet publish -c Release -v minimal || goto :err
copy /Y "bin\Release\net8.0-windows\win-x64\publish\ShortcutHookUI.exe" "..\build\ShortcutHookUI.exe" >nul || goto :err
echo.
echo Published: %~dp0..\build\ShortcutHookUI.exe
echo.
echo Removing stale daemon script...
if exist "C:\Tools\ShortcutHook\ShortcutHook.ps1" (
    del /F /Q "C:\Tools\ShortcutHook\ShortcutHook.ps1"
    echo Deleted: C:\Tools\ShortcutHook\ShortcutHook.ps1
) else (
    echo Not found, skipping: C:\Tools\ShortcutHook\ShortcutHook.ps1
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
