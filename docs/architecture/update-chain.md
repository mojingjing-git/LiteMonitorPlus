# 更新链、签名与事务替换

本文描述 LiteMonitor++ 的自动更新链路：更新源的选择与切换、发布元数据的签名与验签、安装包摘要校验的时序、主程序与独立更新器之间的交接契约、压缩包展开的安全边界、文件级事务替换与回滚，以及用户数据保护、提权模型和更新结果回读。

它回答五个问题：**更新包凭什么可信**，**谁在什么时候检查可信性**，**一次更新失败后安装目录会变成什么样**，**哪些文件用户拥有因而绝不能被覆盖**，**主程序与更新器之间靠什么约定交接**。

适用范围是 `src/System/UpdateChecker.cs`（元数据获取与验签）、`src/UI/UpdateDialog.cs`（下载对话框与摘要校验）、`LiteMonitor.Updater/`（独立更新器工程）、`scripts/sign-release.ps1` 与 `scripts/pack-release.ps1`（发布侧），以及 `src/UI/Helpers/MainFormBizHelper.cs` 中与更新结果回读相关的部分。渲染路线、插件市场、主题工作室、Web 服务的内部设计不在本文范围；模块总体拓扑见 `docs/architecture/overview.md`。

---

## 1. 组件与信任边界

### 1.1 组件职责

| 组件 | 位置 | 职责 | 明确不负责 |
|---|---|---|---|
| `UpdateChecker` | `src/System/UpdateChecker.cs` | 拉取发布元数据、验签、比较版本、给出期望摘要、准备更新器二进制 | 不下载安装包，不写安装目录 |
| `UpdateDialog` | `src/UI/UpdateDialog.cs` | 多镜像下载、摘要校验、提权启动更新器 | 不解压，不替换程序文件 |
| `UpdatePackageBinder` | `src/UI/UpdateDialog.cs` | 以只读钉住句柄打开安装包并计算摘要、构造更新器参数 | 不解释摘要语义，不判定版本 |
| `LiteMonitor.Updater` | `LiteMonitor.Updater/Program.cs` | 参数解析、摘要校验、展开、事务替换、回滚、结果留痕 | 不联网（生产装配下），不比较版本 |
| `UpdateFileTransaction` | `LiteMonitor.Updater/UpdateFileTransaction.cs` | 逐文件备份 / 暂存 / 提交 / 校验 / 回滚 | 不关心更新包来源，不写日志 |
| `ITrustedDigestSource` | `LiteMonitor.Updater/TrustedDigestSource.cs` | 兼容接缝：为不随包传递摘要的调用方提供摘要来源 | 不做写操作，失败一律返回空 |
| `LegacyBridgeMarker` | `LiteMonitor.Updater/LegacyBridgeMarker.cs` | 写出机器可读的手动升级指引标记 | 不弹窗，不改安装文件 |

### 1.2 依赖方向

信任只从一个方向流出：

```
私钥（不入库）
  └─> scripts/sign-release.ps1 ──> version.json 的 sha256 + signature
        └─> 主程序内置公钥验签 ──> 期望摘要
              └─> 更新器命令行第二参数 ──> 更新器再次校验摘要 ──> 展开与替换
```

由此得到两条硬约束：

- 私钥材料绝不进入版本库，也绝不进入发布包；`scripts/sign-release.ps1` 是唯一的信任产生点。
- 摘要的权威来源只能是**已验签的元数据**，任何其它途径得到的摘要都只是提示，不得驱动写操作。

### 1.3 数据流

一次完整的更新按以下顺序推进，任何一步未通过都不得进入下一步：

| 步 | 动作 | 失败即停 |
|---|---|---|
| 1 | 读取并消费升级指引标记 | 无标记即正常继续 |
| 2 | 拉取发布元数据并验签 | 换源；全部源失败即终止 |
| 3 | 比较版本，决定是否提供更新 | 已是最新即终止 |
| 4 | 从镜像下载安装包 | 换镜像；全部镜像失败即终止 |
| 5 | 计算并比对安装包摘要 | 摘要不符即删除坏包并终止 |
| 6 | 提权启动更新器 | 用户取消即终止 |
| 7 | 更新器再次校验摘要、展开、事务替换 | 见第 7 节的回滚语义 |
| 8 | 主程序回读结果标记并提示用户 | — |

