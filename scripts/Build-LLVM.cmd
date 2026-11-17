@echo off
rem Builds LLVM for RPCS3 with the local MSVC toolset (the prebuilt llvmlibs need a newer STL than 14.44).
rem Output: rpcs3-src\build\lib\Release-x64\llvm_build. Log: build-llvm.log in the project root.
setlocal
set ROOT=%~dp0..
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 || exit /b 1
cd /d "%ROOT%\rpcs3-src"
msbuild 3rdparty\llvm\llvm_build.vcxproj /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:SolutionDir=%CD%\\" > "%ROOT%\build-llvm.log" 2>&1
echo MSBUILD_EXIT=%ERRORLEVEL% >> "%ROOT%\build-llvm.log"
exit /b %ERRORLEVEL%
