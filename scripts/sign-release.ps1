#requires -Version 7.0
<#
.SYNOPSIS
    给 resources/version.json 签名，并回写发布包（release ZIP）的 SHA-256。

.DESCRIPTION
    这是更新链路里唯一的"信任产生点"：客户端只验签、不信任 URL。
    因此本脚本必须与 UpdateChecker.cs 的载荷定义逐字节一致。

    载荷（UTF-8，无 BOM，单个 LF 分隔，三行）：
        <version>
        <sha256>
        <releaseDate>

    刻意不签 changelog（含中文与换行，规范化极易漂移）、不签整段 JSON
    （序列化顺序/缩进/行尾任何一处变化都会让签名失效）。

    签名算法：ECDSA P-256 + SHA-256，签名格式为 DER SEQUENCE（RFC 3279），
    对应客户端的 DSASignatureFormat.Rfc3279DerSequence。

.PARAMETER ZipPath
    要发布的 ZIP。摘要由本脚本计算，不接受外部传入——避免"签的不是发的"。

.PARAMETER Version
    版本号（如 1.0.1），必须与 ZIP 文件名和 release tag 一致。

.PARAMETER ReleaseDate
    发布日期（如 2026-10-21）。它是被签字段，改动会导致验签失败。

.PARAMETER KeyPath
    PKCS#8 私钥（base64）。默认 <repo>/secrets/update-signing-key.pkcs8。
    私钥绝不进版本库；.gitignore 已排除 secrets/、*.pkcs8、*.pfx。

.PARAMETER VersionJsonPath
    默认 <repo>/resources/version.json。

.PARAMETER DryRun
    只计算与验签，不写回 version.json。

.EXAMPLE
    pwsh scripts/sign-release.ps1 -ZipPath dist/LiteMonitorPlus_v1.0.1-win-x64.zip -Version 1.0.1 -ReleaseDate 2026-10-21

.NOTES
    退出码：0 成功；1 前置条件失败（缺文件、私钥不匹配、自验失败）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ReleaseDate,
    [string]$KeyPath,
    [string]$VersionJsonPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $KeyPath) { $KeyPath = Join-Path $repoRoot 'secrets\update-signing-key.pkcs8' }
if (-not $VersionJsonPath) { $VersionJsonPath = Join-Path $repoRoot 'resources\version.json' }

if (-not (Test-Path -LiteralPath $ZipPath)) { throw "找不到发布包：$ZipPath" }
if (-not (Test-Path -LiteralPath $KeyPath)) {
    throw "找不到签名私钥：$KeyPath`n私钥不进版本库。若已丢失，只能用新密钥重新发布一次客户端（旧客户端会验不过新签名）。"
}
if (-not (Test-Path -LiteralPath $VersionJsonPath)) { throw "找不到 version.json：$VersionJsonPath" }

# 1. 计算发布包摘要：大写十六进制，与 UpdateChecker.ComputeFileSha256 的输出格式一致
$sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
Write-Host "ZIP       : $ZipPath"
Write-Host "SHA-256   : $sha256"

$signature = $null

# 2. 载入私钥并完成自检
$keyBytes = [Convert]::FromBase64String((Get-Content -LiteralPath $KeyPath -Raw).Trim())
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportPkcs8PrivateKey($keyBytes, [ref]$null)

    # ★ 自检一：私钥必须对应客户端内置的公钥。
    #   不检查这一步，用错密钥签出来的 version.json 会让**所有**客户端验签失败，
    #   而失败现象只出现在用户那边（"更新源不可信"），发布端毫无感知。
    $checkerPath = Join-Path $repoRoot 'src\System\UpdateChecker.cs'
    $checker = Get-Content -LiteralPath $checkerPath -Raw
    if ($checker -notmatch 'UpdateSigningPublicKeySpkiBase64\s*=\s*"([^"]+)"') {
        throw "无法从 $checkerPath 解析出内置公钥常量 UpdateSigningPublicKeySpkiBase64"
    }
    $builtInPublicKey = $Matches[1]
    $actualPublicKey = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    if ($actualPublicKey -ne $builtInPublicKey) {
        throw ("私钥与客户端内置公钥不匹配，签出来的更新会让所有客户端验签失败：`n" +
               "  内置公钥：$builtInPublicKey`n" +
               "  本次私钥：$actualPublicKey")
    }
    Write-Host "公钥自检  : 通过（与 UpdateChecker.cs 内置公钥一致）"

    # 3. 构造载荷 —— 三行、LF 分隔、UTF-8 无 BOM
    $payload = "$Version`n$sha256`n$ReleaseDate"
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

    # 4. 签名（DER SEQUENCE）
    $sigAlgorithm = [System.Security.Cryptography.HashAlgorithmName]::SHA256
    $sigFormat = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    $sigBytes = $ecdsa.SignData($payloadBytes, $sigAlgorithm, $sigFormat)

    # ★ 自检二：立刻用同一载荷验一次。
    #   这一步抓的是"签名能生成但客户端验不过"这类低级错误（格式/算法选错）。
    if (-not $ecdsa.VerifyData($payloadBytes, $sigBytes, $sigAlgorithm, $sigFormat)) {
        throw "自验失败：刚生成的签名无法通过验证，拒绝写出"
    }
    Write-Host "签名自验  : 通过"

    $signature = [Convert]::ToBase64String($sigBytes)
    Write-Host "Signat.   : $signature"
}
finally {
    $ecdsa.Dispose()
}

# 5. 回写 version.json。只替换这 4 个键的字符串值，其余内容（changelog、缩进、行尾）原样保留。
#    ★ 要求模板里已有 sha256 / signature 两个占位键：不要在这里做"缺键就插入"，
#      那需要处理缩进与逗号，容易把 JSON 改成客户端解析不了的样子。
$json = [IO.File]::ReadAllText($VersionJsonPath)

$fields = @(
    @{ Name = 'version';     Value = $Version },
    @{ Name = 'releaseDate'; Value = $ReleaseDate },
    @{ Name = 'sha256';      Value = $sha256 },
    @{ Name = 'signature';   Value = $signature }
)

foreach ($field in $fields) {
    $pattern = '"' + [regex]::Escape($field.Name) + '"\s*:\s*"[^"]*"'
    if (-not [regex]::IsMatch($json, $pattern)) {
        throw "version.json 缺少 `"$($field.Name)`" 键。模板必须保留 sha256 与 signature 两个占位键。"
    }
    $literal = '"' + $field.Name + '": "' + $field.Value + '"'
    $json = [regex]::Replace($json, $pattern, $literal.Replace('$', '$$'), 1)
}

if ($DryRun) {
    Write-Host ""
    Write-Host "DryRun：以下内容**未**写入 $VersionJsonPath"
    Write-Host $json
    exit 0
}

# 保留原文件行尾：先探测，再按原样写回（不引入 BOM）
$encoding = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($VersionJsonPath, $json, $encoding)

Write-Host ""
Write-Host "已写入    : $VersionJsonPath"
Write-Host "下一步    : 提交 resources/version.json 并上传 $([IO.Path]::GetFileName($ZipPath)) 到 release v$Version"
Write-Host ""
Write-Host "提示：客户端只在版本号**大于**当前版本时才会提示更新。"
exit 0