---

## 2. 更新源与镜像切换

### 2.1 元数据源

发布元数据是 `version.json`，由固定的有序地址列表提供，列表顺序即优先级：

| 优先级 | 地址 |
|---|---|
| 主 | `https://raw.githubusercontent.com/mojingjing-git/LiteMonitorPlus/master/resources/version.json` |
| 镜像 | `https://gitee.com/mojingjing-git/LiteMonitorPlus/raw/master/resources/version.json` |

切换条件（`UpdateChecker.GetVersionInfoAsync`）：

- 单一来源的响应头期限 3 s、正文期限 3 s、整体期限 8 s；正文按 `MetadataMaxBodyBytes = 256 KiB` 边读边计数，超限即判该源失败。
- 响应体去 BOM 后按声明的字符集解码；解码失败判该源失败。
- `version` 或 `releaseDate` 缺失判该源失败（读取时即抛出，源不可用）。
- **验签失败判该源失败**（`LastFailure = UpdateCheckFailure.Signature`），并立即换下一个源，绝不降级为「未签名但可用」。
- 网络类异常判该源失败（`LastFailure = UpdateCheckFailure.Network`，细节写入 `LastFailureDetail`）。
- 全部源失败 ⇒ 元数据为空，`LatestVersionInfo` 与 `LatestZipSha256` 被清空，流程终止。

`UpdateChecker.LastFailure` 的取值是 `UpdateCheckFailure` 枚举的 `None` / `Network` / `Signature`，用于把「网络不通」与「更新源不可信」区分成两类用户可见提示。

### 2.2 下载镜像

安装包下载使用同样形状的镜像列表（GitHub Releases 与 Gitee Releases，各一条 `LiteMonitorPlus_v{version}-win-x64.zip` 路径），并额外提供发布页地址作为手工下载入口。

排序规则（`UpdateChecker.GetSortedZipUrls` 与 `UpdateDialog.TestMirrors`）：

- 对每个候选地址做测速：响应头期限 5 s、正文期限 5 s，最多读取 32 KiB 估算吞吐。
- 按实测速度降序排列；测速失败者排在后面但保留在列表中。
- 全部候选都测速失败时保持默认顺序。

切换条件（`UpdateDialog.btnUpdate_Click`）：

- 下载使用 `ResponseHeadersRead` 流式写盘，单个请求超时 10 min。
- 单一镜像下载失败（HTTP 错误状态码、网络异常、用户取消）即换下一个镜像。
- 全部镜像失败 ⇒ 提示下载失败，不产生安装包。
- `DownloadContext.LocalPath` 存在时优先使用本地离线包；复制到下载路径后**仍走完全相同的摘要校验**，信任边界不因来源不同而放宽。

### 2.3 TLS 与证书失败

主程序与更新对话框都不设置自定义证书校验回调，TLS 一律走系统信任链。证书失败必须可见并中止更新，系统**不提供任何跳过校验的开关**——被下载的安装包随后会被执行，绕过证书校验等同于允许中间人替换更新包。证书失败与下载失败分别提示，并在提示中给出发布页入口供用户手工处置。

---

## 3. 签名机制

### 3.1 算法

| 项 | 取值 |
|---|---|
| 签名算法 | ECDSA，P-256 曲线 |
| 摘要算法 | SHA-256 |
| 签名编码 | DER 序列（RFC 3279），base64 文本 |
| 密钥格式 | SPKI / DER，base64 文本 |
| 载荷编码 | UTF-8，无 BOM，行分隔符为单个 LF |

### 3.2 载荷格式

被签名的规范化载荷由三段构成，顺序固定（`UpdateChecker.BuildUpdateSignaturePayload`，更新器侧的 `BuildSignaturePayload` 与之逐字节一致）：

```
<version>\n<sha256>\n<releaseDate>
```

- `version`：发布版本号，与元数据中的 `version` 完全相同。
- `sha256`：安装包的 SHA-256 摘要，十六进制文本。
- `releaseDate`：发布日期，格式 `YYYY-MM-DD`，与元数据中的 `releaseDate` 完全相同。

