@echo off
echo ========================================================
echo       Building MineTunnel Windows Installer (.exe)
echo ========================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo [ERROR] C# compiler csc.exe not found!
    exit /b 1
)

if not exist "bin\MineTunnel.exe" (
    echo [INFO] MineTunnel.exe not found in bin\. Building it first...
    call build.bat
    if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
)

if not exist bin mkdir bin

echo Compiling MineTunnelSetup.exe with embedded MineTunnel.exe ...

%CSC% /target:winexe ^
      /out:bin\MineTunnelSetup.exe ^
      /optimize+ ^
      /platform:anycpu ^
      /win32manifest:app.manifest ^
      /resource:bin\MineTunnel.exe,MineTunnel.exe ^
      /resource:README.md,README.md ^
      /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll ^
      Installer\SetupForm.cs Installer\Program.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo  [SUCCESS] MineTunnelSetup.exe built successfully!
    echo  Installer Location: bin\MineTunnelSetup.exe
    echo ========================================================
    echo.
) else (
    echo.
    echo [FAILED] Installer compilation failed with error code %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)
