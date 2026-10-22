# 插件市场契约夹具（plugin-repo fixture）

本目录是 **`awesome-litemonitor-plugins` 插件仓库的一份最小本地副本**，
用来让客户端与插件仓库之间的契约测试在 CI 里**真正执行**，而不是被跳过。

对应缺陷：**ENV-003**（`MarketIndexContractTests` 的 6 条 `[PluginRepoFact]` 用例
从未真正跑过，CI 也就从未验证过"客户端模型 ↔ 插件仓库"的契约）。

## 怎么用

契约用例由环境变量 `LITEMONITOR_PLUGIN_REPO` 门控
（`tests/LiteMonitor.Tests/PluginRepoFactAttribute.cs:21`）：

```powershell
$env:LITEMONITOR_PLUGIN_REPO = 'G:\TOOL\LiteMonitor-fork\tests\fixtures\plugin-repo'
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --nologo `
  --filter "FullyQualifiedName~MarketIndexContractTests"
```

CI 里已经接好了（`.github/workflows/ci.yml` 的 `LITEMONITOR_PLUGIN_REPO` 环境变量），
不需要人工设置。

## 目录结构

```
plugin-repo/
├── .gitattributes              ← ★ 强制本目录内的 json 以 LF 落盘（见下方"行尾陷阱"）
├── README.md                   ← 本文件
├── index.json                  ← 插件仓库索引（契约的"被测方"）
├── plugins/
│   ├── market-echo/plugin.json ← 夹具插件描述 #1（索引条目 id = market-echo）
│   └── market-clock/plugin.json← 夹具插件描述 #2（索引条目 id = market-clock）
└── invalid-examples/           ← ★ 越界反例（故意不在 index.json 里）
    ├── README.md               ←   5 类反例的台账 + 对应判定代码行号
    ├── host-out-of-scope/plugin.json
    ├── path-prefix-out-of-scope/plugin.json
    ├── non-https/plugin.json
    ├── path-traversal/plugin.json
    └── userinfo/plugin.json
```

## 夹具的硬性契约

`index.json` 由客户端的 `MarketIndex` 模型反序列化
（`src/Plugins/PluginMarket.cs:96-103`，`PropertyNameCaseInsensitive = true`），
契约用例逐条断言（`tests/LiteMonitor.Tests/MarketIndexContractTests.cs`）：

| 字段 | 要求 | 断言位置 |
|---|---|---|
| `schema` | 必须等于 `1` | `:51` |
| `repo` | 必须逐字等于 `mojingjing-git/awesome-litemonitor-plugins` | `:52` |
| `plugins` | 非空数组 | `:53` |
| `total` | 必须等于 `plugins` 的长度 | `:54` |
| 每条 `plugins[]` | `id` / `name` / `name_en` / `description` / `description_en` / `author` / `version` / `download_url` / `readme_url` / `page_url` / `source` / `sha256` 全部非空，且 `size > 0` | `:57-71` |

其它被契约约束的字段：

- **`download_url`**：必须落在 `PluginMarket.TryResolveAllowedDownloadUri` 的白名单内
  （`src/Plugins/PluginMarket.cs:204-271`，白名单 `:160-165`）——
  scheme 必须是 `https`、端口必须是 443、无 userinfo、原始路径无 `..`/空段/编码分隔符、
  **Host 精确等于** `raw.githubusercontent.com`、
  **原始路径前缀精确等于** `/mojingjing-git/awesome-litemonitor-plugins/main/plugins/`。
- **`sha256` / `size`**：必须与 `plugins/<id>/plugin.json` 的**原始字节**一致
  （`:74-96`）——`sha256` 是小写十六进制的 SHA-256，`size` 是**字节数**（不是字符数；
  夹具里的 `plugin.json` 含中文，因此字符数与字节数不相等，正好能卡住这个口径）。
- **`id`**：必须匹配 `\A[A-Za-z0-9_-]{1,64}\z`（`PluginMarket.TryValidatePluginId`，
  `src/Plugins/PluginMarket.cs:411-430`），且不与内置插件 id 冲突
  （`PluginConstants.BuiltinPluginIds`，`src/Plugins/PluginConstants.cs:61-65`：
  Bilibili / Crypto / Exchange / GitHub / Hitokoto / Holiday / ProxyLatency /
  PublicIP / Stock / UniversalAPI / Weather / WeiboHot）。
  本夹具用 `market-echo` / `market-clock`，两个都不在内置名单里。
- **目录名 == `plugin.json` 的 `id` 字段**（`MarketIndexContractTests.cs:146-162`）。

## ★ 行尾陷阱（改动夹具前务必先读）

本仓库 `core.autocrlf=true` 且**仓库根目录没有 `.gitattributes`**，
所以 `.json` 文件在 Windows 检出时会被转换成 CRLF，
`File.ReadAllBytes` 读到的字节就与仓库 blob 不一致 → `sha256` / `size` 断言必然失败。

**解法**就是本目录下的 `.gitattributes`：

```
*.json text eol=lf
```

Git 支持**子目录局部生效**的 `.gitattributes`，这条规则只作用于
`tests/fixtures/plugin-repo/` 之内，不影响仓库其它任何文件。
`eol=lf` 让 Windows runner 与 Linux runner 检出得到**完全相同的字节**，跨平台可复现。

> ⇒ **改了夹具里的 `plugin.json` 之后，必须重算 `index.json` 里的 `sha256` 与 `size`。**
> 正确写法：`sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()`、
> `size = raw.Length`（`raw` 是文件的原始字节）。

## 越界反例在哪里

**不在 `index.json` 里**，在 `invalid-examples/` 下，原因见该目录的 README：
契约用例 `索引里的下载地址_都落在允许的源范围内`（`:98-112`）会遍历 `index.json` 里
**每一条**并断言全部通过，反例只要混进 `index.json` 就会让该用例必然失败。

## 本夹具不含任何真实凭据

`plugin.json` 里的 `execution.url` 指向本仓库的 `raw.githubusercontent.com` 路径，
不含 token / key / 口令；夹具也不会发起任何网络请求（契约用例只读本地文件）。
