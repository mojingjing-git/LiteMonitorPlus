#requires -Version 7.0
<#
.SYNOPSIS
    独立复验发布签名：用客户端内置的**公钥**重新验证 resources/version.json，
    并（可选）核对 version.json 里的 sha256 与实际发布包一致。

.DESCRIPTION
    ★ 为什么要有这一步：
      scripts/sign-release.ps1 用私钥"自验"过一次，但那只证明签名自洽，
      不证明它能被**客户端内置的公钥**验过，也不证明"签的就是发的那个 ZIP"。
      一旦这里失配，失败现象只出现在用户那边（"更新源不可信"），发布端毫无感知。

      本脚本完全独立于 sign-release.ps1：
        1. 只从 src/System/UpdateChecker.cs 解析公钥常量（客户端信任的锚点）
        2. 从 version.json 读 version / sha256 / releaseDate / signature
        3. 按协议重建载荷 "<version>\n<sha256>\n<releaseDate>"（UTF-8 无 BOM、LF 分隔）
        4. 用公钥验签：ECDSA P-256 + SHA-256，签名格式 DER SEQUENCE（RFC 3279）
        5. 给了 -ZipPath 时，再核对 version.json 的 sha256 与 ZIP 实际摘要一致
      任一步不符即抛错，退出码非 0。

.PARAMETER RepoRoot
    仓库根目录。留空时按脚本自身位置推断（<root>/.github/workflows/…）。

.PARAMETER VersionJsonPath
    待复验的 version.json。默认 <RepoRoot>/resources/version.json。

.PARAMETER ZipPath
    实际发布包。留空则跳过"签的是不是发的"这一项核对。

.EXAMPLE
    pwsh -File .github/workflows/verify-release-signature.ps1 -ZipPath dist/LiteMonitorPlus_v1.0.0-win-x64.zip

.NOTES
    退出码：0 验签通过；1 复验失败；2 用法/IO 错误。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$VersionJsonPath,
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 统一的失败出口。
# ★ 不用 Write-Error：本脚本设了 $ErrorActionPreference='Stop'，
#   Write-Error 会变成终止性错误，后面的 exit N 根本执行不到，退出码会退化成 1。
#   这里改成"打印到 stderr + 显式 exit N"，保证退出码就是文档里写的那个。
function Stop-WithError {
    param(
        [string]$Message,
        [int]$Code = 1
    )
    Write-Host "::error::$Message"
    [Console]::Error.WriteLine($Message)
    exit $Code
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    # 脚本位于 <repo>/.github/workflows/ ⇒ 上两级是仓库根
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
if ([string]::IsNullOrWhiteSpace($VersionJsonPath)) {
    $VersionJsonPath = Join-Path $RepoRoot 'resources\version.json'
}

$checkerPath = Join-Path $RepoRoot 'src\System\UpdateChecker.cs'
if (-not (Test-Path -LiteralPath $checkerPath)) {
    Stop-WithError -Message "找不到 UpdateChecker.cs：$checkerPath" -Code 2
}
if (-not (Test-Path -LiteralPath $VersionJsonPath)) {
    Stop-WithError -Message "找不到 version.json：$VersionJsonPath" -Code 2
}

# ----------------------------------------------------------------------
# 1. 客户端内置公钥（唯一信任锚点）
# ----------------------------------------------------------------------
$checker = Get-Content -LiteralPath $checkerPath -Raw -Encoding UTF8
if ($checker -notmatch 'UpdateSigningPublicKeySpkiBase64\s*=\s*"([^"]+)"') {
    Stop-WithError -Message "无法从 $checkerPath 解析出内置公钥常量 UpdateSigningPublicKeySpkiBase64" -Code 2
}
$publicKeyB64 = $Matches[1]
Write-Host '== 签名独立复验 =='
Write-Host "内置公钥来源 : src/System/UpdateChecker.cs"
Write-Host "内置公钥     : $publicKeyB64"

# ----------------------------------------------------------------------
# 2. 被签内容
# ----------------------------------------------------------------------
$manifest = Get-Content -LiteralPath $VersionJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$version     = [string]$manifest.version
$sha256      = [string]$manifest.sha256
$releaseDate = [string]$manifest.releaseDate
$signature   = [string]$manifest.signature

Write-Host "version.json : $VersionJsonPath"
Write-Host "version      : $version"
Write-Host "releaseDate  : $releaseDate"
Write-Host "sha256       : $sha256"

if ([string]::IsNullOrWhiteSpace($version) -or
    [string]::IsNullOrWhiteSpace($sha256) -or
    [string]::IsNullOrWhiteSpace($releaseDate) -or
    [string]::IsNullOrWhiteSpace($signature)) {
    Stop-WithError -Message 'version.json 缺少 version / sha256 / releaseDate / signature 中的某一项 —— 拒绝复验。'
}
if ($sha256.Length -ne 64 -or ($sha256.ToCharArray() | Where-Object { -not [Uri]::IsHexDigit($_) })) {
    Stop-WithError -Message "sha256 形态不对（应为 64 位十六进制）：$sha256"
}

# ----------------------------------------------------------------------
# 3. 载荷与验签
# ----------------------------------------------------------------------
# 载荷格式与 UpdateChecker.BuildUpdateSignaturePayload / sign-release.ps1 严格一致：
#   "<version>\n<sha256>\n<releaseDate>"，UTF-8，无 BOM，LF 分隔。
$payload = "$version`n$sha256`n$releaseDate"
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

try {
    $signatureBytes = [Convert]::FromBase64String($signature)
}
catch {
    Stop-WithError -Message "signature 不是合法的 base64：$($_.Exception.Message)"
}

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$verified = $false
try {
    $ecdsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($publicKeyB64), [ref]$null)
    $verified = $ecdsa.VerifyData(
        $payloadBytes,
        $signatureBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
}
finally {
    $ecdsa.Dispose()
}

if (-not $verified) {
    Stop-WithError -Message '验签失败：version.json 的签名无法用客户端内置公钥验证 —— 拒绝发布。'
}
Write-Host '验签结果     : 通过（客户端内置公钥可验证）'

# ----------------------------------------------------------------------
# 4. "签的是不是发的"
# ----------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    if (-not (Test-Path -LiteralPath $ZipPath)) {
        Stop-WithError -Message "找不到发布包：$ZipPath" -Code 2
    }
    $actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    Write-Host "发布包       : $ZipPath"
    Write-Host "实际 SHA-256 : $actual"
    if ($actual -ne $sha256) {
        Stop-WithError -Message '发布包摘要与 version.json 里的 sha256 不一致 —— 签的不是发的，拒绝发布。'
    }
    Write-Host '摘要核对     : 通过（签的就是发的）'
}
else {
    Write-Host '摘要核对     : 跳过（未提供 -ZipPath）'
}

Write-Host '== 复验通过 =='
exit 0
