@echo off
rem Builds rpcs3-src (Release x64) with MSVC Build Tools. Log: build-rpcs3.log in the project root.
setlocal
set ROOT=%~dp0..
set QTDIR=%ROOT%\deps\Qt
set Qt6_ROOT=%ROOT%\deps\Qt
if not defined VULKAN_SDK set VULKAN_SDK=C:\VulkanSDK\1.4.363.0
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 || exit /b 1
cd /d "%ROOT%\rpcs3-src"
msbuild rpcs3.sln /t:rpcs3 /m /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 > "%ROOT%\build-rpcs3.log" 2>&1
echo MSBUILD_EXIT=%ERRORLEVEL% >> "%ROOT%\build-rpcs3.log"
exit /b %ERRORLEVEL%
