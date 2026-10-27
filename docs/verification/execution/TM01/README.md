# TM01 共享校验与 CLI

日期：2026-10-27  
状态：已通过（客户端/CLI 同例同判据）  
计划：`docs/plans/2026-10-27-theme-market-launch-plan.md`

## 交付

## 客户端

- 新增 `src/Plugins/ThemeMarketValidator.cs`：
  - 严格 UTF-8 解码（`UTF8Encoding(false, true)`），拒绝非法字节，不再以替换字符“伪解码”；
  - 单一 JSON 对象、无注释/尾逗号；
  - 在**解码后的属性名和字符串值**上检查 `http://` / `https://`，覆盖 `\/` 转义；
  - 载荷 1–262144 字节（256 KiB）；
  - 索引声明大小必须等于实际载荷长度；
  - `route` / `material` 与 `rendering.backend` / `rendering.material` 互验；
  - `rendering` 存在时复用 `ThemeRenderingConfig.Validate()` 全规则集；缺失时按 legacy 解释；
  - 稳定错误码（`theme_utf8_invalid`、`theme_remote_reference`、`theme_too_large`、`theme_size_mismatch`、`theme_route_mismatch` 等）。
- `src/Plugins/ThemeMarket.cs`：`ValidateThemeContent` 改为该校验器的薄适配，保留 `ThemeContentRejected` 异常合同。
- `src/Plugins/PluginMarket.cs`：下载读取增加可选 `maxBytes` 重载；主题下载按 256 KiB 实际截断，不再先读到 8 MiB 才拒绝。
- `src/Rendering/Configuration/ThemeRenderingConfig.cs`：CLI 编译符号 `THEME_MARKET_CLI` 下跳过仅用于诊断的 `fallbackStyle` 解析；错误列表规则不变。

## 仓库 CLI

- `tools/ThemeMarketValidator/ThemeMarketValidator.csproj`（net8.0）
- `tools/ThemeMarketValidator/Program.cs`
- 通过 `<Compile Include="..\..\src\Plugins\ThemeMarketValidator.cs" Link="..." />` 链接客户端同一份源码，**没有复制规则**；同样链接 `ThemeRenderingConfig.cs`。
- 用法：
  ```text
  dotnet run --project tools/ThemeMarketValidator -- <theme.json> <route> <material> [declaredSize]
  ```
  退出码 `0` 通过、`1` 拒收、`2` 参数错误；拒收输出稳定错误码。

## 验证

| 检查 | 结果 | 日志 |
|---|---|---|
| ThemeMarket 定向（含 7 个新增正反例） | 21/21 通过 | `tm01-tests.log` |
| CLI 构建 | 0 warnings / 0 errors | `tm01-cli-build.log` |
| 合法 GPU 主题 | `ok`，exit 0 | `tm01-cli-valid.log` |
| route 不一致 | `theme_route_mismatch`，exit 1 | `tm01-cli-invalid.log` |
| 客户端 Release 构建 | 0 warnings / 0 errors | `tm01-client-build.log` |

新增测试覆盖：非法 UTF-8、`\/` 转义远程引用、超 256 KiB、声明大小不一致、合法 GPU、无 rendering 的 legacy、route 不一致。

## 未覆盖 / 边界

- 远端仓库的生成器、CI 触发路径、审核记录绑定尚未修改（TM03/TM06）。
- 当前 CLI 只做单文件校验；索引级校验仍由 TM03 生成器承担。
- `THEME_MARKET_CLI` 只影响可选 `fallbackStyle` 诊断，不影响市场接受/拒收的错误列表；该边界需在 TM03 评审中保持。