设计取舍：签名范围刻意限定为「哪一个版本的哪一个字节内容、什么时候发布」三元组，而不覆盖整段 JSON。载荷因此与 JSON 的排版、字段顺序、附加字段无关，签名工具链只需维护一条规范化规则；代价是元数据中未被签名字段覆盖的内容不受签名保护（见第 11 节）。

### 3.3 公钥内置位置

公钥以常量形式内置在两处，两处必须逐字节相同：

- `src/System/UpdateChecker.cs` 的 `UpdateChecker.UpdateSigningPublicKeySpkiBase64`；
- `LiteMonitor.Updater/TrustedDigestSource.cs` 中 `SignedVersionMetadataDigestSource` 内置的同一常量。

公钥指纹（对公钥 base64 文本取 SHA-256 后的十六进制）为 `25E6659D954A92A9FCCF1E2622F30F68A7FA9C84390B419B33F3EB0F11F97572`。指纹是人工核对的锚点：更换签名密钥必须同步两处常量、核对指纹，并让两端的常量保持一致，否则验签将在两端同时失效。

### 3.4 验签规则

`UpdateChecker.TryVerifyUpdateSignature` 的判定是 fail-closed 的：

- `version`、`sha256`、`releaseDate`、`signature` 任一为空或空白 ⇒ 验签失败。
- `sha256` 不是 64 位十六进制 ⇒ 验签失败。
- base64 解码失败、密钥导入失败、验签计算异常 ⇒ 验签失败。
- 只有 `ECDsa.VerifyData(payload, signature, SHA256, Rfc3279DerSequence)` 返回 true 才算通过。

**验签失败即不信任**：该元数据源被整体丢弃并切换到下一个源，绝不出现「签名无效但先用着」的中间状态。`VerifyUpdateSignatureWithKey` 提供公钥注入重载，仅供测试替换密钥使用，生产路径只使用内置公钥。

### 3.5 元数据字段契约

`version.json` 中被消费的字段：

| 字段 | 必需性 | 用途 |
|---|---|---|
| `version` | 必需 | 版本比较、签名载荷第一段 |
| `releaseDate` | 必需 | 界面展示、签名载荷第三段 |
| `sha256` | 必需 | 期望摘要、签名载荷第二段 |
| `signature` | 必需 | 对三段载荷的签名 |

`downloadUrl` 不参与下载决策：安装包地址由第 2.2 节的镜像列表推导，发布地址列表不来自元数据。元数据中出现的其它字段同样不被消费。

### 3.6 签名的产生

`scripts/sign-release.ps1` 是唯一允许用私钥签名的入口，其自检包含两项：

- 私钥导出的 SPKI 必须与主程序内置的 `UpdateSigningPublicKeySpkiBase64` 常量一致，否则中止——这把「换了密钥但没换客户端」的错配挡在发布侧。
- 签名完成后立刻用同一载荷做一次自验，通过才允许回写元数据。

摘要由脚本自行对安装包计算，不接受外部传入值。脚本回写 `version` / `releaseDate` / `sha256` / `signature` 四个键，元数据模板必须已预留 `sha256` 与 `signature` 占位键。发布脚本默认读取 `%LOCALAPPDATA%\LiteMonitor++\signing\update-signing-key.pkcs8`，CI 则显式传入 runner 临时私钥；日常单元测试只在 `LITEMONITOR_UPDATE_SIGNING_KEY_PATH` 显式设置或兼容的仓库 `secrets/` 私钥存在时执行公私钥配对检查。

---

## 4. 摘要校验的时序

### 4.1 主程序侧

下载完成后，主程序按以下顺序校验（`UpdateDialog.btnUpdate_Click`）：

1. `UpdatePackageBinder.IsWellFormedDigest(ExpectedSha256)` 判定期望摘要格式合法（64 位十六进制）；不合法 ⇒ 抛出 `UpdateIntegrityException`。
2. `UpdatePackageBinder.OpenPinned(SavePath)` 以 `FileShare.Read` 打开安装包，得到钉住句柄——其它进程仍可读，但**不得写入、替换或删除**该文件。
3. `UpdatePackageBinder.ComputeSha256Hex(pinned)` 在钉住句柄上计算摘要。
4. 与期望摘要比对（大小写不敏感）；不匹配 ⇒ 抛出 `UpdateIntegrityException`。
5. 匹配则保存钉住句柄与已验证摘要，供交接给更新器。

