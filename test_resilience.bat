@echo off
echo ========================================================
echo   Running MineTunnel Network Resilience Test Suite      
echo ========================================================
if not exist bin\NetworkResilienceTest.exe (
    echo Compiling NetworkResilienceTest.exe ...
    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:bin\NetworkResilienceTest.exe /r:System.dll,System.Core.dll,System.Security.dll /recurse:Common\*.cs /recurse:Core\*.cs NetworkResilienceTest.cs
)
bin\NetworkResilienceTest.exe
echo ========================================================
pause
