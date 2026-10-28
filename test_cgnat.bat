@echo off
echo ========================================================
echo     Running PLDT CGNAT UDP Hole Punching Simulation     
echo ========================================================
if not exist bin\CgnatTest.exe (
    echo Compiling CgnatTest.exe ...
    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:bin\CgnatTest.exe CgnatTest.cs
)
bin\CgnatTest.exe
echo ========================================================
pause