摘要不匹配时的处置是提示「安装包校验失败」并删除损坏的安装包，不进入任何解压或写操作。

### 4.2 更新器侧

`LiteMonitor.Updater.Program.RunUpdate` 的第一步就是摘要校验，顺序为：

1. 定位主程序目录（含 `LiteMonitor++.exe` 的目录）；定位不到即以 `ExitNoMainProgram` 退出。
2. 校验期望摘要格式；不合法即以 `ExitIntegrityRejected` 退出。
3. `OpenPinnedPackage` 钉住安装包并计算摘要；与期望摘要不一致即以 `ExitIntegrityRejected` 退出。
4. 其后才是互斥锁、等待主程序退出、写入 `update_in_progress`、解压、事务替换。

### 4.3 不变量

**摘要校验必须早于任何解压或写操作。** 具体地说，在更新器侧，摘要未通过比对时，不得解压条目、不得创建临时目录、不得写入任何标记文件、不得触碰安装目录中的任何文件。这条约束同时是安全属性（被篡改的包不得在磁盘上展开）与正确性属性（损坏的包不得留下半成品）。

### 4.4 钉住句柄的用途

钉住（以共享读、不共享写的方式打开）消除的是 TOCTOU 窗口：从「主程序校验完」到「更新器展开」之间，安装包在磁盘上的内容必须与被校验的字节完全一致。主程序在交接期间保持句柄打开，直到更新器进程被启动后才释放。

---

## 5. 更新器交接契约

### 5.1 命令行参数

独立更新器与主程序之间只有一个契约：位置参数 `<安装包路径> <期望摘要>`。

- 参数由 `UpdatePackageBinder.BuildUpdaterArguments(packagePath, verifiedSha256)` 生成，摘要以小写十六进制传入；期望摘要非法时该方法直接抛出 `ArgumentException`，不存在「构造不出参数就退化为单参数」的路径。
- `UpdateDialog.StartUpdater` 在没有钉住句柄或没有已验证摘要时拒绝启动更新器，并提示安装包校验失败。
- **期望摘要必须随包传递**。参数个数不足或摘要位置为空时，更新器不得继续解压。

### 5.2 兼容接缝

`ITrustedDigestSource` 是为「不随包传递摘要」的调用方保留的接缝：

| 实现 | 行为 |
|---|---|
| `NullTrustedDigestSource` | 恒返回空；生产装配的默认值 |
| `SignedVersionMetadataDigestSource` | 通过注入的取元数据函数读取 `version` / `sha256` / `releaseDate` / `signature`，用内置公钥验签后返回小写摘要 |

接缝的规则：

- 来源自身抛异常、取不到元数据、摘要畸形、验签不通过 ⇒ 一律返回空，fail-closed。
- **不提供摘要时不得继续解压**：摘要不可得即以 `ExitIntegrityRejected` 结束。
- 元数据获取函数由构造参数注入，生产装配不注入，因此生产环境下单参数调用一律被拒绝。
- 更新器侧内置的公钥与主程序内置公钥逐字节一致，`BuildSignaturePayload` 的格式与主程序一致。

### 5.3 退出码

| 退出码 | 常量 | 含义 |
|---|---|---|
| 0 | `ExitSuccess` | 更新成功 |
| 2 | `ExitUsage` | 未提供参数 |
| 3 | `ExitNoMainProgram` | 找不到主程序目录 |
| 5 | `ExitUpdateFailed` | 更新过程失败（已按三态语义处理） |
| 6 | `ExitIntegrityRejected` | 摘要缺失、畸形或与安装包不符 |
| 7 | `ExitConcurrentUpdate` | 同一安装目录已有更新在进行 |

### 5.4 交接标记文件

更新器在主程序目录写入以下标记，主程序在下次启动时回读：

| 文件 | 语义 |
|---|---|
| `update_success` | 更新成功；主程序提示后删除 |
| `update_in_progress` | 更新正在进行，含版本、备份目录与时间；进程异常退出会留下它 |
| `update_error.log` | 失败诊断，含 `[Result] status=...` 行与 `[Summary]` 段 |
| `update.lock` | 同目录互斥锁句柄文件（`FileShare.None`），保证同一安装目录同时只有一个更新在跑 |
| `update_legacy_bridge.json` | 机器可读的升级指引标记，schema `liteMonitor.legacyBridge/v1` |

