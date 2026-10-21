using System;
using System.IO;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 仅在 <c>LITEMONITOR_PLUGIN_REPO</c> 指向一个含 <c>index.json</c> 的插件仓库副本时才运行的
    /// <see cref="FactAttribute"/>。
    ///
    /// 这些契约测试原本在门控不满足时 <c>Console.WriteLine</c> + <c>return</c>，
    /// 于是 runner 记为 Passed ——「没跑却报通过」会让本地与 CI 都误以为契约已被验证。
    /// 现在未满足条件时由 xunit 记为 Skipped，用例体在门控满足时必然真正执行。
    ///
    /// 门控满足后若仍加载失败（例如环境在特性构造与用例执行之间被改动），
    /// 用例体会以断言失败而非静默返回收场。
    /// </summary>
    public sealed class PluginRepoFactAttribute : FactAttribute
    {
        /// <summary>开关环境变量名。</summary>
        public const string RepoVariable = "LITEMONITOR_PLUGIN_REPO";

        public PluginRepoFactAttribute()
        {
            // 测试项目未启用可空引用类型标注（#nullable），此处不使用 string? 注解。
            string root = Environment.GetEnvironmentVariable(RepoVariable);

            if (string.IsNullOrEmpty(root))
            {
                Skip = $"需要 {RepoVariable} 指向插件仓库副本才运行";
                return;
            }

            if (!Directory.Exists(root))
            {
                Skip = $"{RepoVariable} 指向的目录不存在：{root}";
                return;
            }

            if (!File.Exists(Path.Combine(root, "index.json")))
            {
                Skip = $"{RepoVariable} 目录下没有 index.json：{root}";
            }
        }
    }
}
