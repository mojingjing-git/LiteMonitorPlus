# R4 证据报告（F10 凭据头与动态主机授权）

- 日期：2026-10-27（Asia/Shanghai）
- 计划：`docs/plans/2026-10-27-repair-and-product-execution-plan.md`（v2.1）
- 范围：`X-API-Token` 真实凭据头分类；用户主动授权的动态 origin；未授权目标不发送凭据。
- 结论：**核心授权路径、设置页授权入口、回环重定向逐跳、IDN 规范化与实例删除清理已实现；真实代理/DNS 场景仍缺，R4 保持未关闭。**

## 1. 改动

| 落点 | 处理 |
|---|---|
| `src/Plugins/PluginProcessor.cs` | `CredentialHeaderNames` 增加 `x-api-token`；`GetDeclaredHosts` 增加可选实例 ID，合并该实例的用户授权 origin；`EnsureCredentialTargetAllowed` 先按完整 origin 精确匹配，再按旧字面量主机匹配 |
| `src/Plugins/DynamicHostAuthorizer.cs` | 新增授权存储；仅 `AuthorizeForUser` 可写；只接受 HTTPS（回环 HTTP 仅供本地测试）；规范化 IDN/端口；拒绝通配符；授权写入失败则不发送凭据；支持撤销与跨实例读取 |
| `src/Plugins/PluginExecutor.cs` | 实例执行入口与步骤入口把 `inst.Id` 传入 `GetDeclaredHosts`，授权按实例隔离 |
| `src/UI/Settings/PluginPage.cs` | 每个目标增加“授权此主机”入口：显示目标 origin、二次确认、只允许 HTTPS、结果可见；写入同一授权存储 |

授权文件默认路径：`%LOCALAPPDATA%\LiteMonitor\plugin-host-authorizations.json`。记录只含实例 ID、origin、授权时间；不含令牌。

## 2. 验证

```powershell
dotnet build LiteMonitor.csproj -c Release --no-incremental --nologo `
  -p:TreatWarningsAsErrors=true `
  -p:WarningsNotAsErrors=CS0618%3BCS0612%3BCS0619%3BCS0067%3BCS0168%3BCS0219 `
  -p:NuGetAudit=false
# exit 0；0 warnings，0 errors

dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~DynamicHostAuthorizationTests"
# 8/8 通过
```

覆盖：`X-API-Token` 分类、用户授权精确 origin、端口不可复用、实例隔离、非 HTTPS 非回环拒绝、跨实例持久化、重复授权幂等、带凭据请求 origin 匹配、端口不符拒绝、回环重定向逐跳凭据、IDN 规范化、实例删除清理。

## 3. 未覆盖（R4 关闭前必须补）

- 设置页授权入口已接入；令牌/密钥类目标字段使用密码字符掩码。
- 本地回环服务器端到端已覆盖：未授权跳不发送、两跳授权逐跳发送、IDN 规范化；HTTP 降级到非回环仍由 `TryNormalizeOrigin` 拒绝。
- 删除实例时 `PluginManager.RemoveInstance` 调用 `DynamicHostAuthorizer.RevokeAll` 清理授权。
- 真实代理/DNS/企业网络场景未验证。
- `GetDeclaredHosts` 的默认授权存储不可注入，端到端测试通过 `PluginExecutor` 注入允许集合绕开默认存储。
- `resources/plugins/vps_traffic.json` 仍是未跟踪本地模板，未提交；F10 回归夹具未固化。
