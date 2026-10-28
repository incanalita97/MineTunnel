@echo off
setlocal
cd /d "%~dp0"

echo ========================================================
echo       Publish / Sync MineTunnel to GitHub Repository
echo ========================================================
echo.

set "PATH=C:\Users\SPIRO\AppData\Local\GitHubDesktop\app-3.6.4\resources\app\git\cmd;%PATH%"
set GH="C:\Program Files\GitHub CLI\gh.exe"
set GIT="C:\Users\SPIRO\AppData\Local\GitHubDesktop\app-3.6.4\resources\app\git\cmd\git.exe"

echo Current Directory: %CD%
echo.

echo [1/2] Checking remote status...
git remote -v
if %ERRORLEVEL% neq 0 (
    echo Remote not found. Creating repository via GitHub CLI...
    %GH% repo create MineTunnel --public --source=. --remote=origin --push
) else (
    echo Remote 'origin' is configured.
)

echo.
echo [2/2] Pushing latest changes to origin/main...
git push -u origin main

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo  [SUCCESS] All code is up to date on GitHub!
    echo  URL: https://github.com/incanalita97/MineTunnel
    echo ========================================================
) else (
    echo.
    echo [ERROR] Push failed. Check your internet connection or GitHub credentials.
)

echo.
pause