`update_legacy_bridge.json` 的载荷字段为 `schema`、`code`、`reason`、`guidance`、`manualDownloadUrl`、`updaterVersion`、`timestamp`。当更新器因摘要不可得而拒绝执行时，写出 `code = missing_digest_rejected` 与指向发布页的手工升级指引。主程序先于任何网络请求读取该标记并提示用户，读到即删除（消费语义），保证同一条提示不重复出现。标记中不含任何私钥材料。

### 5.5 更新器自身的自举

新版本安装包内自带 `LiteMonitor++.Updater.exe`。`UpdateChecker.PreUpdateUpdater` 负责把更新器自身换成新版本：从安装包中取出该条目，解到 `resources/<文件名>.new`，比对落地长度与条目声明长度，再以 `--selfcheck` 启动该副本（3 s 超时，退出码必须为 0），通过后备份为 `<目标路径>.bak`、覆盖目标、删除暂存文件。任一步失败都不阻断主流程，更新器回退到安装目录中已有的 `resources/LiteMonitor++.Updater.exe` 或 `resources/Updater.exe`。

更新器自身的 `--selfcheck` 参数优先于一切其它参数解析。

---

## 6. 压缩包展开安全

### 6.1 展开上限

展开上限是生产常量（`LiteMonitor.Updater.ZipExpansionLimits`）：

| 常量 | 值 | 含义 |
|---|---|---|
| `MaxEntryCount` | 4096 | 条目数量上限 |
| `MaxSingleEntryBytes` | 256 MiB | 单条目展开大小上限 |
| `MaxExpandedBytes` | 512 MiB | 整个包展开总大小上限 |

上限同时作用于条目声明值与实际写出的字节数，两者任一越界即拒绝展开。

### 6.2 路径规则

条目相对路径经 `NormalizeEntryRelativePath` 规范化后，下列形态一律拒绝：

- 绝对路径与带盘符的路径；
- 含 `:` 的路径（NTFS 备用数据流）；
- 含 `..` 片段的穿越路径；
- 以空格或点结尾的片段（Windows 路径规范化歧义）。

规范化之后，落地路径还要经 `ResolveEntryDestination` 确认位于目标目录之内；落地路径落在目标目录之外即拒绝。**展开过程不使用 `ZipFile.ExtractToDirectory`**，所有条目均逐条经过上述判定。

### 6.3 条目一致性

实际写出的字节数与条目声明长度不一致即拒绝展开，保证落地内容与包内声明一致，避免截断包造成半截文件被当作完整文件提交。

### 6.4 条目名编码

条目名先按 UTF-8 试探解析；条目名出现 U+FFFD 替换字符或解析抛错时，改用 GBK 重开包继续展开，以兼容用本地代码页创建的中文名压缩包。

---

## 7. 事务替换

### 7.1 整体顺序

`LiteMonitor.Updater.Program.RunUpdate` 的执行顺序是固定的：

1. 定位主程序目录；
2. 校验并比对安装包摘要（第 4.2 节）；
3. 获取同目录互斥锁（`update.lock`，`FileShare.None`）；获取失败即以 `ExitConcurrentUpdate` 退出，不产生任何混合内容；
4. 等待主程序退出：3 s 礼貌等待 → 请求关闭主窗口 → 最多再等 5 s 后强制结束进程；
5. 写入 `update_in_progress` 标记；
6. 解压到临时目录 `_update_tmp_<yyyyMMdd_HHmmss>`；若包内只有一个子目录则取该子目录作为包根；
7. 对包根执行 `UpdateFileTransaction.ApplyDirectory`；
8. 校验主程序可执行文件 `LiteMonitor++.exe` 在安装目录中存在，存在即视为提交成功；
9. 成功路径：写 `update_success`、删除备份目录、删除安装包、删除 `update_in_progress`、删除 `update_error.log`、删除升级指引标记，然后重启主程序；
10. 失败路径：删除 `update_success`，写入失败诊断日志（含 `[Result] status=...` 与 `[Summary]`），并把日志副本复制进备份目录以便排障。

### 7.2 逐文件事务

`UpdateFileTransaction.ApplyDirectory` 按 `OrdinalIgnoreCase` 顺序遍历包根的全部文件，对每个文件执行 `ApplyFile`：

