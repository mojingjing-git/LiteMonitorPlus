using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件市场用例会碰进程级静态（<c>PluginMarket.HttpHandlerOverride</c>、配置单例）
    /// ⇒ 归入禁并行 collection，避免与其它市场用例互相污染。
    /// </summary>
    [CollectionDefinition("marketrecovery", DisableParallelization = true)]
    public class BatchD1MarketRecoveryCollection
    {
    }

    /// <summary>
    /// P2 批次 D · LM-083 护栏：安装事务的**恢复**必须排在**任何加载 / 调度之前**。
    ///
    /// <para><b>缺陷（修复前）：</b></para>
    /// <list type="bullet">
    /// <item><c>PluginMarket</c> 把启动恢复挂在**静态构造函数</c>上 ⇒ 恢复发生在"类型首次被碰到"的那一刻；</item>
    /// <item><c>PluginManager</c> 全文**零** <c>PluginMarket</c> 引用 ⇒ 加载路径根本不会触发恢复；</item>
    /// <item>主窗体的启动顺序是「先 <c>LoadPlugins</c> + <c>Start</c>，后 <c>PluginSelfCheck.RunOnce</c>」
    ///       ⇒ 第一次碰到 <c>PluginMarket</c> 是自检那一刻 ⇒ **恢复晚于加载与调度**。</item>
    /// </list>
    /// <para>⇒ 磁盘回滚了，<b>内存里跑的却仍是新版本</b> ⇒ 回滚承诺在进程内不成立。</para>
    ///
    /// <para><b>本护栏守三件事：</b></para>
    /// <list type="number">
    /// <item>插件管理器在**枚举插件模板之前**必须先对账安装事务（结构性兜底）；</item>
    /// <item>那个"对账入口"必须**真的**调用市场的恢复（不许是空壳）；</item>
    /// <item>主窗体启动时必须**先恢复、再加载、再调度**（顺序一眼可见）。</item>
    /// </list>
    ///
    /// <para>★ 顺序用 <b>IL 反汇编</b>断言（与 <c>MainFormStartupOrderTests</c> 同一手法）：
    ///   它看的是**真实编译产物的先后顺序**，不依赖任何运行期时序，因此不会假绿。</para>
    ///
    /// <para>★ 本类**绝不碰共享配置**：既不写 settings.json，也不重建配置单例
    ///   （重建单例会把"监听范围"的解析结果**冻结**在当时的文件状态上，
    ///   从而污染同批次里的网页服务用例）。详见 <see cref="WithIsolatedSettings"/>。</para>
    /// </summary>
    [Collection("marketrecovery")]
    public class BatchD1MarketRecoveryOrderingTests
    {

        // ================================================================
        //  ① 顺序：插件管理器必须"先恢复、后枚举"
        // ================================================================

        [Fact]
        public void 护栏_插件管理器枚举模板前必须先对账安装事务()
        {
            MethodInfo loadPlugins = typeof(PluginManager).GetMethod(
                "LoadPlugins",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            Assert.True(loadPlugins != null, "PluginManager.LoadPlugins(string) 不存在 —— 护栏前提被破坏。");

            IlInspector.Disassembly body = IlInspector.Disassemble(loadPlugins);

            int recoverOffset = FindCallOffset(body, m =>
                m.DeclaringType == typeof(PluginManager) && m.Name == "RecoverMarketTransactions");
            int enumerateOffset = FindCallOffset(body, m =>
                m.DeclaringType == typeof(Directory) && m.Name == "GetFiles");

            Assert.True(recoverOffset >= 0,
                "PluginManager.LoadPlugins 里找不到对账安装事务的调用（PluginManager.RecoverMarketTransactions）—— " +
                "恢复必须排在任何加载之前，否则磁盘回滚了而内存里仍跑新版本（LM-083）。");
            Assert.True(enumerateOffset >= 0,
                "PluginManager.LoadPlugins 里找不到插件模板枚举（Directory.GetFiles）—— 护栏前提被破坏。");

            Assert.True(recoverOffset < enumerateOffset,
                "初始化顺序错误：对账安装事务在 IL 偏移 " + recoverOffset +
                "，而枚举插件模板在偏移 " + enumerateOffset +
                "。恢复必须排在加载之前 —— 否则磁盘回滚了，内存里跑的却仍是新版本（LM-083）。");
        }

        // ================================================================
        //  ② 对账入口必须真的调用市场的恢复（不许是空壳）
        // ================================================================

        [Fact]
        public void 护栏_对账入口必须真的调用市场的安装事务恢复()
        {
            MethodInfo recover = typeof(PluginManager).GetMethod(
                "RecoverMarketTransactions",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            Assert.True(recover != null, "PluginManager.RecoverMarketTransactions(string) 不存在 —— 护栏前提被破坏。");

            IlInspector.Disassembly body = IlInspector.Disassemble(recover);

            bool callsMarketRecovery = body.Instructions.Any(ins =>
                (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                ins.Target is MethodInfo called &&
                called.DeclaringType == typeof(PluginMarket) &&
                called.Name.StartsWith("RecoverInterruptedInstalls", StringComparison.Ordinal));

            Assert.True(callsMarketRecovery,
                "PluginManager.RecoverMarketTransactions 必须真的调用 PluginMarket 的安装事务恢复 —— " +
                "空壳入口等于没恢复（LM-083）。");
        }

        // ================================================================
        //  ③ 顺序：主窗体必须"先恢复、再加载、再调度"
        // ================================================================

        [Fact]
        public void 护栏_主窗体必须先恢复再加载与调度插件()
        {
            ConstructorInfo ctor = typeof(MainForm).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            Assert.True(ctor != null, "MainForm 无参构造函数不存在 —— 护栏前提被破坏。");

            IlInspector.Disassembly body = IlInspector.Disassemble(ctor);

            int recoverOffset = FindCallOffset(body, m =>
                m.DeclaringType == typeof(PluginMarket) &&
                m.Name.StartsWith("RecoverInterruptedInstalls", StringComparison.Ordinal));
            int loadOffset = FindCallOffset(body, m =>
                m.DeclaringType == typeof(PluginManager) && m.Name == "LoadPlugins");
            int startOffset = FindCallOffset(body, m =>
                m.DeclaringType == typeof(PluginManager) && m.Name == "Start");

            Assert.True(recoverOffset >= 0,
                "MainForm 构造函数里找不到安装事务恢复的调用（PluginMarket.RecoverInterruptedInstalls）—— " +
                "恢复必须排在加载与调度之前（LM-083）。");
            Assert.True(loadOffset >= 0,
                "MainForm 构造函数里找不到 PluginManager.LoadPlugins —— 护栏前提被破坏。");
            Assert.True(startOffset >= 0,
                "MainForm 构造函数里找不到 PluginManager.Start —— 护栏前提被破坏。");

            Assert.True(recoverOffset < loadOffset,
                "初始化顺序错误：安装事务恢复在 IL 偏移 " + recoverOffset +
                "，而加载插件在偏移 " + loadOffset +
                "。先加载再恢复 ⇒ 磁盘回滚了而内存里仍跑新版本（LM-083）。");
            Assert.True(recoverOffset < startOffset,
                "初始化顺序错误：安装事务恢复在 IL 偏移 " + recoverOffset +
                "，而启动插件调度在偏移 " + startOffset +
                "。调度必须排在恢复之后（LM-083）。");
        }

        // ================================================================
        //  ④ 行为：回滚之后，内存里跑的必须是**旧版本**
        // ================================================================

        /// <summary>
        /// ★ 这是缺陷本身的复现：进程被杀时"替换已经发生、安装记录还没跟上"，
        ///   磁盘上留着**新版本**。若先加载再恢复，内存里跑的就是新版本，
        ///   而磁盘随后被回滚成旧版本 ⇒ 回滚承诺在进程内不成立。
        /// </summary>
        [Fact]
        public void 行为_回滚之后加载到内存的必须是旧版本()
        {
            string dir = NewPluginDir();
            try
            {
                string target = Path.Combine(dir, "demo_lm083.json");
                byte[] oldBytes = TemplateBytes("1.0");
                byte[] newBytes = TemplateBytes("2.0");
                File.WriteAllBytes(target, oldBytes);

                // 模拟"进程被杀"：原子替换已经发生（磁盘=新版本），安装记录还没更新
                Assert.Throws<MarketInstallCrashException>(() =>
                    PluginMarketInstallCore.CommitInstall(
                        dir, "demo_lm083", "2.0", newBytes, Sha256(newBytes),
                        id => null,
                        rec => throw new InvalidOperationException("崩溃点在更新记录之前，记录不应被改动"),
                        id => { },
                        point => { if (point == "afterMove") throw new MarketInstallCrashException(point); }));

                // 前置条件（反证）：此刻磁盘上确实是**新版本**
                Assert.Equal("2.0", ReadVersion(target));

                // 生产路径：加载插件（内部先对账、后加载）
                PluginTemplate? tmpl = null;
                WithIsolatedSettings(() =>
                {
                    var manager = new PluginManager();
                    manager.LoadPlugins(dir);
                    tmpl = manager.GetAllTemplates().FirstOrDefault(t => t.Id == "demo_lm083");
                });

                // 磁盘已回滚到旧版本
                Assert.Equal("1.0", ReadVersion(target));

                // ★ 内存里跑的必须是**旧版本**（不是磁盘上曾经出现过的那个新版本）
                Assert.True(tmpl != null, "回滚后应当仍然能加载到该插件（旧版本）。");
                Assert.Equal("1.0", tmpl!.Meta.Version);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        // ================================================================
        //  ⑤ 行为：没有在途事务时，恢复不得动任何文件
        // ================================================================

        [Fact]
        public void 行为_没有在途事务时恢复不得改动任何文件()
        {
            string dir = NewPluginDir();
            try
            {
                string target = Path.Combine(dir, "demo_lm083.json");
                byte[] bytes = TemplateBytes("1.0");
                File.WriteAllBytes(target, bytes);
                string shaBefore = Sha256File(target);

                var manager = new PluginManager();
                WithIsolatedSettings(() => manager.LoadPlugins(dir));

                Assert.Equal(shaBefore, Sha256File(target));
                Assert.Equal("1.0", ReadVersion(target));
                Assert.False(File.Exists(Path.Combine(dir, PluginMarketInstallCore.TransactionLogFileName)),
                    "没有在途事务时不应留下写前日志。");
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>
        /// 在**不碰共享配置**的前提下执行一段会加载插件的代码。
        /// <para>
        /// ★ 为什么要隔离：<c>PluginManager.LoadPlugins</c> 读完模板后会"自动同步配置"
        ///   —— 它会把**当前配置单例**写回 settings.json、并可能删掉用户已有的插件实例。
        ///   测试里真写用户的 settings.json / 真删别人的实例，会污染同集合里的其它用例
        ///   （例如把 <c>WebServerEnabled</c> 写进磁盘，让网页服务用例直接起不来）。
        /// </para>
        /// <para>
        /// ★ 所以这里：① 先把配置单例备好（后续调用不再读盘）；② 临时把插件实例换成空列表；
        ///   ③ 临时禁写（让那次"自动同步"不落盘）；④ 结束后原样还原。
        /// </para>
        /// </summary>
        private static void WithIsolatedSettings(Action body)
        {
            // ★ 用 Settings.Load()（**不** forceReload）：只取**已经存在**的单例，
            //   绝不重建它 —— 重建会把"监听范围"的解析结果冻结在当时的文件状态上
            //   （文件里若是 WebServerEnabled=true 且没有监听范围字段 ⇒ 解析成局域网模式），
            //   从而让后面网页服务用例直接起不来。
            LiteMonitor.Settings cfg = LiteMonitor.Settings.Load();
            List<PluginInstanceConfig> savedInstances = cfg.PluginInstances;
            bool savedBlock = LiteMonitor.SettingsHelper.GlobalBlockSave;
            try
            {
                cfg.PluginInstances = new List<PluginInstanceConfig>();
                LiteMonitor.SettingsHelper.GlobalBlockSave = true;   // 禁止这次加载把配置写回磁盘
                body();
            }
            finally
            {
                LiteMonitor.SettingsHelper.GlobalBlockSave = savedBlock;
                cfg.PluginInstances = savedInstances;
            }
        }

        private static int FindCallOffset(IlInspector.Disassembly body, Func<MethodBase, bool> match)
        {
            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call &&
                    instruction.OpCode != OpCodes.Callvirt &&
                    instruction.OpCode != OpCodes.Newobj)
                {
                    continue;
                }

                if (instruction.Target != null && match(instruction.Target))
                {
                    return instruction.Offset;
                }
            }

            return -1;
        }

        private static byte[] TemplateBytes(string version)
            => Encoding.UTF8.GetBytes(
                "{\"id\":\"demo_lm083\",\"meta\":{\"name\":\"demo\",\"version\":\"" + version + "\"}}");

        private static string ReadVersion(string path)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("meta").GetProperty("version").GetString() ?? "";
        }

        private static string Sha256(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static string Sha256File(string path)
            => Sha256(File.ReadAllBytes(path));

        private static string NewPluginDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lm-lm083-dir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
