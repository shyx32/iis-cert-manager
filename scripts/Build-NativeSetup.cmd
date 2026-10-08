@echo off
setlocal
set "ROOT=%~1"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" exit /b 1
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL exit /b 1
call "%VSINSTALL%\VC\Auxiliary\Build\vcvarsall.bat" x64
if errorlevel 1 exit /b 1
pushd "%ROOT%\artifacts\native-setup"
rc.exe /nologo /c65001 /fo setup.res setup.rc
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /utf-8 /W4 /WX /EHsc /MT /O2 /DUNICODE /D_UNICODE /Fe:IisCertManager-Setup-win-x64.exe "%ROOT%\src\NativeSetup\Setup.cpp" setup.res /link /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT user32.lib shell32.lib advapi32.lib ole32.lib gdi32.lib comctl32.lib
if errorlevel 1 exit /b 1
popd
exit /b 0