| 步 | 动作 | 不变量 |
|---|---|---|
| 备份 | 原文件存在时复制到备份目录 | 备份完成后必须比对长度与 SHA-256，两者任一不符即视为备份失败并中止 |
| 暂存 | 在目标同目录写入 `.lmu-stage-<guid>.tmp` | 暂存内容必须与包内内容比对通过才允许提交 |
| 提交 | 原文件存在用 `File.Replace`，原文件不存在用 `File.Move` | 提交前设置 `CommitStarted`，`IOException` / `UnauthorizedAccessException` 按固定间隔重试 |
| 校验 | 提交后重新比对长度与 SHA-256 | 校验不通过即按失败处理并进入回滚 |
| 清理 | 删除暂存文件 | 无论成败都不留暂存残留 |

命中「用户拥有路径」的文件被完全跳过：不备份、不覆盖、不删除，并记入 `SkippedPaths`。

### 7.3 回滚规则

回滚按**提交顺序的逆序**执行，只针对 `CommitStarted` 为真的记录：

- 原文件存在 ⇒ 从备份目录复制回目标（经 `.lmu-rollback-<guid>.tmp` 暂存、校验、提交、再校验），恢复状态置为 `Restored`。
- 原文件不存在 ⇒ 删除该次更新新增的文件，恢复状态置为 `Removed`。
- 恢复过程抛错 ⇒ 恢复状态置为 `Failed` 并记录错误信息。

逆序是硬约束：提交顺序保证了文件之间的一致性假设，回滚必须按相反顺序拆解这个假设，否则可能出现「先恢复依赖方、再恢复被依赖方」的中间错误状态。

### 7.4 三态结果

`UpdateApplyOutcome` 的取值与语义：

| 取值 | 条件 | 含义 |
|---|---|---|
| `Succeeded` | 全部文件提交并校验通过 | 安装目录处于新版本状态 |
| `FailedBeforeCommit` | 没有任何文件进入提交 | 失败发生在尚无文件提交时，安装目录未被修改 |
| `FailedRecovered` | 有提交但回滚全部成功 | 失败发生但已恢复到更新前状态 |
| `FailedRecoveryIncomplete` | 回滚存在失败项 | 恢复不完整，需人工介入 |

`UpdateRollbackResult.IsComplete`（失败项数为零）是 `FailedRecovered` 与 `FailedRecoveryIncomplete` 的判定依据。

诊断日志中 `[Result]` 行的 `status` 取值与之一一对应：`failed_before_commit`、`recovered`、`recovery_incomplete`，以及恢复状态无法确认时的 `unknown`。恢复不完整时备份目录必须保留，供用户手工恢复。

### 7.5 失败留痕

`update_error.log` 每条结果行都带时间戳。诊断材料在通知性日志被删除后仍需保留可追溯副本，因此日志副本随备份目录一同保留。

---

## 8. 用户数据保护

### 8.1 用户拥有的路径

`UpdateFileTransaction` 维护一份用户拥有路径清单（相对于安装目录）：

| 类型 | 路径 |
|---|---|
| 文件 | `settings.json`、`settings.json.bak`、`settings.json.tmp` |
| 文件 | `HardwareHistory.json`、`HardwareHistory.json.bak`、`HardwareHistory.json.tmp` |
| 文件 | `TrafficHistory.json`、`TrafficHistory.json.bak`、`TrafficHistory.json.tmp` |
| 目录 | `resources/themes` |
| 目录 | `resources/plugins` |

