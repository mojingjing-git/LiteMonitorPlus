# 插件实现独立复审：F03 / F05

审查日期：2026-10-24（Asia/Shanghai）。工作树：`C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`；基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。

**最终有效结论：PASS。** 已收到 B 第二次 ready，完成 P2-PLUG-01 补丁及授权扩展范围的独立定向复核；该阻塞已关闭，限定范围内没有剩余已证实的 P1 / P2。其他已读边界结论保留，没有重新审查全项目。下文初审发现及旧行号保留为历史证据，最终轮的依据和冻结哈希见文末。

依据：v3 计划 §6 / §8、R1-B02 最新 PASS、R2-P2-01 / R2-P2-02 最新 PASS。审查仅涉及 PluginManager、PluginExecutor、PluginProcessor、Native/CryptoNative、新增 PluginHostPolicy / PluginPublicationLease / PluginRuntimeSnapshot 及两个 ReviewPlugin 回归测试；必要依赖仅用于追踪调用。没有复审其他组的市场、GPU、指标或 T09 实现。

## P2-PLUG-01：点分实例 ID 误删（初审发现，最终已关闭）

本节反例及行号属于补丁前快照；不代表最终冻结实现仍有该问题。

定位：`src/Plugins/PluginManager.cs:211–216`，尤其第 215 行；热更新调用点第 256–257 行；卸载调用点第 176–178 行。依赖中的误删条件位于 `PluginMonitorSyncService.cs:145` / `:166`。

`instanceId + "."` 只能区分 A / AB，无法区分 A / A.B。点分 ID 是生产可接受输入：`PluginModels.cs:13` 明确举例 `com.weather`，`LoadPlugins()` 第 186–188 行也直接以模板 ID 创建实例 ID，没有禁止点号。该问题并非需要非法配置或强行修改内部字段才可触发。

确定性反例：

1. 目录中有两个有效模板，ID 分别为 `A` / `A.B`，均启用、各有输出 `value`，先调用真实 Load / Start 并让两者发布一次。
2. 只改变 A 的模板 URL（版本号不变），再调用 `LoadPlugins()`。
3. A 的退役执行 `RemoveDataByPrefix("A.")`，直接删除邻居的 `A.B.value`、`.Color`、`.Unit`；标签前缀 `PROP.Label.DASH.A.` / `PROP.ShortLabel.DASH.A.` 同样删除 A.B 的标签。
4. 紧接着 `SyncMonitorItem(A, ...)` 的孤儿清理又将 `DASH.A.B.value` 判为 A 的孤儿并删除。A.B 的指纹未变，Reconcile 第 249–251 行跳过它，所以不会重建这些监控项。在正常可见性判断下，A.B 甚至可能因监控项消失而不再抓取。
5. 删除 A 的模板再 Load 时，`RemoveMonitorItems("A")` 使用相同前缀，同样影响 A.B。

最小修复方向：基于实例实际拥有的完整键清理／同步，或在门内维护明确所有权并保护其他实例的命名空间；不要仅再添加一个点号。需要同一所有权规则覆盖公开数据、标签／单位和 MonitorItems，而非只修步骤缓存。可将现有 A / AB 测试扩展为 A / A.B，并同时断言邻居值、属性、监控项、缓存和原计时器不变。现有第 279 行的 A / AB 用例无法暴露本例。

证据性质：生产调用链及确定的字符串匹配条件；没有运行项目、编译测试或宣称该反例已经由测试执行验证。

## 网络重置、资源释放与锁序核对

主控特别要求核对 Tick 第三次失败分支内的 `ResetNetworkClients()`，不能只凭 NetworkResetObserver 位于门外判为安全。

- Manager 第 496–516 行把失败计数、重置决策及缓存清理放在同一个 lease 提交内，退役用同一 `_scheduleLock` 排序。门内已提交的重置先于后续退役；已退役旧执行无法再进入该分支。
- 调用链的锁序为 `_scheduleLock -> PluginExecutor._httpLock -> CityCodeResolver._lock / CryptoNative._clientLock`；两个 native 锁依次获取，不互相嵌套。InitializeDefaultClient 再取得同一 `_httpLock` 是同线程重入。
- `PluginExecutor.cs:155–194` 门内只检查冷却时间、快照旧引用、增加客户端代次、构造／替换默认客户端、清代理引用以及调用两个 native reset；没有 CTS Cancel、HttpClient Dispose、网络 await 或同步任务等待。
- CityCodeResolver 第 138–143 行的 reset 只在短锁内将 `_db` 置空。CryptoNative 第 63–76 行只在 `_clientLock` 内换引用，旧客户端在 `Task.Run` 内延迟 30 秒后 Dispose。
- 默认／代理旧客户端的 Dispose 位于 `PluginExecutor.cs:184–193` 的后台任务中，同样先延迟 30 秒；没有把此任务同步等待回发布门。HttpClient.Dispose 可能同步取消请求并触发取消回调，但这些释放点不在 Manager 的发布／生命周期锁内，且只持有已经分离的旧客户端引用。
- 反向路径已查：默认客户端获取／代次读取只短暂持 `_httpLock`；native GetClient 只短暂持 `_clientLock`，SendAsync 和 CommitNative 均在释放该锁后执行；没有发现持这些锁反向取得 `_scheduleLock` 的生产路径。NetworkResetObserver 第 536 行在门外，仅观察已提交重置。

