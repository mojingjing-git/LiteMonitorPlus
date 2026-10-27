# TM00 基线（主题市场首期）

- 采集时间：2026-10-27（Asia/Shanghai）
- 客户端仓库：`G:\TOOL\LiteMonitor-fork`
- 客户端 HEAD：`4de25d38add7f8b2342303805fdf0b654c57fa55`（`master`）
- 客户端版本：`1.0.0`；目标框架 `net8.0-windows`
- 工作区：存在本批修复/产品能力的未提交改动；主题市场实施不得回退、覆盖或混入这些改动。开工前以 `git status --short` 记录的实际清单为准。
- 远端仓库（只读克隆）：`G:\TOOL\awesome-litemonitor-plugins`
- 远端 HEAD：`a1c2f5c6174f8ddd89e0f7735c872e493df8f8a6`（`chore(index): 自动重新生成插件索引`）
- 远端工作区：干净
- 远端索引：`schema=1`、`total=13`、无 `themes`/`themes_total` 段；`updated_at=2026-10-19T22:42:43Z`
- 远端生成器：`scripts/gen_index.py`，当前只扫描 `plugins/`，生成 `index.json` 与 `PLUGINS.md`
- 远端 CI：`.github/workflows/gen-index.yml`，触发路径仅 `plugins/**`、`scripts/**`；PR 只读校验，main 分支自动提交索引
- 权限基线：根 `app.manifest` 的 `requestedExecutionLevel` 为 `requireAdministrator`，由 `LiteMonitor.csproj` 的 `ApplicationManifest` 引用
- 首期边界：4–6 个精选主题，至少 2 个 legacy + 2 个 GPU；沿用 `AppContext.BaseDirectory/resources/themes/{id}.json`；不新增用户主题目录、账号、上传 API 或远程预览图通道
- 远端写入：本计划只允许在本地克隆生成候选内容与索引；推送、合并、发布必须由维护者执行，本文不宣称远端已发布

## 已核对的风险

1. 远端生成器的 `updated_at` 取当前时间，生成结果不确定；TM03 必须改为可复现来源。
2. `plugins/**`/`scripts/**` 触发路径不覆盖 `themes/**`、`reviews/**`；TM03 必须扩展。
3. `LanguageManager.GetOriginal` 只回退 key 后缀，没有英文底表；新增主题市场文案不能只补 zh/en 后假设其他语言会自动显示英文。
4. 同一 `PluginMarketPage` 注册到两个导航 key 时，`SettingsForm.ApplySettings()` 会重复 `Save()`，`DisposeCachedPages()` 会重复 `Dispose()`；TM04 必须按引用去重。
5. 现有 `ThemeMarket.ValidateThemeContent` 使用默认 UTF-8 替换解码和原始文本 `http(s)://` 检查；TM01 必须补严格解码、解码后引用检查、256 KiB 上限与声明大小一致。
