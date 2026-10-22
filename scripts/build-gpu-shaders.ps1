#requires -Version 7.0
[CmdletBinding()]
param([string]$Fxc = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe")
$ErrorActionPreference = 'Stop'
$shaderRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'resources/shaders'
if (-not (Test-Path -LiteralPath $Fxc -PathType Leaf)) { throw "Shader compiler missing: $Fxc" }
foreach ($entry in @(@('VSMain','vs_5_0','SceneMaterialVS.cso'), @('PSMain','ps_5_0','SceneMaterialPS.cso'))) {
    & $Fxc /nologo /T $entry[1] /E $entry[0] /Fo (Join-Path $shaderRoot $entry[2]) (Join-Path $shaderRoot 'SceneMaterial.hlsl')
    if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed: $($entry[0])" }
}
Get-FileHash -LiteralPath (Join-Path $shaderRoot 'SceneMaterial.hlsl'), (Join-Path $shaderRoot 'ToolbarGlass.hlsli'), (Join-Path $shaderRoot 'SceneMaterialVS.cso'), (Join-Path $shaderRoot 'SceneMaterialPS.cso')
