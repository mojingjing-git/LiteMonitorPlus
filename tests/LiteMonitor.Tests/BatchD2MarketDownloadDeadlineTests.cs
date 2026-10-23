using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件市场用例会改进程级静态 <c>PluginMarket.HttpHandlerOverride</c>
    /// ⇒ 归入禁并行 collection，避免与其它市场用例互相污染。
    /// </summary>
    [CollectionDefinition("marketstatics", DisableParallelization = true)]
    public class BatchD2MarketStaticCollection
    {
    }

    /// <summary>
    /// P2 批次 D · LM-084 护栏：插件市场**正文读取**必须有期限。
    ///
    /// <para><b>缺陷（修复前）：</b></para>
    /// <list type="bullet">
    /// <item>下载走 <c>HttpCompletionOption.ResponseHeadersRead</c>
    ///       ⇒ <c>HttpClient.Timeout</c> 只约束到**响应头**；</item>
    /// <item>正文读取只用**调用方**令牌，而生产调用（市场页安装）**不传令牌**；</item>
    /// <item>⇒ 正文读取就是**无限等待** ⇒ 市场页的 _busy 永久卡死。</item>
    /// </list>
    ///
    /// <para><b>本护栏守三件事：</b></para>
    /// <list type="number">
    /// <item>没有调用方令牌时，正文读取也必须在期限内**中止**（不得无限等待）；</item>
    /// <item>调用方主动取消仍要能**提前中断**（不得被降级成"只能等超时"）；</item>
    /// <item>生产入口（不传令牌的那个重载）必须**带上期限**（不许漏掉）。</item>
    /// </list>
    ///
    /// <para>★ 用**假流**制造"响应头已到、正文永不到"的现场，用**可注入的期限**把等待压到毫秒级：
    ///   不靠长时间 <c>Thread.Sleep</c>，也不靠真实网络。</para>
    /// </summary>
    [Collection("marketstatics")]
    public class BatchD2MarketDownloadDeadlineTests : IDisposable
    {
        private static readonly Uri TestUri = new Uri(
            "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/demo.json");

        private readonly HttpMessageHandler? _oldHandler;

        public BatchD2MarketDownloadDeadlineTests()
        {
            _oldHandler = PluginMarket.HttpHandlerOverride;
            PluginMarket.HttpHandlerOverride = new HangingBodyHandler();
        }

        public void Dispose()
        {
            PluginMarket.HttpHandlerOverride = _oldHandler;
        }

        // ================================================================
        //  ① 没有调用方令牌时，正文读取也必须在期限内中止
        // ================================================================

        /// <summary>
        /// ★ 盯的缺陷：正文读取不受任何期限约束 ⇒ 调用方不传令牌时就是**无限等待**。
        /// ★ 反证：先把"响应头已到、正文永不到"这个现场造出来（假处理器 + 假流），
        ///   再断言下载任务**在期限内结束**（而不是一直挂着）。
        /// </summary>
        [Fact]
        public async Task 正文读取必须有期限_没有调用方令牌时也要在期限内中止()
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            Task<byte[]> download = PluginMarket.DownloadBytesAsync(
                TestUri, cts.Token, TimeSpan.FromMilliseconds(300));

            Task finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));
            sw.Stop();

            try
            {
                Assert.True(ReferenceEquals(finished, download),
                    "正文读取在期限内没有中止（耗时 " + sw.Elapsed.TotalSeconds.ToString("0.0") +
                    " 秒）—— 没有调用方令牌时下载会无限等待，市场页 _busy 会永久卡死（LM-084）。");

                // 必须是"超时中止"，而且要报得出来（不是静默卡住）
                PluginMarketException ex = await Assert.ThrowsAsync<PluginMarketException>(
                    async () => await download);
                Assert.Contains("超时", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                cts.Cancel();
            }
        }

        // ================================================================
        //  ② 调用方主动取消仍要能提前中断
        // ================================================================

        /// <summary>
        /// ★ 盯的缺陷：把"用户主动取消"降级成"只能等超时"（例如只用一个期限令牌、不接调用方令牌）。
        /// ★ 现场：期限给得很长（30 秒），只靠调用方令牌在 200 毫秒时取消 ——
        ///   若实现忽略调用方令牌，下载要等到 30 秒期限才会结束（断言会红）。
        /// </summary>
        [Fact]
        public async Task 调用方取消仍能提前中断_不得降级成只能等超时()
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();

            Task<byte[]> download = PluginMarket.DownloadBytesAsync(
                TestUri, cts.Token, TimeSpan.FromSeconds(30));

            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            Task finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));
            sw.Stop();

            Assert.True(ReferenceEquals(finished, download),
                "调用方取消没有中断正文读取（耗时 " + sw.Elapsed.TotalSeconds.ToString("0.0") +
                " 秒）—— 用户主动取消不得被降级成只能等超时（LM-084）。");

            // ★ 取消语义必须保留：抛的是取消异常，而不是"超时"
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
        }

        // ================================================================
        //  ③ 生产入口（不传令牌的重载）必须带上期限
        // ================================================================

        /// <summary>
        /// ★ 盯的缺陷：给"带期限"的重载加了期限，却让**生产入口**（市场页调用、不传令牌的那个）
        ///   绕开它 ⇒ 现场仍然无限等待。
        /// ★ 这里看编译产物：生产重载必须转调带期限的重载。
        /// </summary>
        [Fact]
        public void 生产入口必须带上下载期限()
        {
            MethodInfo production = typeof(PluginMarket).GetMethod(
                "DownloadBytesAsync",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Uri), typeof(CancellationToken) },
                null);
            Assert.True(production != null,
                "PluginMarket.DownloadBytesAsync(Uri, CancellationToken) 不存在 —— 护栏前提被破坏。");

            IlInspector.Disassembly body = IlInspector.Disassemble(production);

            bool forwardsToDeadlineOverload = body.Instructions.Any(ins =>
                (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                ins.Target is MethodInfo called &&
                called.DeclaringType == typeof(PluginMarket) &&
                called.Name == "DownloadBytesAsync" &&
                called.GetParameters().Length == 3);

            Assert.True(forwardsToDeadlineOverload,
                "生产入口 DownloadBytesAsync(Uri, CancellationToken) 必须转调带期限的重载 —— " +
                "市场页安装走的正是它，漏掉期限就又是无限等待（LM-084）。");
        }

        // ================================================================
        //  辅助：假处理器 + 假流
        // ================================================================

        /// <summary>
        /// 假处理器：立刻返回**响应头**，正文是一个**永远不会读完**的流
        /// （= "响应头已到、正文迟迟不来"的真实故障形态）。
        /// </summary>
        private sealed class HangingBodyHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new HangingContent()
                };
                response.Content.Headers.ContentLength = 1024;   // 声称有正文，但正文永不到
                return Task.FromResult(response);
            }
        }

        private sealed class HangingContent : HttpContent
        {
            protected override Task<Stream> CreateContentReadStreamAsync()
                => Task.FromResult<Stream>(new HangingStream());

            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
                => Task.FromResult<Stream>(new HangingStream());

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => Task.CompletedTask;

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }

        /// <summary>永远读不出数据的流；只有令牌取消时才会结束。</summary>
        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }
    }
}