结论：**当前网络重置调用链本身没有已证实的 P1 / P2 锁序、同步取消或门内等待问题**。判断依据是完整调用链，不是仅观察 observer 的位置。若 B 后续改变 reset / dispose 路径，最终轮需重新核对。

## 当前已核对的其他实现要点

- 模板及实例配置进入内容指纹，字典键排序，模板同版本内容变化也能更换 runtime；运行快照复制实例输入和目标，旧任务不读新模板内容。Load / Reload / Restart 的正常入口遵守 `_running`，Stop 后 Load 不自动复活。
- lease 的有效性复核和共享写入在同一 gate 内；退役先撤销 lease，再清实例 KeyCache、步骤缓存、失败计数和公开数据，门外才停止／释放计时器和 Cancel / Dispose CTS。新计时器先创建，安装／默认值／Start 同门内提交；工厂被暂停时 Stop 可以先退役 reservation。
- 执行器输出、api_text、错误、KeyCache、缓存过期删除、缓存淘汰和写入及 Manager 失败收尾／重装均接入提交协议；BeforePublicationAsync / BeforeScheduleCommitAsync 屏障在门外。OnSchemaChanged 仍无生产触发点，本次没有新增通知路径，不把不存在的事件死锁当作发现。
- 共享 HTTP 用 CancellationToken.None 启动，等待者独立监听自身 token；退役不移除或取消共享 HTTP，完成续体使用键和值匹配删除在途项。
- 主机 helper 先验证原始完整 authority，再规范主机；拒绝动态 authority、双 @、非法端口、破损方括号和数字／编码 scope；普通静态 host 的动态 path / query 仍可声明。请求路径先执行原始目标校验，再进入 RequestIdentity、缓存和在途复用；HTTP / native 重定向检查 OriginalString，native fallback 在原生全局缓存前校验。
- 现有公开执行入口、ExecuteStepAsync / ExecuteStepWithScopeAsync / ExecuteInstanceWithScopeAsync 及 HostInAllowedSet / IsHostAllowed 等实际反射接缝仍保留。

## 验证边界与协调记录

本次只读生产／测试；唯一写入文件是本报告。未运行 `dotnet build` / `dotnet test` / `dotnet run`，未启动产品、未联网；仅对 URI 和字符串使用现有 PowerShell 运行时做无网络边界解析。新增测试的实际通过情况由主控在全员 ready、冻结后串行运行提供。

主控提供的验证状态：209 项定向回归已通过；这是主控反馈，非本审查者运行结果，也不代表尚待修复的 P2-PLUG-01 已关闭。主控将在补丁后执行进一步测试。

定向扩展范围（主控已授权）：`src/Plugins/PluginMonitorSyncService.cs`、实际键归属共用 helper，以及 `src/System/InfoService/InfoService.cs` 的内部精确键删除入口。验收要求 Manager 与 MonitorSync 使用同一归属规则，禁止以宽 prefix 删除再回填邻居数据；该做法仍会暴露数据丢失窗口。

上述等待已完成；主控发出 B 第二次 ready 后，实施了以下最终定向复核。

## 第二次 ready 后的最终定向复核：PASS

本轮只读取冻结补丁的五个文件：PluginManager、PluginMonitorSyncService、新 PluginKeyOwnership、InfoService，以及 ReviewPluginReloadRegressionTests 中与该阻塞相关的变更。另对首次 ready 的其余七个文件复核 SHA-256，均与先前记录相同，包含 PluginRuntimeSnapshot；不重复审查已经完成的其他边界。

