using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 C · <b>LM-082</b>：原生（<c>native://crypto</c>）请求的**在途归并**按
    /// <b>实例 ID</b>而不是<b>执行作用域</b>，且共享任务**绑定创建者的取消令牌**。
    ///
    /// ★ 缺陷事实（修复前的源码）：
    ///   ① <c>src/Plugins/PluginExecutor.cs</c>：<c>ExecuteNativeAsync(..., inst?.Id ?? "")</c>
    ///      ⇒ 在途键 = <c>cacheKey + "|" + 实例ID</c>；**同一实例的两个代次**（两次执行）
    ///      共享同一条在途请求。
    ///   ② <c>src/Plugins/Native/CryptoNative.cs</c>：共享任务用**创建者**的 <c>token</c> 启动
    ///      ⇒ 旧代次作用域到期 ⇒ 共享任务被取消 ⇒ **新代次的复用者一起失败**。
    ///   ⇒ 与 HTTP 步骤路径（<c>CancellationToken.None</c> + 等待者侧监听自己的令牌）**口径不一致**，
    ///      违反 S1.0 契约 C3.4"取消一个等待者不得取消共享任务"。
    ///
    /// ★ 危害方向：**跨代次取消相互影响**（新代次被旧代次的到期连累）。
    ///
    /// 测试纪律：
    ///  · 一律只连 127.0.0.1 回环的本地服务器（**不触公网**），服务器自带请求计数；
    ///  · 官方数据源通过 <see cref="CryptoNative.SourceUrlOverrides"/> 指向本地回环（测试接缝）；
    ///  · 每个用例用**唯一**交易对（行情缓存是进程级静态的，复用会让请求数变 0 ⇒ 失去判别力）；
    ///  · 全部断言都真正执行，不存在早退 / 跳过。
    /// </summary>
    public class BatchCLM082NativeScopeTests
    {
        /// <summary>Bybit 形状的行情 JSON（官方源第一个就是 Bybit，解析器按该形状解析）。</summary>
        private const string BybitTickerJson =
            "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[" +
            "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"95000\",\"price24hPcnt\":\"0.0123\"," +
            "\"highPrice24h\":\"96000\",\"lowPrice24h\":\"94000\",\"turnover24h\":\"123456\"}]}}";

        /// <summary>
        /// LM-082 核心断言（**作用域键**）：**同一实例**的**两个执行作用域**（两个代次）
        /// 必须**各自发出自己的请求**，且**互不取消**。
        /// <para>修复前：在途键是**实例 ID** ⇒ 第二个代次复用第一个代次的在途请求
        /// （服务端自始至终只看到 1 条请求），且第一个代次到期时**连带打断**第二个代次。</para>
        /// </summary>
        [Fact]
        public async Task LM082_同一实例的不同执行作用域不共享在途请求且互不取消()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(req =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return RedirectSandboxResponse.Text(BybitTickerJson);
            }, "127.0.0.1");
            using var exec = new PluginExecutor();
            using var ov = new NativeSourceOverride(server.Url);

            // ★ 同一个实例（模拟"同一实例被重启 ⇒ 两个代次"）
            var inst = NewInstance("inst-lm082-gen");
            var step = NativeStep("native://crypto?symbol=" + UniqueSymbol("GEN"));

            using var firstScope = new CancellationTokenSource();
            var first = RunStepAsync(exec, inst, step, firstScope.Token);

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(15)),
                "第一个代次的请求必须真的发出（回环服务器未收到请求）");

            // ★ 第二个代次（另一个执行作用域）：同实例、同交易对
            var second = RunStepAsync(exec, inst, step);

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 2, TimeSpan.FromSeconds(15)),
                "不同执行作用域没有发出自己的请求（跨代次复用了同一条在途请求）");

            // 第一个代次被取消 ⇒ **不得**影响第二个代次
            firstScope.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            gate.Set();
            await second;      // ★ 第二个代次必须成功拿到结果

            Assert.Equal(2, server.RequestCount);
        }

        /// <summary>
        /// LM-082 核心断言（**共享任务的令牌**）：**创建者**被取消时，
        /// **不得**连带取消同作用域内正在复用的等待者（S1.0 契约 C3.4）。
        /// <para>修复前：共享任务绑定**创建者**的令牌 ⇒ 创建者一取消，等待者一起失败。</para>
        /// </summary>
        [Fact]
        public async Task LM082_创建者被取消不得连带取消同作用域的等待者()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(req =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return RedirectSandboxResponse.Text(BybitTickerJson);
            }, "127.0.0.1");
            using var ov = new NativeSourceOverride(server.Url);

            string symbol = UniqueSymbol("CR");
            using var creatorCts = new CancellationTokenSource();
            using var waiterCts = new CancellationTokenSource();

            // 同一个执行作用域（同一个 inflightScopeKey）⇒ 应当**共享一条**在途请求
            var creator = CryptoNative.FetchAsync(symbol, "", creatorCts.Token, 0, null, "scope-lm082");
            var waiter = CryptoNative.FetchAsync(symbol, "", waiterCts.Token, 0, null, "scope-lm082");

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(15)),
                "原生请求必须真的发出（回环服务器未收到请求）");
            await Task.Delay(300);
            Assert.Equal(1, server.RequestCount);   // ★ 同一作用域 ⇒ 只发一条

            // ★ 创建者被取消 ⇒ 共享任务不得被连带取消
            creatorCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);

            gate.Set();
            string result = await waiter;           // ★ 等待者仍要拿到结果
            Assert.False(string.IsNullOrEmpty(result), "等待者必须拿到响应正文");
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// LM-082 的**对称护栏**：**等待者**被取消**不得**取消创建者
        /// （修复前这条本来就成立；修复后必须继续成立 —— 共享任务不绑定任何一方）。
        /// </summary>
        [Fact]
        public async Task LM082_等待者被取消不得取消创建者()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(req =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return RedirectSandboxResponse.Text(BybitTickerJson);
            }, "127.0.0.1");
            using var ov = new NativeSourceOverride(server.Url);

            string symbol = UniqueSymbol("WT");
            using var creatorCts = new CancellationTokenSource();
            using var waiterCts = new CancellationTokenSource();

            var creator = CryptoNative.FetchAsync(symbol, "", creatorCts.Token, 0, null, "scope-lm082");
            var waiter = CryptoNative.FetchAsync(symbol, "", waiterCts.Token, 0, null, "scope-lm082");

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(15)),
                "原生请求必须真的发出（回环服务器未收到请求）");
            await Task.Delay(300);
            Assert.Equal(1, server.RequestCount);

            // ★ 等待者被取消 ⇒ 创建者不受影响
            waiterCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

            gate.Set();
            string result = await creator;          // ★ 创建者仍要拿到结果
            Assert.False(string.IsNullOrEmpty(result), "创建者必须拿到响应正文");
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// LM-082 的**作用域键护栏**：**不同**执行作用域 ⇒ **各自一条**请求
        /// （同作用域 ⇒ 仍然只发一条，见上一条用例）。
        /// </summary>
        [Fact]
        public async Task LM082_不同执行作用域各自发出自己的请求()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(req =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return RedirectSandboxResponse.Text(BybitTickerJson);
            }, "127.0.0.1");
            using var ov = new NativeSourceOverride(server.Url);

            string symbol = UniqueSymbol("SC");

            var a = CryptoNative.FetchAsync(symbol, "", CancellationToken.None, 0, null, "scope-A");
            var b = CryptoNative.FetchAsync(symbol, "", CancellationToken.None, 0, null, "scope-B");

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 2, TimeSpan.FromSeconds(15)),
                "不同执行作用域必须各自发出请求（实际 " + server.RequestCount + " 条）");

            gate.Set();
            var results = await Task.WhenAll(a, b);
            Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r)));
            Assert.Equal(2, server.RequestCount);
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        private static PluginInstanceConfig NewInstance(string id) => new PluginInstanceConfig { Id = id };

        /// <summary>构造一条 native:// 步骤。</summary>
        private static PluginExecutionStep NativeStep(string url)
        {
            return new PluginExecutionStep
            {
                Id = "s1",
                Url = url,
                Method = "GET",
                ResponseEncoding = "utf-8",
                ResponseFormat = "text",
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = 0
            };
        }

        /// <summary>驱动产品的步骤执行路径（私有方法，签名与生产一致）。</summary>
        private static async Task RunStepAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step, CancellationToken token = default)
        {
            var context = new Dictionary<string, string>();
            var task = (Task)Method("ExecuteStepAsync").Invoke(exec, new object[] { inst, step, context, "", token });
            await task.ConfigureAwait(false);
        }

        private static MethodInfo Method(string name)
        {
            var m = typeof(PluginExecutor).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.NotNull(m);
            return m;
        }

        /// <summary>每次用例都用**唯一**的交易对（行情缓存是进程级静态的）。</summary>
        private static string UniqueSymbol(string tag) =>
            "LM082" + tag + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

        /// <summary>
        /// 测试接缝：把 <see cref="CryptoNative"/> 的官方数据源 URL 指向本地回环服务器
        /// （避免用例触公网）。**只换"请求去哪"**，闸门仍是生产同一条路径。
        /// </summary>
        private sealed class NativeSourceOverride : IDisposable
        {
            private readonly Dictionary<string, string> _previous;

            public NativeSourceOverride(string baseUrl)
            {
                _previous = CryptoNative.SourceUrlOverrides;
                CryptoNative.SourceUrlOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bybit"] = baseUrl + "/bybit?symbol={symbol}",
                    ["GateIO"] = baseUrl + "/gate?symbol={symbol}",
                    ["Binance"] = baseUrl + "/binance?symbol={symbol}",
                    ["OKX"] = baseUrl + "/okx?symbol={symbol}",
                };
            }

            public void Dispose()
            {
                CryptoNative.SourceUrlOverrides = _previous;
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (predicate()) return true;
                await Task.Delay(25);
            }
            return predicate();
        }
    }
}
