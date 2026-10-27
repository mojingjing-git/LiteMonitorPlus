# TM02–TM06 本地实施证据

日期：2026-10-27（Asia/Shanghai）
范围：主题市场投稿元数据、仓库主题生成器/CI、独立入口、真实应用动作、首批精选主题。
结论：本地代码与夹具通过；真实远端推送、发布包实机安装/应用/更新/卸载、远程 URL 可用性未验证，不能标记 TM07/TM08 完成。

## 结果

| 项目 | 结果 | 证据 |
|---|---|---|
| TM02 投稿元数据 | `submission.json` 含 id/name/name_en/description/description_en/market_version/license/author；主题格式 `version` 仍为整数；工作室增加市场投稿信息对话框（中文/英文标签 + 9 语言文案） | `tm02-tm04-tests.log`（ThemeExportServiceTests 17/17） |
| TM03 仓库生成器 | `scripts/gen_theme_index.py` 扫描 `themes/**`，严格 UTF-8、≤256 KiB、远程引用、route/material、SHA-256/size；重复运行 index 哈希一致；插件段保留 | `tm03-generator-check.log` |
| TM03 真实仓库契约 | 以 `LITEMONITOR_THEME_REPO=G:\TOOL\awesome-litemonitor-plugins` 跑 `ThemeMarketContractTests`：8/8 通过（schema=1、6 个主题、SHA/size/route/material） | `tm03-live-contract.log` |
| TM04 独立入口 | `SettingsForm` 两个市场入口共用单实例，`Save/Dispose` 按引用去重，`SwitchPage` 带 `MarketCategory` 意图；9 语言 `Menu.ThemeMarket` | `tm02-tm04-tests.log`（市场/导出/插件/语言 184/184） |
| TM05 应用动作 | 已安装主题增加“应用”；只改 Live `Skin`，走 `ApplyThemeAndLayoutWithResult`，检查 `ThemeRuntime`；卸载当前主题先切默认并检查结果 | 代码审查；行为/实机证据待补 |
| TM06 首批主题 | 6 个本地主题：2 GPU（cyberpunk-gpu、liquid-glass-gpu）+ 4 legacy；全部通过 `ThemeMarketValidator` | `tm06-validator.log` |

## 未完成/未验证

- TM05 的真实 UI 点击、安装→应用→重启→卸载闭环未执行。
- TM03/TM06 尚未提交/推送到 `mojingjing-git/awesome-litemonitor-plugins`；当前只有本地克隆改动。
- TM07 的真实远端 URL、发布包实机、更新与撤架流程未验证。
- TM08 的 RC 冻结、更新说明与运行维护未开始。
