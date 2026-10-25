#requires -Version 7.0
<#
    LiteMonitor++ —— 编译外观材质的 HLSL 着色器（共享实现）。

    ★ 为什么放在 scripts/ 而不是每个工具目录各留一份：
      tools\AppearanceMaterialLab 与 tools\GlassToolbarPreview 各自带一套
      Material.hlsl（内容不同，不能共用产物），但"把本目录的 Material.hlsl
      编译成 MaterialVS.cso + MaterialPS.cso 并打印哈希"这段逻辑完全相同。
      复制两份的后果是：改编译参数时只改一处 ⇒ 两个工具的着色器悄悄用了
      不同参数。故把逻辑收敛到这里，工具目录只留薄封装。

    用法（直接调用）：
      pwsh -File scripts/build-shaders.ps1 -ShaderDir tools/AppearanceMaterialLab
#>

function Build-MaterialShaders {
    <#
        把 $ShaderDir 下的 Material.hlsl 编译成 MaterialVS.cso / MaterialPS.cso。
        @param ShaderDir    着色器所在目录（应含 Material.hlsl）。
        @param FxcPath      fxc.exe 路径；不给则用 Windows SDK 的默认安装位置。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ShaderDir,
        [string]$FxcPath
    )

    if (-not $FxcPath) {
        $FxcPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe'
    }

    $source = Join-Path $ShaderDir 'Material.hlsl'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "找不到着色器源文件: $source"
    }
    if (-not (Test-Path -LiteralPath $FxcPath -PathType Leaf)) {
        throw "fxc.exe not found: $FxcPath"
    }

    $vs = Join-Path $ShaderDir 'MaterialVS.cso'
    $ps = Join-Path $ShaderDir 'MaterialPS.cso'

    & $FxcPath /nologo /T vs_5_0 /E VSMain /Fo $vs $source
    if ($LASTEXITCODE -ne 0) { throw "Vertex shader compilation failed ($LASTEXITCODE)." }

    & $FxcPath /nologo /T ps_5_0 /E PSMain /Fo $ps $source
    if ($LASTEXITCODE -ne 0) { throw "Pixel shader compilation failed ($LASTEXITCODE)." }

    Get-FileHash -LiteralPath $source, $vs, $ps -Algorithm SHA256 |
        Select-Object Hash, Path |
        Format-Table -AutoSize
}
