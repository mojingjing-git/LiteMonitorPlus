#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Fxc = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = Join-Path $root 'Shaders\Material.hlsl'
$outDir = Join-Path $root 'Shaders'

if (-not (Test-Path -LiteralPath $Fxc -PathType Leaf)) {
    throw "fxc.exe not found: $Fxc"
}

$vs = Join-Path $outDir 'MaterialVS.cso'
$ps = Join-Path $outDir 'MaterialPS.cso'

& $Fxc /nologo /T vs_5_0 /E VSMain /Fo $vs $source
if ($LASTEXITCODE -ne 0) { throw "Vertex shader compilation failed ($LASTEXITCODE)." }

& $Fxc /nologo /T ps_5_0 /E PSMain /Fo $ps $source
if ($LASTEXITCODE -ne 0) { throw "Pixel shader compilation failed ($LASTEXITCODE)." }

Get-FileHash -LiteralPath $source, $vs, $ps -Algorithm SHA256 |
    Select-Object Hash, Path |
    Format-Table -AutoSize