匹配规则（`IsUserOwnedPath`）：路径分隔符统一为 `\`，去掉前导 `\` 后按清单匹配；目录项按 `目录\` 前缀匹配，即目录下的全部内容都归用户所有。

**更新绝不得覆盖这些路径**：命中清单的文件在事务替换中被完全跳过。

### 8.2 便携布局的取舍

这些用户数据全部落在程序目录（`AppContext.BaseDirectory`）：

| 数据 | 落盘位置 | 归属代码 |
|---|---|---|
| 设置 | `<程序目录>/settings.json` | `src/Core/SettingsHelper.cs` |
| 硬件历史 | `<程序目录>/HardwareHistory.json` | `src/Core/HardwareHistoryLogger.cs` |
| 流量历史 | `<程序目录>/TrafficHistory.json` | `src/Core/TrafficLogger.cs` |
| 主题 | `<程序目录>/resources/themes` | `src/Core/ThemeManager.cs` |
| 插件 | `<程序目录>/resources/plugins` | `src/Plugins/PluginProcessor.cs` |

取舍的正面价值：整个安装目录可整体复制或移动，配置、主题、插件随程序走，无需额外的用户目录初始化，也不会在用户配置目录留下第二份副本。

取舍的代价：程序目录必须可写；安装目录位于受保护位置时配置与插件无法落盘；更新器必须显式维护用户拥有路径清单——只要清单遗漏一项，更新就会覆盖用户数据，因此该清单是更新链与用户数据之间的唯一防线，必须与实际落盘位置保持同步。

### 8.3 发布侧的个人状态剔除

`scripts/pack-release.ps1` 在压缩前剔除运行时配置与日志（`settings.json` 及其 `.bak` / `.tmp`、`TrafficHistory.json` 及其 `.bak` / `.tmp`、顶层 `*.log`），确保构建机的个人状态不随发布包分发。`settings.json` 中的口令以 PBKDF2 哈希与盐保存，不写明文。

---

## 9. 提权模型

### 9.1 判定

`UpdateDialog.IsRunningAsAdmin` 用 `WindowsIdentity.GetCurrent()` 配合 `WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)` 判定当前进程是否已具备管理员权限。主程序清单 `app.manifest` 声明 `requestedExecutionLevel level="requireAdministrator"`。

### 9.2 提权启动

| 当前权限 | 启动方式 |
|---|---|
| 已是管理员 | `UseShellExecute = false`，更新器直接继承当前权限与工作目录 |
| 非管理员 | `UseShellExecute = true` 且 `Verb = "runas"`，触发 UAC 提权 |

### 9.3 用户取消

用户取消 UAC 会以 `Win32Exception`（`NativeErrorCode = 1223`）结束。该情形被识别为「用户主动放弃」，按静默取消处理：恢复更新对话框的按钮状态，保留已下载并已通过摘要校验的安装包，不写任何失败诊断——用户放弃不是失败。

### 9.4 其它失败

提权启动的其它异常弹出错误提示并终止。更新器二进制自身缺失（安装包内没有、备份位置也没有）时报「文件丢失」并终止。

---

## 10. 更新结果回读

主程序启动时由 `src/UI/Helpers/MainFormBizHelper.cs` 的 `CheckUpdateSuccess` 读取主程序目录下的三个标记：

| 组合 | 用户可见提示 | 处置 |
|---|---|---|
| `update_success` | 提示更新成功 | 删除 `update_success`、`update_in_progress`、`update_error.log` |
| `update_error.log` | 按 `[Result]` 的 `status` 分别呈现：失败但已恢复、提交前失败未改动安装目录、恢复不完整需人工处理、状态未知 | 删除日志与 `update_in_progress` 标记 |
| 仅 `update_in_progress` | 上次更新被中断，当前安装状态待确认 | 保留 `_update_backup_*` 备份目录供人工恢复 |

「恢复不完整」是唯一需要人工介入的情形：备份目录在此时必须保持存在。

---

## 11. 已知限制

- 回滚只覆盖单次更新过程：系统保留当前版本与一次更新备份，不支持回退到任意历史版本，也不保留多版本历史。
- 签名只覆盖 `version` / `sha256` / `releaseDate` 三段载荷。元数据中未被这三段覆盖的字段不受签名保护；`downloadUrl` 之类的字段当前不参与下载决策，安装包地址来自固定镜像列表。
- 摘要与安装包字节强绑定：元数据与安装包若不是同一次发布动作产生，比对必然不匹配，更新被拒绝（这是 fail-closed 的必然结果，而不是可恢复的错误）。
- 便携布局要求程序目录可写；安装目录不可写时设置、主题与插件无法落盘。
- 展开上限是固定生产常量（条目数 4096、单条目 256 MiB、总量 512 MiB），不随磁盘可用空间动态调整；包超过上限时整包拒绝，不做部分展开。
- 兼容接缝在生产装配下不提供摘要来源，因此不带摘要参数的调用一律被拒绝并需要人工下载升级。
