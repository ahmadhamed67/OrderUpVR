# Builds the Order Up VR Vulkan layer into ..\bin using MSVC Build Tools.
param([ValidateSet('Release','Debug','RelWithDebInfo')][string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'build'

if (-not $env:VULKAN_SDK) {
    $sdk = Get-ChildItem C:\VulkanSDK -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($sdk) { $env:VULKAN_SDK = $sdk.FullName } else { throw 'Vulkan SDK not found (set VULKAN_SDK).' }
}

cmake -S (Join-Path $root 'layer') -B $build -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'CMake configure failed' }
cmake --build $build --config $Config
if ($LASTEXITCODE) { throw 'Build failed' }
Write-Host "Layer built: $(Join-Path $root 'bin\VkLayer_OrderUpVR.dll')"