| 核对项 | 当前证据及判断 |
| --- | --- |
| 公开数据精确归属 | `PluginKeyOwnership.cs:10–39` 从实例／模板快照生成完整输出键、颜色、单位、DASH 默认值、标签和 api_text 根键。Manager 在 `:260–285` 准备并登记每个实例的键集合；Stop 后保留归属，后续真正清理仍能识别旧键。 |
| 精确删除且与退役排序 | `PluginManager.cs:209–212` 只把该实例登记的完整键交给 `InfoService.RemoveDataByKeys()`；`InfoService.cs:182–189` 在其数据锁内逐键删除。Manager `:287–300` 先退役 lease，再在同一发布门内清理；没有宽 prefix 删除、邻居快照回填或门外重新覆盖数据的窗口。 |
| 监控项同源归属 | `PluginMonitorSyncService.cs:37–43` 和 `:133–143` 使用共用 helper 的输出键／归属台账做精确孤儿删除；`:153–165` 的移除也只删除属于目标实例的完整键。`PluginKeyOwnership.cs:42–91` 将台账绑定到 Settings 实例，并在 MonitorItems 锁下访问；导入持久化键时选择完整的最长实例 ID，随后记录实际输出键。因此 A 离开 settings 后仍不会把 A.B 认作 A。 |
| 可见性、标签及排序 | `PluginManager.cs:461–471` 的真实可见性判断、`PluginMonitorSyncService.cs:171–201` 的标签查询使用同一归属；新增输出在 `:96–99` 追加排序位置，不重排邻居已有项。锁方向仍为发布门到 MonitorItems／InfoService；helper 不回调 Manager，没有新增取消、外部等待或事件调用。 |
| 原反例的回归判别力 | `ReviewPluginReloadRegressionTests.cs:278–395` 的四组参数覆盖 A / AB 与 A / A.B，分别走模板卸载和实例移除；每例先执行同版本 URL 更新，再执行输出键替换，再移除。关闭 VisibilityProbe，驱动真实可见性路径。在邻居再次执行之前逐次断言值／颜色／单位／标签、MonitorItem 对象和序列化状态、步骤缓存、KeyCache、runtime／timer 身份及 Start 次数不变，并强制触发旧父计时器验证不能复活。最后验证邻居仍可抓取。 |

因此，原先“只更新或删除 A 就清空 A.B 的公开数据并误删 MonitorItems”的可触发调用链已被精确键归属切断。**P2-PLUG-01 关闭；本轮最终 PASS，无剩余具体阻塞。** 网络重置、发布退役、主机政策、sandbox／反射接缝的先前审查结论保持有效。

本结论是代码与测试内容的独立静态复核，不宣称新增四例或全量双配置已通过。本审查者没有运行构建／测试；主控正在统一执行定向及完整 Debug / Release 验证，其结果由主控记录。本轮唯一写入仍为本报告。

### 冻结文件 SHA-256

第二次 ready 后在读取前及结束时重复核对，五个补丁文件哈希一致；其余七个文件与首次 ready 记录一致。

| 文件 | SHA-256 |
| --- | --- |
| `src/Plugins/PluginManager.cs` | `4883CE14940C564B03682EBCC655AA9BED39AC3AD07652C74ED0436A689DF37C` |
| `src/Plugins/PluginMonitorSyncService.cs` | `A5A811352C5AB072125A6F0FA2274E0A41A49E37D5F05325B463345130E27ADA` |
| `src/Plugins/PluginKeyOwnership.cs` | `4B2546FA2A969B6A2A178555649A641BE060F985E8337F2E616C2BCDC3E18790` |
| `src/System/InfoService/InfoService.cs` | `9E4650676A1ED914AFF950EE494A72D1DD43D6C123AD59DCB953AC02D620920D` |
| `tests/LiteMonitor.Tests/ReviewPluginReloadRegressionTests.cs` | `00BD2E4B9E454429D4A2208211D21CCF5782B935BEE642DE12EC1F315F794146` |
| `src/Plugins/PluginExecutor.cs` | `5B8FDB3430C3282972B582E4C05DF7F45F2C3855F3CF2B70E627E9EFFAD67A4F` |
| `src/Plugins/PluginProcessor.cs` | `BD7717F33A56F162BE6B876F5775415C89DF6739E887FB8010862C36295BDDA2` |
| `src/Plugins/PluginHostPolicy.cs` | `4F62FD0DA0275E3625A3DAD5A52ED4E5A22746055AAD3505F5DE720B66D4657B` |
| `src/Plugins/PluginPublicationLease.cs` | `65D1043AD6D675D2B6D67ECF859D9F9E6DE90591616A3B528B3D535071F11682` |
| `src/Plugins/PluginRuntimeSnapshot.cs` | `C71F2701ED7E8C39CEAC18525D19735FEC2ED98F2B999E06739D3CF732A473BB` |
| `src/Plugins/Native/CryptoNative.cs` | `6D6372A13546A332D2C95627D058739C67BA3FB20CB1A3C4E7CFD907D913F3FB` |
| `tests/LiteMonitor.Tests/ReviewPluginHostNormalizationTests.cs` | `AF4A1490BA3583D19C6BDA6222F02774C2725BCA26B2C0C658FD62B23F9E22ED` |
