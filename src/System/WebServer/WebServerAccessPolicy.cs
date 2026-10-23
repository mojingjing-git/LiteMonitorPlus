using System;

namespace LiteMonitor.src.WebServer
{
    /// <summary>
    /// Web 监听的访问策略判定（纯函数 · 无副作用 · 可单测）。
    /// </summary>
    /// <remarks>
    /// ★ 「局域网暴露必须配口令」这条规则有**两个**执行点，必须共用同一个判定：
    ///   <list type="number">
    ///   <item><b>冷启动</b>：<see cref="LiteWebServer.Start"/> —— 局域网模式 + 无有效口令 ⇒ 拒绝启动；</item>
    ///   <item><b>运行中变更</b>：<c>AppActions.ApplyWebServer</c> —— 运行中把口令清掉 ⇒ 必须停止监听。</item>
    ///   </list>
    ///   两处共用 ⇒ 运行中的变更**绕不过**冷启动那条约束（R03）。
    /// </remarks>
    public static class WebServerAccessPolicy
    {
        /// <summary>「对局域网暴露，但当前没有有效凭据」⇒ 必须拒绝监听。</summary>
        /// <param name="cfg">当前设置；<c>null</c> 视为无配置 ⇒ 不拒绝。</param>
        /// <param name="runtimeLanExposed">
        /// 运行时事实：正在监听**非回环**地址。
        /// ★ 之所以不只看配置：配置写着「仅本机」但服务其实还绑在 0.0.0.0 上时（监听范围尚未重绑），
        ///   凭据一旦被清掉也必须停 —— 否则旧的对外监听会一直敞着。
        /// </param>
        public static bool IsLanAccessForbidden(Settings? cfg, bool runtimeLanExposed)
        {
            if (cfg == null) return false;

            bool lanRequired = cfg.WebServerBindMode == WebServerBindMode.Lan;
            if (!lanRequired && !runtimeLanExposed) return false;

            // ★ 没有有效凭据（既没有明文，也没有落盘的哈希）⇒ 对局域网暴露就是无凭据暴露
            return !cfg.HasWebPasswordConfigured();
        }
    }
}
