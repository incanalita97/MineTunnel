@echo off
echo ========================================================
echo         Building MineTunnel for Windows 10/11
echo ========================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% set CSC="C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo [ERROR] C# compiler csc.exe not found!
    pause
    exit /b 1
)

if not exist bin mkdir bin

echo Using C# Compiler: %CSC%
echo.
echo [1/3] Compiling MineTunnel.exe ...

%CSC% /target:winexe ^
      /out:bin\MineTunnel.exe ^
      /optimize+ ^
      /platform:anycpu ^
      /win32manifest:app.manifest ^
      /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Xml.dll,System.Security.dll ^
      /recurse:Common\*.cs /recurse:Core\*.cs /recurse:UI\*.cs

if %ERRORLEVEL% neq 0 (
    echo.
    echo [FAILED] Compilation of MineTunnel.exe failed with error %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)

echo [1/3] SUCCESS: bin\MineTunnel.exe created.
echo.
echo [2/3] Compiling Zero-Port-Forwarding Relay (MineTunnelRelay.exe) ...

%CSC% /target:exe ^
      /out:bin\MineTunnelRelay.exe ^
      /optimize+ ^
      /platform:anycpu ^
      /r:System.dll,System.Core.dll,System.Security.dll ^
      Relay\RelayServer.cs Relay\Program.cs Common\TunnelPacket.cs

if %ERRORLEVEL% neq 0 (
    echo.
    echo [FAILED] Compilation of MineTunnelRelay.exe failed with error %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)

echo [2/3] SUCCESS: bin\MineTunnelRelay.exe created.
echo.
echo [3/3] Compiling Windows Installer (MineTunnelSetup.exe) ...

%CSC% /target:winexe ^
      /out:bin\MineTunnelSetup.exe ^
      /optimize+ ^
      /platform:anycpu ^
      /win32manifest:app.manifest ^
      /resource:bin\MineTunnel.exe,MineTunnel.exe ^
      /resource:bin\MineTunnelRelay.exe,MineTunnelRelay.exe ^
      /resource:Relay\minetunnel_relay.py,minetunnel_relay.py ^
      /resource:Relay\install_oracle.sh,install_oracle.sh ^
      /resource:README.md,README.md ^
      /resource:INSTRUCTIONS.md,INSTRUCTIONS.md ^
      /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll ^
      Installer\SetupForm.cs Installer\Program.cs

if %ERRORLEVEL% neq 0 (
    echo.
    echo [FAILED] Compilation of MineTunnelSetup.exe failed with error %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)

echo [3/3] SUCCESS: bin\MineTunnelSetup.exe created.
echo.
echo [4/4] Compiling Test Runner (TestRunner.exe) ...

%CSC% /target:exe ^
      /out:bin\TestRunner.exe ^
      /optimize+ ^
      /platform:anycpu ^
      /main:MineTunnel.Test.Program ^
      /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Security.dll ^
      /recurse:Common\*.cs /recurse:Core\*.cs /recurse:Relay\*.cs /recurse:UI\*.cs /recurse:Installer\*.cs TestRunner.cs

if %ERRORLEVEL% neq 0 (
    echo.
    echo [FAILED] Compilation of TestRunner.exe failed with error %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)

echo [4/4] SUCCESS: bin\TestRunner.exe created.
echo.
echo ========================================================
echo  [BUILD COMPLETE]
echo  - Standalone Application   : bin\MineTunnel.exe
echo  - Zero-Port-Forward Relay  : bin\MineTunnelRelay.exe
echo  - Windows Installer (.exe) : bin\MineTunnelSetup.exe
echo  - Integration Test Runner  : bin\TestRunner.exe
echo ========================================================
