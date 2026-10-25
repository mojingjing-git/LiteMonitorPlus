#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Fxc = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe"
)

# ★ 编译逻辑已收敛到 scripts/build-shaders.ps1（两个外观工具共用一份）。
#   本文件只负责"编译本工具自己的 Shaders 目录"。
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\..\scripts\build-shaders.ps1')

Build-MaterialShaders -ShaderDir (Join-Path $PSScriptRoot 'Shaders') -FxcPath $Fxc
