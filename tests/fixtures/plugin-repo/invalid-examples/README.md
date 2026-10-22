# 越界反例台账（invalid-examples）

本目录存放**必须被客户端拒绝**的插件市场下载地址样本。

> ★ 这些反例**故意不在** `index.json` 的 `plugins` 数组里。
> 契约用例 `索引里的下载地址_都落在允许的源范围内`
> （`tests/LiteMonitor.Tests/MarketIndexContractTests.cs:98-112`）会遍历 `index.json`
> 里**每一条**并断言全部落在允许的源范围内 —— 只要有一条反例混进 `index.json`，
> 该用例就必然失败。因此反例只能作为**独立文件**存放在这里。
>
> 每个 `<case>/plugin.json` 的内容是**索引条目形状**的记录（带 `download_url`），
> 而不是可安装的插件模板（`PluginTemplate`）。它的用途是记录一个"必须被拒绝"的地址；
> 真要验证拒绝行为，把它拼进一份 `index.json` 即可，`PluginMarket.IsAllowedDownloadUrl`
> 应当返回 `false`。

## 判定依据

全部判定都来自 `src/Plugins/PluginMarket.cs` 的
`TryResolveAllowedDownloadUri`（`:204-271`），白名单定义在 `AllowedDownloadSources`（`:160-165`）：

| 白名单项 | 值 |
|---|---|
| Host（精确比对） | `raw.githubusercontent.com` |
| 原始路径前缀（精确比对） | `/mojingjing-git/awesome-litemonitor-plugins/main/plugins/` |

## 逐条台账

| # | 目录 | download_url | 期望判定 | 期望拒绝理由 | 对应代码行 |
|---|---|---|---|---|---|
| 1 | `host-out-of-scope/` | `https://evil.example.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x/plugin.json` | **拒绝** | 主机 `evil.example.com` 不在白名单内（Host 精确比对失败） | `PluginMarket.cs:249`（Host 比对）、`:258-262`（hostMatched=false 出口） |
| 2 | `path-prefix-out-of-scope/` | `https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins-evil/x/plugin.json` | **拒绝** | 原始路径不以 `/mojingjing-git/awesome-litemonitor-plugins/main/plugins/` 开头（`plugins-evil` ≠ `plugins/`） | `PluginMarket.cs:251`（路径前缀比对）、`:263-267`（pathMatched=false 出口） |
| 3 | `non-https/` | `http://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x/plugin.json` | **拒绝** | 只允许 HTTPS | `PluginMarket.cs:221-225` |
| 4 | `path-traversal/` | `https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/../evil/plugin.json` | **拒绝** | 路径含穿越段（`..`）—— 用**未经 Uri 规范化**的原始路径判定 | `PluginMarket.cs:242-243`（取原始路径）、`PluginMarket.cs:315`（`..` 段判定） |
| 5 | `userinfo/` | `https://u:p@raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x/plugin.json` | **拒绝** | 地址不得包含 userinfo（`user:password@`） | `PluginMarket.cs:228-232` |

## 补充说明

- 这 5 类只是白名单校验中**最容易被绕过**的几类。`TryValidateRawPath`（`:291-326`）
  还会拒绝：编码分隔符 `%2f` / `%5c`（`:298-302`）、空段（`:308`）、解码后含分隔符（`:316-320`）、
  二次编码 `%25xx`（`:322`）、反斜杠（`:212`）、非 443 端口（`:234-238`）。
- 这些反例**目前没有自动化用例覆盖**（xunit 测试工程不在 S4.1 的文件所有权范围内，
  夹具不能自己加用例）。详见 S4.1 交付报告中的"未闭环缺口"一节。
