using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteMonitor.src.Plugins
{
    // ==========================================
    // 1. 模版定义 (对应 JSON 文件)
    // ==========================================
    public class PluginTemplate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = ""; // 模版唯一ID (如 "com.weather")

        [JsonPropertyName("meta")]
        public PluginMeta Meta { get; set; } = new();

        [JsonPropertyName("inputs")]
        public List<PluginInput> Inputs { get; set; } = new();

        [JsonPropertyName("execution")]
        public PluginExecution Execution { get; set; } = new();

        [JsonPropertyName("outputs")]
        public List<PluginOutput> Outputs { get; set; } = new();

        [JsonPropertyName("display")]
        public PluginDisplay Display { get; set; } = new();
    }

    public class PluginDisplay
    {
        // 监控项显示名称 (支持 {{key}} 替换)
        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; 

        // 任务栏简写 (支持 {{key}} 替换)
        [JsonPropertyName("short_label")]
        public string ShortLabel { get; set; } = "";
    }

    public class PluginMeta
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>英文名。界面为非中文时优先使用，缺失则回退 <see cref="Name"/>。</summary>
        [JsonPropertyName("name_en")]
        public string NameEn { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>英文简介。界面为非中文时优先使用，缺失则回退 <see cref="Description"/>。</summary>
        [JsonPropertyName("description_en")]
        public string DescriptionEn { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    public class PluginInput
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = ""; // 参数名 (如 "city_code")

        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; // 显示给用户的名称

        /// <summary>英文显示名。界面为非中文时优先使用，缺失则回退 <see cref="Label"/>。</summary>
        [JsonPropertyName("label_en")]
        public string LabelEn { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "text"; // text, password, select...

        [JsonPropertyName("default")]
        public string DefaultValue { get; set; } = "";

        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = "";

        [JsonPropertyName("options")]
        public List<PluginInputOption>? Options { get; set; }

        // Scope: "global" (default) or "target"
        // global: 整个插件实例共享 (如 API Key)
        // target: 每个目标单独配置 (如 股票代码)
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "global";
    }

    public class PluginExecution
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "api_json"; // api_json, api_text, chain

        [JsonPropertyName("method")]
        public string Method { get; set; } = "GET"; // GET, POST

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 60;

        [JsonPropertyName("min_interval")]
        public int MinInterval { get; set; } = 0; // 可选：限制用户自定义刷新频率的最小值，0 表示不额外限制

        [JsonPropertyName("url")]
        public string Url { get; set; } = ""; // 支持 {{key}} 替换

        [JsonPropertyName("body")]
        public string Body { get; set; } = ""; // POST body, 支持 {{key}} 替换

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("extract")]
        public Dictionary<string, string> Extract { get; set; } = new();

        [JsonPropertyName("process")]
        public List<PluginTransform>? Process { get; set; }

        [JsonPropertyName("steps")]
        public List<PluginExecutionStep>? Steps { get; set; }
    }

    public class PluginExecutionStep
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "GET";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("response_encoding")]
        public string ResponseEncoding { get; set; } = "utf-8"; // utf-8, gbk

        [JsonPropertyName("response_format")]
        public string ResponseFormat { get; set; } = "json"; // json, jsonp, text

        [JsonPropertyName("extract")]
        public Dictionary<string, string> Extract { get; set; } = new(); // Variables to extract

        [JsonPropertyName("process")]
        public List<PluginTransform>? Process { get; set; }

        [JsonPropertyName("cache_minutes")]
        public int CacheMinutes { get; set; } = 0; // 0=No Cache, -1=Forever

        [JsonPropertyName("skip_if_set")]
        public string SkipIfSet { get; set; } = ""; // If context[SkipIfSet] is present & not empty, skip this step

        /// <summary>
        /// [F13] 允许本步骤失败后继续执行链中的后续步骤（默认 false）。
        /// <para><b>默认 false</b>：行为与既有实现逐字一致 —— 步骤失败即中断整条链。</para>
        /// <para><b>true</b> 时只有**业务可恢复失败**（请求失败 / 无有效数据 / 字段提取失败 /
        /// 单请求超时）会被吞掉并进入下一步；<b>外层取消、单次执行总时长耗尽、沙箱违规</b>
        /// （并发 / 响应体 / 步数 / 目标数 / 凭据目标 / 重定向）一律**不可吞**，
        /// 必须照旧中断整条链。</para>
        /// <para>★ 语义细节：可回退步骤使用<b>临时 context</b>，<b>只有成功</b>才把变量合并回链上下文
        /// ⇒ 失败不会留下半成品（❓ / ❌）误触发后续步骤的 <see cref="SkipIfSet"/>。</para>
        /// </summary>
        [JsonPropertyName("continue_on_error")]
        public bool ContinueOnError { get; set; } = false;

        [JsonPropertyName("proxy")]
        public string Proxy { get; set; } = ""; // Optional: "host:port" for proxy
    }

    public class PluginTransform
    {
        [JsonPropertyName("var")]
        public string TargetVar { get; set; } = ""; // 目标变量名 (如 "temp")

        [JsonPropertyName("source")]
        public string SourceVar { get; set; } = ""; // 源变量名 (如 "temp")

        [JsonPropertyName("function")]
        public string Function { get; set; } = "regex_replace"; // 转换函数 (regex_replace, map)

        // For regex_replace
        [JsonPropertyName("pattern")]
        public string Pattern { get; set; } = ""; // 正则表达式模式
        [JsonPropertyName("to")]
        public string To { get; set; } = "";// 替换字符串

        // For map
        [JsonPropertyName("map")]
        public Dictionary<string, string>? Map { get; set; }

        // [New] Simplified Key-Value Map for Thresholds (e.g. { "0": "0", "80": "2" })
        [JsonPropertyName("value_map")]
        public Dictionary<string, string>? ValueMap { get; set; }
    }

    public class PluginOutput
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = ""; // 唯一标识后缀，如 "temp"

        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; // 完整显示名称模式，如 "{{city}} 温度"

        /// <summary>英文显示名称模式。界面为非中文时优先使用，缺失则回退 <see cref="Label"/>。</summary>
        [JsonPropertyName("label_en")]
        public string LabelEn { get; set; } = "";

        [JsonPropertyName("short_label")]
        public string ShortLabel { get; set; } = ""; // 任务栏名称模式

        /// <summary>英文任务栏名称模式。界面为非中文时优先使用，缺失则回退 <see cref="ShortLabel"/>。</summary>
        /// <remarks>注意：<c>short_label</c> 为 " " 表示"隐藏标签"，此语义在英文下同样保留。</remarks>
        [JsonPropertyName("short_label_en")]
        public string ShortLabelEn { get; set; } = "";

        [JsonPropertyName("format_val")]
        public string Format { get; set; } = ""; // 数据格式，如 "{{val}}"

        [JsonPropertyName("color")]
        public string Color { get; set; } = ""; // 颜色状态 (0=Safe, 1=Warn, 2=Crit)

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = ""; // 单位
    }

    public class PluginInputOption
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("value")]
        public string Value { get; set; } = "";
    }

    // [Optimization] Cache keys to avoid string concatenation in hot loops
    public class PluginOutputKeys
    {
        // ★ 给安全默认值：这些键由 PluginExecutor 的输出预处理器一次性填好，
        //   但 DTO 本身可能被别处 new 出来，给默认空串可避免"未初始化即被读取"。
        public string InjectKey = "";
        public string InjectColorKey = "";
        public string InjectUnitKey = "";
        public string PropLabelKey = "";
        public string PropShortKey = "";
    }
}
