@echo off
echo ========================================================
echo       Publish MineTunnel to GitHub Repository
echo ========================================================
echo.

set GH="C:\Program Files\GitHub CLI\gh.exe"
set GIT="C:\Users\SPIRO\AppData\Local\GitHubDesktop\app-3.6.4\resources\app\git\cmd\git.exe"

if not exist %GH% (
    echo [ERROR] GitHub CLI not found at %GH%.
    pause
    exit /b 1
)

echo [1/2] Checking GitHub authentication status...
%GH% auth status >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo.
    echo [INFO] Logging into GitHub via browser...
    echo A browser window will open for one-click authorization.
    echo.
    %GH% auth login --web -h github.com -p https
    if %ERRORLEVEL% neq 0 (
        echo [ERROR] Authentication was not completed.
        pause
        exit /b %ERRORLEVEL%
    )
)

echo.
echo [2/2] Creating remote repository and pushing main branch...
%GH% repo create MineTunnel --public --source=. --remote=origin --push

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo  [SUCCESS] Repository successfully published to GitHub!
    echo  URL: https://github.com/incanalita97/MineTunnel
    echo ========================================================
) else (
    echo.
    echo [INFO] Attempting git push if remote already exists...
    %GIT% push -u origin main
)

echo.
pause
