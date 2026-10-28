@echo off
echo ========================================================
echo   Running MineTunnel Multi-Player CGNAT Simulation      
echo ========================================================
if not exist bin\MultiPlayerCgnatTest.exe (
    echo Compiling MultiPlayerCgnatTest.exe ...
    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:bin\MultiPlayerCgnatTest.exe MultiPlayerCgnatTest.cs
)
bin\MultiPlayerCgnatTest.exe
echo ========================================================
pause
