@echo off
echo [gh-bridge] Deploying...

set BUILD_DIR=bin\Debug
set DEPLOY_DIR=deploy
set GH_COMP_DIR=%APPDATA%\Grasshopper\Libraries

if not exist %DEPLOY_DIR% mkdir %DEPLOY_DIR%

copy /Y %BUILD_DIR%\HanakoBridge.gha %DEPLOY_DIR%\HanakoBridge.gha
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build output not found. Run 'dotnet build' first.
    pause
    exit /b 1
)

copy /Y %BUILD_DIR%\HanakoBridge.dll %DEPLOY_DIR%\HanakoBridge.dll
copy /Y %BUILD_DIR%\HanakoBridge.pdb %DEPLOY_DIR%\HanakoBridge.pdb

echo [OK] Deployed to %DEPLOY_DIR%\

echo.
echo Copying to Grasshopper Libraries folder...
if not exist "%GH_COMP_DIR%" mkdir "%GH_COMP_DIR%"

copy /Y %DEPLOY_DIR%\HanakoBridge.gha "%GH_COMP_DIR%\HanakoBridge.gha"
if %ERRORLEVEL% NEQ 0 (
    echo [WARN] Cannot copy to GH Libraries folder. Is Grasshopper running?
    echo        Close Grasshopper and run this script again.
    echo        Or manually copy: %DEPLOY_DIR%\HanakoBridge.gha -^> %GH_COMP_DIR%\
    pause
    exit /b 1
)

echo [OK] Deployed to GH Libraries folder.
echo       Restart Grasshopper to load the new version.
