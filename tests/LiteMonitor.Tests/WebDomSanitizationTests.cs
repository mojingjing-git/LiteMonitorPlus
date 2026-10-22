using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 仅在 PATH 上存在 <c>node</c> 时才运行的 <see cref="FactAttribute"/>。
    ///
    /// 行为级用例（把网页脚本放进一个最小 DOM 替身里真跑一遍）需要 JS 运行时；
    /// CI 环境（windows-latest、无 Node）拿不到时由 xunit 记为 <c>Skipped</c>，
    /// 而不是让用例体 <c>return</c> 之后仍显示 Passed。
    /// 「没跑却报通过」比明确跳过更有害 —— 所以 CI 的门禁由同文件里的
    /// <b>结构级断言</b>承担（那部分不依赖任何外部运行时）。
    /// </summary>
    public sealed class NodeScriptFactAttribute : FactAttribute
    {
        public NodeScriptFactAttribute()
        {
            if (!NodeRunner.IsAvailable())
            {
                Skip = "需要 PATH 上存在 node 才能运行行为级用例（CI 无 Node 时由结构级断言兜底）";
            }
        }
    }

    /// <summary>node 可用性探测（结果缓存，避免每个用例都起一次进程）。</summary>
    internal static class NodeRunner
    {
        private static bool? _available;

        public static bool IsAvailable()
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("node", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { _available = false; return false; }
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    _available = p.ExitCode == 0;
                }
            }
            catch
            {
                _available = false;
            }
            return _available.Value;
        }

        /// <summary>
        /// 把网页脚本交给 node 里的最小 DOM 替身真跑一遍，返回 JSON 结果。
        /// </summary>
        public static string RunHarness(string scriptNoAuth, string scriptAuth)
        {
            string dir = Path.Combine(Path.GetTempPath(), "litemonitor-webdom-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string noAuthPath = Path.Combine(dir, "page-noauth.js");
                string authPath = Path.Combine(dir, "page-auth.js");
                string harnessPath = Path.Combine(dir, "harness.js");
                File.WriteAllText(noAuthPath, scriptNoAuth, new UTF8Encoding(false));
                File.WriteAllText(authPath, scriptAuth, new UTF8Encoding(false));
                File.WriteAllText(harnessPath, HarnessJs, new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo(
                    "node",
                    "\"" + harnessPath + "\" \"" + noAuthPath + "\" \"" + authPath + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // ★ 必须显式指定 UTF-8：默认会用系统 ANSI 代码页解码，
                    //   页面里的中文（分组名、监控名）会变成乱码并破坏 JSON。
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) throw new InvalidOperationException("无法启动 node");
                    Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(true); } catch { }
                        throw new TimeoutException("node 行为级用例超时");
                    }
                    string stdout = stdoutTask.GetAwaiter().GetResult();
                    string stderr = stderrTask.GetAwaiter().GetResult();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException("node 退出码 " + p.ExitCode + "：" + stderr);
                    }
                    return stdout;
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// 行为级用例的 JS 载体：把网页脚本放进一个**最小 DOM 替身**里真跑一遍。
        ///
        /// 替身的保真度（以及它的边界，写在这里以免被误读成"真实浏览器"）：
        ///  <list type="bullet">
        ///   <item>实现了页面真正用到的那部分 DOM：createElement / createElementNS /
        ///   appendChild / setAttribute / classList / textContent / style / id / className。</item>
        ///   <item><c>innerHTML</c> setter **会如实解析标记**（标签、属性、文本），
        ///   所以"有人把外部数据拼回 HTML"在这里会真的变成元素 —— 而不是被忽略掉。
        ///   它不执行脚本，而是把 script 元素 / on* 事件属性 / javascript: URL 记成违规。</item>
        ///   <item>它**不是**浏览器：没有布局、没有 CSS 级联、没有真实事件派发。
        ///   因此它能证明"数据流进了哪个 DOM 节点"，不能证明"像素级渲染结果"。</item>
        ///  </list>
        /// 该脚本刻意只用单引号字符串（正则里用 \x22 表示双引号），
        /// 以便原样放进 C# 的逐字字符串里。
        /// </summary>
        private const string HarnessJs = @"
'use strict';
const fs = require('fs');
const vm = require('vm');

const scriptNoAuth = fs.readFileSync(process.argv[2], 'utf8');
const scriptAuth = fs.readFileSync(process.argv[3], 'utf8');

const violations = [];
function note(kind, detail) { violations.push(kind + ':' + detail); }

// ---------------------------------------------------------------- DOM 替身
class TextNode {
    constructor(value) { this.nodeType = 3; this.data = String(value); this.parentNode = null; }
    get textContent() { return this.data; }
    set textContent(v) { this.data = String(v); }
}

class ClassList {
    constructor(el) { this.el = el; }
    items() {
        const c = this.el.getAttribute('class');
        return c ? String(c).split(/\s+/).filter(function (x) { return x.length > 0; }) : [];
    }
    add() {
        const items = this.items();
        for (let i = 0; i < arguments.length; i++) {
            const n = arguments[i];
            if (n && items.indexOf(n) < 0) items.push(n);
        }
        this.el.setAttribute('class', items.join(' '));
    }
    remove() {
        const args = Array.prototype.slice.call(arguments);
        const items = this.items().filter(function (x) { return args.indexOf(x) < 0; });
        this.el.setAttribute('class', items.join(' '));
    }
    contains(n) { return this.items().indexOf(n) >= 0; }
}

class Element {
    constructor(tagName, ns) {
        this.nodeType = 1;
        this.tagName = String(tagName).toUpperCase();
        this.namespaceURI = ns || 'http://www.w3.org/1999/xhtml';
        this.attributes = Object.create(null);
        this.childNodes = [];
        this.parentNode = null;
        this.style = {};
        this.value = '';
        this.placeholder = '';
        this._listeners = Object.create(null);
        this._classList = new ClassList(this);
    }
    get children() { return this.childNodes.filter(function (n) { return n.nodeType === 1; }); }
    get id() { return this.getAttribute('id') || ''; }
    set id(v) { this.setAttribute('id', v); }
    get className() { return this.getAttribute('class') || ''; }
    set className(v) { this.setAttribute('class', v); }
    get classList() { return this._classList; }
    appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
    setAttribute(name, value) {
        const n = String(name).toLowerCase();
        const v = String(value);
        this.attributes[n] = v;
        if (n.indexOf('on') === 0) note('event-handler-attribute', n);
        if (n === 'srcdoc') note('srcdoc-attribute', n);
        if ((n === 'href' || n === 'src' || n === 'action') && /^\s*javascript:/i.test(v)) note('javascript-url', n);
    }
    getAttribute(name) { const v = this.attributes[String(name).toLowerCase()]; return v === undefined ? null : v; }
    removeAttribute(name) { delete this.attributes[String(name).toLowerCase()]; }
    get textContent() { return this.childNodes.map(function (n) { return n.textContent; }).join(''); }
    set textContent(v) {
        this.childNodes.length = 0;
        if (v !== '' && v !== null && v !== undefined) this.appendChild(new TextNode(v));
    }
    get innerHTML() { return this._innerHTML || ''; }
    set innerHTML(v) {
        this._innerHTML = String(v);
        this.childNodes.length = 0;
        parseInto(this, String(v));
    }
    addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); }
    focus() {}
    get offsetWidth() { return 0; }
}

const VOID_TAGS = { area: 1, base: 1, br: 1, col: 1, embed: 1, hr: 1, img: 1, input: 1, link: 1, meta: 1, param: 1, source: 1, track: 1, wbr: 1 };
const DANGEROUS_TAGS = { script: 1, style: 1, iframe: 1, object: 1, embed: 1, link: 1, meta: 1, base: 1, form: 1 };
const TAG_RE = /<(\/)?([a-zA-Z][a-zA-Z0-9:_-]*)((?:\s+[^\s=/>]+(?:\s*=\s*(?:\x22[^\x22]*\x22|\x27[^\x27]*\x27|[^\s\x22\x27>]+))?)*)\s*(\/)?>/g;
const ATTR_RE = /([^\s=/>]+)(?:\s*=\s*(?:\x22([^\x22]*)\x22|\x27([^\x27]*)\x27|([^\s\x22\x27>]+)))?/g;

function parseInto(parent, html) {
    const stack = [parent];
    let last = 0;
    let m;
    TAG_RE.lastIndex = 0;
    while ((m = TAG_RE.exec(html)) !== null) {
        const chunk = html.slice(last, m.index);
        if (chunk.length > 0) stack[stack.length - 1].appendChild(new TextNode(chunk));
        last = TAG_RE.lastIndex;
        const closing = m[1] === '/';
        const tag = m[2].toLowerCase();
        if (closing) {
            for (let i = stack.length - 1; i >= 0; i--) {
                if (stack[i].tagName === tag.toUpperCase()) { stack.length = i; break; }
            }
            continue;
        }
        const el = new Element(tag);
        const attrsRaw = m[3] || '';
        ATTR_RE.lastIndex = 0;
        let a;
        while ((a = ATTR_RE.exec(attrsRaw)) !== null) {
            const name = a[1];
            if (!name) continue;
            let value = '';
            if (a[2] !== undefined) value = a[2];
            else if (a[3] !== undefined) value = a[3];
            else if (a[4] !== undefined) value = a[4];
            el.setAttribute(name, value);
        }
        if (DANGEROUS_TAGS[tag]) note('dangerous-element', tag);
        stack[stack.length - 1].appendChild(el);
        if (!VOID_TAGS[tag] && m[4] !== '/') stack.push(el);
    }
    const tail = html.slice(last);
    if (tail.length > 0) stack[stack.length - 1].appendChild(new TextNode(tail));
}

// ---------------------------------------------------------------- 遍历 / 摘要
function walk(el, fn) {
    fn(el);
    const kids = el.children;
    for (let i = 0; i < kids.length; i++) walk(kids[i], fn);
}

function dump(el) {
    let s = el.tagName.toLowerCase();
    const cls = el.getAttribute('class');
    if (cls) s += '.' + cls.split(/\s+/).join('.');
    const id = el.getAttribute('id');
    if (id) s += '#' + id;
    const kids = el.children;
    if (kids.length > 0) {
        s += '(';
        for (let i = 0; i < kids.length; i++) { if (i > 0) s += ','; s += dump(kids[i]); }
        s += ')';
    } else {
        const t = el.textContent;
        if (t.length > 0) s += '{' + t + '}';
    }
    return s;
}

function structOf(el) {
    let s = el.tagName.toLowerCase();
    const cls = el.getAttribute('class');
    if (cls) s += '.' + cls.split(/\s+/).join('.');
    const id = el.getAttribute('id');
    if (id) s += '#' + id;
    const kids = el.children;
    if (kids.length > 0) {
        s += '(';
        for (let i = 0; i < kids.length; i++) { if (i > 0) s += ','; s += structOf(kids[i]); }
        s += ')';
    }
    return s;
}

function cardSummary(board) {
    return board.children.map(function (card) {
        const head = card.children[0];
        const layout = card.children[1];
        return {
            head: head ? head.textContent : '',
            headClass: head ? (head.getAttribute('class') || '') : '',
            layoutClass: layout ? (layout.getAttribute('class') || '') : '',
            layoutId: layout ? (layout.getAttribute('id') || '') : '',
            cardClass: card.getAttribute('class') || '',
            order: card.style.order === undefined ? '' : String(card.style.order),
            childCount: layout ? layout.children.length : 0
        };
    });
}

function collectBy(el, attr) {
    const out = [];
    walk(el, function (e) {
        const v = e.getAttribute(attr);
        if (v !== null) out.push(v);
    });
    return out;
}

function collectAttrs(el) {
    const out = [];
    walk(el, function (e) {
        for (const k in e.attributes) out.push(k + '=' + e.attributes[k]);
    });
    return out;
}

function countTag(el, tag) {
    let n = 0;
    walk(el, function (e) { if (e.tagName === tag.toUpperCase()) n++; });
    return n;
}

// ---------------------------------------------------------------- 运行时
function makeRuntime(script) {
    const registry = Object.create(null);
    const fetchCalls = [];
    const wsInstances = [];
    let snapshotPayload = null;

    function ensure(id, tag) {
        const el = new Element(tag || 'div');
        el.setAttribute('id', id);
        registry[id] = el;
        return el;
    }

    function makeResponse(status, body) {
        return {
            status: status,
            ok: status >= 200 && status < 300,
            json: function () { return Promise.resolve(body); }
        };
    }

    function fakeFetch(url, options) {
        const opt = options || {};
        fetchCalls.push({
            url: String(url),
            method: opt.method || 'GET',
            headers: opt.headers || {},
            body: opt.body === undefined ? null : String(opt.body),
            credentials: opt.credentials || ''
        });
        const u = String(url);
        if (u.indexOf('/api/auth/login') === 0) return Promise.resolve(makeResponse(200, { csrf: 'csrf-token-value' }));
        if (u.indexOf('/api/auth/ticket') === 0) return Promise.resolve(makeResponse(200, { ticket: 'ticket-value' }));
        if (u.indexOf('/api/auth/logout') === 0) return Promise.resolve(makeResponse(204, null));
        if (u.indexOf('/api/snapshot') === 0) return Promise.resolve(makeResponse(200, snapshotPayload));
        return Promise.resolve(makeResponse(404, null));
    }

    function FakeWebSocket(url) {
        this.url = String(url);
        this.onopen = null; this.onmessage = null; this.onclose = null; this.onerror = null;
        this.closed = false;
        wsInstances.push(this);
    }
    FakeWebSocket.prototype.close = function () { this.closed = true; };

    const ctx = {
        console: console,
        setTimeout: function () { return 0; },
        clearTimeout: function () { },
        setInterval: function () { return 0; },
        clearInterval: function () { },
        fetch: fakeFetch,
        WebSocket: FakeWebSocket,
        location: { protocol: 'http:', host: '127.0.0.1:5000' }
    };
    vm.createContext(ctx);
    ctx.document = {
        createElement: function (tag) { return new Element(tag); },
        createElementNS: function (ns, tag) { return new Element(tag, ns); },
        getElementById: function (id) { return registry[id] || null; }
    };
    ensure('board', 'div'); ensure('btn-auth', 'div'); ensure('auth-modal', 'div');
    ensure('pwd-input', 'input'); ensure('status-dot', 'div'); ensure('status-text', 'span');
    ensure('sys-ip', 'span'); ensure('sys-uptime', 'span');

    vm.runInContext(script, ctx, { filename: 'page.js' });

    return {
        ctx: ctx,
        board: registry['board'],
        registry: registry,
        fetchCalls: fetchCalls,
        wsInstances: wsInstances,
        setSnapshot: function (p) { snapshotPayload = p; }
    };
}

function flush(n) {
    let p = Promise.resolve();
    for (let i = 0; i < n; i++) {
        p = p.then(function () { return new Promise(function (r) { setImmediate(r); }); });
    }
    return p;
}

// ---------------------------------------------------------------- 数据
const XSS_IMG = '<img src=x onerror=alert(1)>';
const XSS_SCRIPT = '<script>alert(1)</script>';
const XSS_ATTR = '\u0022 onmouseover=\u0022alert(2)';
const XSS_SINGLE = '\u0027 onfocus=\u0027alert(3)';
const XSS_GID = 'CPU\u0022 onload=\u0022alert(4)';
const XSS_STS = '1\u0022 onmouseover=\u0022alert(5)';
const PAYLOADS = [XSS_IMG, XSS_SCRIPT, XSS_ATTR, XSS_SINGLE];

const SYS = { ip: '10.0.0.1', port: 5000, uptime: '00:10:00' };

const BENIGN = [
    { k: 'CPU.Load', n: 'CPU 负载', gid: 'CPU', gn: '处理器', gidx: 0, v: '23', u: '%', pct: 23, sts: 0, primary: true },
    { k: 'CPU.Fan', n: 'CPU 风扇', gid: 'CPU', gn: '处理器', gidx: 0, v: '1200', u: 'RPM', pct: 50, sts: 0, primary: false },
    { k: 'MEM.Load', n: '内存使用', gid: 'MEM', gn: '内存', gidx: 1, v: '55', u: '%', pct: 55, sts: 1, primary: true },
    { k: 'NET.Down', n: '下载', gid: 'NET', gn: '网络', gidx: 2, v: '1.2', u: 'MB/s', pct: 10, sts: 0, primary: false },
    { k: 'DASH.Uptime', n: '运行时间', gid: 'DASH', gn: '仪表盘', gidx: 3, v: '01:00', u: '', pct: 0, sts: 0, primary: false }
];

const PAYLOAD_ITEMS = [
    { k: 'CPU.Load', n: XSS_SCRIPT, gid: 'CPU', gn: XSS_ATTR, gidx: 0, v: XSS_IMG, u: XSS_SINGLE, pct: 23, sts: 0, primary: true },
    { k: 'CPU.Fan', n: XSS_IMG, gid: 'CPU', gn: XSS_ATTR, gidx: 0, v: '1200', u: 'RPM', pct: 50, sts: 0, primary: false },
    { k: 'MEM.Load', n: XSS_SINGLE, gid: 'MEM', gn: XSS_SCRIPT, gidx: 1, v: '55', u: '%', pct: 55, sts: 1, primary: true },
    { k: 'NET.Down', n: XSS_IMG, gid: 'NET', gn: XSS_ATTR, gidx: 2, v: '1.2', u: 'MB/s', pct: 10, sts: 0, primary: false },
    { k: 'DASH.Uptime', n: XSS_SCRIPT, gid: 'DASH', gn: XSS_SINGLE, gidx: 3, v: '01:00', u: '', pct: 0, sts: 0, primary: false }
];

const HOSTILE_ITEMS = [
    { k: 'CPU.Load', n: XSS_GID, gid: XSS_GID, gn: XSS_GID, gidx: 0, v: '23', u: '%', pct: 23, sts: XSS_STS, primary: true }
];

// ---------------------------------------------------------------- 主流程
async function main() {
    const result = {};

    const r1 = makeRuntime(scriptNoAuth);
    await flush(20);
    r1.ctx.render(BENIGN);
    result.benignDump = dump(r1.board);
    result.benignStruct = structOf(r1.board);
    result.cards = cardSummary(r1.board);

    const r2 = makeRuntime(scriptNoAuth);
    await flush(20);
    r2.ctx.render(PAYLOAD_ITEMS);
    result.payloadDump = dump(r2.board);
    result.payloadStruct = structOf(r2.board);
    result.payloadText = r2.board.textContent;
    result.payloadScriptElements = countTag(r2.board, 'script');
    result.payloadAttributes = collectAttrs(r2.board);
    r2.ctx.render(PAYLOAD_ITEMS);
    result.refreshAfterPayloadStruct = structOf(r2.board);

    r1.ctx.render(BENIGN);
    result.refreshStruct = structOf(r1.board);

    const r3 = makeRuntime(scriptNoAuth);
    await flush(20);
    r3.ctx.render(HOSTILE_ITEMS);
    result.hostileText = r3.board.textContent;
    result.hostileIds = collectBy(r3.board, 'id');
    result.hostileClasses = collectBy(r3.board, 'class');
    result.hostileAttributes = collectAttrs(r3.board);

    const r4 = makeRuntime(scriptNoAuth);
    await flush(20);
    r4.setSnapshot({ sys: SYS, items: BENIGN });
    await r4.ctx.fetchSnapshot();
    result.httpDump = dump(r4.board);

    const r5 = makeRuntime(scriptNoAuth);
    await flush(20);
    const ws = r5.wsInstances[0];
    if (ws && typeof ws.onmessage === 'function') {
        ws.onmessage({ data: JSON.stringify({ sys: SYS, items: BENIGN }) });
    }
    result.wsDump = dump(r5.board);
    result.wsUrls = r5.wsInstances.map(function (w) { return w.url; });

    const r6 = makeRuntime(scriptAuth);
    await flush(20);
    r6.registry['pwd-input'].value = 'secret-pass';
    await r6.ctx.savePwd();
    await flush(20);
    result.fetchCalls = r6.fetchCalls;
    result.authWsUrls = r6.wsInstances.map(function (w) { return w.url; });

    result.violations = violations;
    result.payloads = PAYLOADS;
    process.stdout.write(JSON.stringify(result));
}

main().then(function () { process.exit(0); }, function (e) {
    process.stderr.write(String(e && e.stack ? e.stack : e));
    process.exit(1);
});
";
    }

    /// <summary>
    /// LM-004（S2.1）：网页端 DOM 文本化 + 前端认证协议迁移（契约 W1/W3/W4/W10/W13/W13.5）。
    ///
    /// 测试载体（写死，不允许退化成"人工检查"）：
    ///  <list type="number">
    ///   <item>
    ///   <b>(a) 结构级断言</b>（本文件上半部分，纯 C#，无外部依赖 → <b>CI 门禁</b>）：
    ///   对 <c>WebPageContent.IndexHtml</c> 里的脚本做源码级不变量断言 ——
    ///   禁止任何"字符串拼 HTML 再进 DOM"的写法（innerHTML / insertAdjacentHTML /
    ///   outerHTML / document.write / eval / new Function），
    ///   要求外部值（分组名 / 监控名 / 取值）只能经 <c>text(...)</c> 包装后由
    ///   <c>textContent</c> 承载，动态分组标识必须先过受限字符集
    ///   <c>[A-Za-z0-9_-]</c> 才能进 id / class，且页面里不再存在任何动态选择器。
    ///   这一层覆盖 <b>D1</b>（载荷只能作为文本）、<b>D2</b>（外部数据进不了标记解析器 ⇒
    ///   结构不可能被外部数据改变）、<b>D3</b>（外部数据进不了标记解析器 ⇒ 不可能产生
    ///   可执行构造）、<b>D4</b>（受限字符集 + 无动态选择器）、<b>D6</b>（两条通道共用
    ///   同一渲染入口）、<b>D7</b>（排序 / class 逻辑未变）。
    ///  </item>
    ///   <item>
    ///   <b>(b) 行为级断言</b>（本文件下半部分，node + 内置最小 DOM 替身）：
    ///   把网页脚本真的跑一遍，用最小 DOM 替身（含一个会如实解析标记的
    ///   <c>innerHTML</c> setter）检查最终 DOM：载荷是否只落在文本节点、
    ///   DOM 结构是否与正常数据逐节点同形、有没有出现 script / on* 事件属性、
    ///   两条数据通道的渲染结果是否一致、正常数据的布局与排序是否保持不变。
    ///   这一层补足 (a) 无法证明的 <b>D2/D3/D7 的"实际执行结果"</b>，
    ///   并额外覆盖前端协议迁移的行为面（W10 / W13.5）。
    ///   CI（windows-latest、无 Node、无浏览器）拿不到这一层，因此 (a) 始终保留为门禁。
    ///  </item>
    ///  </list>
    /// </summary>
    public class WebDomSanitizationTests
    {
        // ================================================================
        // 载荷与常量
        // ================================================================

        /// <summary>D1/D3 载荷：img + onerror。</summary>
        private const string PayloadImg = "<img src=x onerror=alert(1)>";

        /// <summary>D1/D3 载荷：script 标签。</summary>
        private const string PayloadScript = "<script>alert(1)</script>";

        /// <summary>D1/D4 载荷：双引号 + 事件属性（试图逃出属性上下文）。</summary>
        private const string PayloadAttr = "\" onmouseover=\"alert(2)";

        /// <summary>D1 载荷：单引号。</summary>
        private const string PayloadSingle = "' onfocus='alert(3)";

        /// <summary>D4 载荷：试图逃出 id / 选择器上下文的分组标识。</summary>
        private const string PayloadGid = "CPU\" onload=\"alert(4)";

        /// <summary>动态 id / class 后缀允许的字符集（D4）。</summary>
        private static readonly Regex SafeIdPattern = new Regex("^[A-Za-z0-9_-]*$", RegexOptions.Compiled);

        // ================================================================
        // 辅助
        // ================================================================

        /// <summary>取出页面里的脚本（并把占位符替换成 false）。</summary>
        private static string ExtractScript(bool authRequired)
        {
            string html = WebPageContent.IndexHtml;
            const string open = "<script>";
            const string close = "</script>";
            int start = html.IndexOf(open, StringComparison.Ordinal);
            int end = html.IndexOf(close, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "页面里应当恰好有一段 <script>");
            Assert.True(start == html.LastIndexOf(open, StringComparison.Ordinal), "页面里应当只有一段 <script>");
            string js = html.Substring(start + open.Length, end - start - open.Length);
            return js.Replace("{{AUTH_REQUIRED}}", authRequired ? "true" : "false");
        }

        /// <summary>
        /// 去掉注释（行注释 + 块注释），只留代码 —— 源码级断言不能被注释里的关键字骗到。
        /// </summary>
        private static string StripComments(string source)
        {
            StringBuilder sb = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(source.Length, i + 2);
                    continue;
                }
                if (source[i] == '\'' || source[i] == '"')
                {
                    char quote = source[i];
                    sb.Append(source[i]);
                    i++;
                    while (i < source.Length)
                    {
                        sb.Append(source[i]);
                        if (source[i] == '\\') { i++; if (i < source.Length) { sb.Append(source[i]); i++; } continue; }
                        if (source[i] == quote) { i++; break; }
                        i++;
                    }
                    continue;
                }
                sb.Append(source[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>统计子串出现次数。</summary>
        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// 断言"某个外部数据表达式"的每一次出现都在 <c>text(...)</c> 包装里。
        ///
        /// 这是"外部数据只能作为文本节点进入 DOM"这条规则的**精确**静态表达：
        /// 只要有一处把 <c>item.n</c> 之类直接拼进字符串（或直接赋给 innerHTML），
        /// 计数就相等，用例立刻失败。
        /// </summary>
        private static void AssertAlwaysTextified(string code, string expression)
        {
            int total = CountOccurrences(code, expression);
            int wrapped = CountOccurrences(code, "text(" + expression + ")");
            Assert.True(total > 0, "表达式 " + expression + " 在脚本里应当出现（否则这条断言失去意义）");
            Assert.True(wrapped == total,
                "外部数据 " + expression + " 有 " + total + " 处出现，但只有 " + wrapped +
                " 处被 text(...) 包装 —— 存在未经文本化的使用");
        }

        // ================================================================
        // A. 结构级断言（CI 门禁，不依赖任何外部运行时）
        // ================================================================

        [Fact]
        public void 脚本中不存在把外部数据拼进HTML的写法()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ 核心不变量：页面里**完全不存在**"字符串 → HTML 解析器"的通道。
            //   外部数据因此不可能改变 DOM 结构、也不可能产生可执行构造（D2 / D3）。
            Assert.DoesNotContain("innerHTML", code, StringComparison.Ordinal);
            Assert.DoesNotContain("outerHTML", code, StringComparison.Ordinal);
            Assert.DoesNotContain("insertAdjacentHTML", code, StringComparison.Ordinal);
            Assert.DoesNotContain("document.write", code, StringComparison.Ordinal);
            Assert.DoesNotContain("srcdoc", code, StringComparison.Ordinal);
            Assert.DoesNotContain("eval(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("new Function", code, StringComparison.Ordinal);
            Assert.DoesNotContain("setAttribute('on", code, StringComparison.Ordinal);

            // 唯一允许的写入通道是文本节点。
            Assert.True(CountOccurrences(code, ".textContent") >= 12,
                "外部数据必须经 textContent 进入 DOM，实际只找到 " + CountOccurrences(code, ".textContent") + " 处");

            // ★ 外部值必须逐个被 text(...) 包装（分组名 / 监控名 / 取值 / 单位 / 系统信息）。
            AssertAlwaysTextified(code, "grp.name");
            AssertAlwaysTextified(code, "grp.core.n");
            AssertAlwaysTextified(code, "grp.core.v");
            AssertAlwaysTextified(code, "grp.core.u");
            AssertAlwaysTextified(code, "item.n");
            AssertAlwaysTextified(code, "item.v");
            AssertAlwaysTextified(code, "item.u");
            AssertAlwaysTextified(code, "payload.sys.ip");
            AssertAlwaysTextified(code, "payload.sys.port");
            AssertAlwaysTextified(code, "payload.sys.uptime");
        }

        [Fact]
        public void 动态分组标识经过受限字符集清洗_且页面里没有动态选择器()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ 受限字符集：只保留 [A-Za-z0-9_-]，其余字符确定性替换为 '_'（D4）。
            Assert.Contains("/[^A-Za-z0-9_-]/g", code, StringComparison.Ordinal);
            Assert.Contains("function safeToken(", code, StringComparison.Ordinal);

            // ★ 页面里不再有任何"按外部值拼选择器"的做法 —— 元素引用一律直接持有。
            Assert.DoesNotContain("querySelector", code, StringComparison.Ordinal);
            Assert.DoesNotContain("getElementById(`", code, StringComparison.Ordinal);
            Assert.DoesNotContain("${gid}", code, StringComparison.Ordinal);
            Assert.DoesNotContain("${item.", code, StringComparison.Ordinal);

            // ★ 所有动态 id 都由清洗后的 key 拼出（而不是原始 gid）。
            int idAssignments = CountOccurrences(code, ".id = ");
            Assert.True(idAssignments >= 5, "动态 id 数量异常：" + idAssignments);
            int idFromKey = Regex.Matches(code, @"\.id\s*=\s*'[^']*'\s*\+\s*key\s*;").Count;
            Assert.True(idAssignments == idFromKey,
                "存在不是由清洗后的 key 拼出的动态 id：" + idAssignments + " 处赋值里只有 " + idFromKey + " 处合规");

            // ★ 动态 class 后缀同样过受限字符集（状态值也来自服务端）。
            Assert.Contains("'card cs-' + safeToken(", code, StringComparison.Ordinal);
            Assert.Contains("'big-item is-' + safeToken(", code, StringComparison.Ordinal);
            Assert.Contains("'d-row is-' + safeToken(", code, StringComparison.Ordinal);
            Assert.Contains("'ring-wrap is-' + safeToken(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("`card cs-${", code, StringComparison.Ordinal);
            Assert.DoesNotContain("`big-item is-${", code, StringComparison.Ordinal);
            Assert.DoesNotContain("`d-row is-${", code, StringComparison.Ordinal);
        }

        [Fact]
        public void 外部数据当键的映射不使用原型链()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // gid / item key 都来自服务端；若用普通对象，__proto__ 之类的键会撞上原型属性。
            Assert.True(CountOccurrences(code, "Object.create(null)") >= 4,
                "groups / cards / rows / groupKeys 都必须用无原型对象，实际找到 " +
                CountOccurrences(code, "Object.create(null)") + " 处");
        }

        [Fact]
        public void 口令不再进入URL或任何长期存储()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ 口令绝不进 URL（W13 / W13.5）。
            Assert.DoesNotContain("?pwd=", code, StringComparison.Ordinal);
            Assert.DoesNotContain("&pwd=", code, StringComparison.Ordinal);
            Assert.DoesNotContain("pwd=", code, StringComparison.Ordinal);

            // ★ 口令绝不进任何长期存储（W10 / W13）。
            Assert.DoesNotContain("localStorage.setItem", code, StringComparison.Ordinal);
            Assert.DoesNotContain("localStorage.getItem", code, StringComparison.Ordinal);
            Assert.DoesNotContain("sessionStorage", code, StringComparison.Ordinal);
            Assert.DoesNotContain("document.cookie", code, StringComparison.Ordinal);

            // ★ 升级 / 登出流程必须**主动删除**历史残留的明文口令（W10）。
            Assert.Contains("localStorage.removeItem('ws_pwd')", code, StringComparison.Ordinal);
            Assert.Contains("function purgeLegacyPassword()", code, StringComparison.Ordinal);
            Assert.True(CountOccurrences(code, "purgeLegacyPassword();") >= 3,
                "升级、登出、会话失效三条路径都必须清掉历史口令");
        }

        [Fact]
        public void 认证协议符合契约W13_5端点形态()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ 三个端点 + 响应字段形态必须与契约逐字一致（前后端并行实现的唯一依据）。
            Assert.Contains("'/api/auth/login'", code, StringComparison.Ordinal);
            Assert.Contains("'/api/auth/ticket'", code, StringComparison.Ordinal);
            Assert.Contains("'/api/auth/logout'", code, StringComparison.Ordinal);
            Assert.Contains("'X-LiteMonitor-CSRF'", code, StringComparison.Ordinal);
            Assert.Contains("JSON.stringify({ password: password })", code, StringComparison.Ordinal);
            Assert.Contains("data.csrf", code, StringComparison.Ordinal);
            Assert.Contains("data.ticket", code, StringComparison.Ordinal);

            // ★ 快照取数不带任何口令参数，只靠 Cookie 携带会话。
            Assert.Contains("fetch('/api/snapshot', { credentials: 'same-origin' })", code, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/snapshot?", code, StringComparison.Ordinal);

            // ★ WebSocket 握手必须带一次性票据。
            Assert.Contains("'/ws?ticket=' + encodeURIComponent(ticket)", code, StringComparison.Ordinal);
            Assert.DoesNotContain("location.host + qs", code, StringComparison.Ordinal);

            // ★ 改变状态的请求必须带 CSRF 头。
            Assert.Contains("function authHeaders(", code, StringComparison.Ordinal);
            Assert.Contains("h['X-LiteMonitor-CSRF'] = csrfToken;", code, StringComparison.Ordinal);
            Assert.True(CountOccurrences(code, "headers: authHeaders(") >= 2,
                "取票据与注销两个端点都必须带 CSRF 头");
        }

        [Fact]
        public void 会话失效时清空内存CSRF并重新弹出登录框()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ 401 → 清除内存里的 CSRF 令牌 + 重新弹出登录框，不静默失败（W10 / 任务要求 7）。
            Assert.Contains("function onSessionLost()", code, StringComparison.Ordinal);
            int lost = code.IndexOf("function onSessionLost()", StringComparison.Ordinal);
            Assert.True(lost >= 0, "缺少 onSessionLost");
            string lostBody = code.Substring(lost, Math.Min(900, code.Length - lost));
            Assert.Contains("csrfToken = null;", lostBody, StringComparison.Ordinal);
            Assert.Contains("showAuth();", lostBody, StringComparison.Ordinal);

            // ★ 两条 401 路径都要走 onSessionLost（快照取数 + 取票据）。
            Assert.Contains("if (res.status === 401) { onSessionLost(); return null; }", code, StringComparison.Ordinal);
            Assert.Contains("if (res.status === 401 || res.status === 403) { onSessionLost(); return null; }", code, StringComparison.Ordinal);
        }

        [Fact]
        public void 两条数据通道共用同一渲染入口()
        {
            string code = StripComments(ExtractScript(authRequired: false));

            // ★ D6：HTTP 快照与 WebSocket 广播必须汇到同一个 applySnapshot → render。
            Assert.Contains("function applySnapshot(", code, StringComparison.Ordinal);
            Assert.Contains("if (payload.items) render(payload.items);", code, StringComparison.Ordinal);
            Assert.True(CountOccurrences(code, "applySnapshot(") >= 3,
                "applySnapshot 至少要被 WS 处理器、HTTP 取数、启动流程三处调用，实际 " +
                CountOccurrences(code, "applySnapshot(") + " 处");
            Assert.Contains("applySnapshot(JSON.parse(event.data))", code, StringComparison.Ordinal);
            Assert.Contains("applySnapshot(data);", code, StringComparison.Ordinal);
        }

        // ================================================================
        // B. 行为级断言（node + 最小 DOM 替身）
        // ================================================================

        [NodeScriptFact]
        public void 载荷只作为文本显示_不改变DOM结构_不触发脚本()
        {
            HarnessResult r = HarnessResult.Run();

            // ★ D1：载荷只作为**文本**出现。
            //   先确认行为级用例用的确实是本文件声明的那几个载荷（防止两边悄悄跑偏）。
            Assert.Equal(new[] { PayloadImg, PayloadScript, PayloadAttr, PayloadSingle }, r.Payloads);
            foreach (string payload in r.Payloads)
            {
                Assert.Contains(payload, r.PayloadText, StringComparison.Ordinal);
            }

            // ★ D1/D4：载荷绝不进入任何属性（id / class / 其它）。
            foreach (string attr in r.PayloadAttributes)
            {
                Assert.DoesNotContain("<", attr, StringComparison.Ordinal);
                Assert.DoesNotContain(">", attr, StringComparison.Ordinal);
                Assert.DoesNotContain("onerror", attr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("onload", attr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("onmouseover", attr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("javascript:", attr, StringComparison.OrdinalIgnoreCase);
            }

            // ★ D2：DOM 结构与正常数据**逐节点同形**（标签、class、id、层级全一致）。
            Assert.Equal(r.BenignStruct, r.PayloadStruct);

            // ★ D3：没有产生任何可执行构造（script 元素 / on* 事件属性 / javascript: URL）。
            Assert.Empty(r.Violations);
            Assert.Equal(0, r.PayloadScriptElements);

            // ★ D5：五种场景都在同一棵树上成立 ——
            //   普通卡片(layout-std) / 大小卡片(layout-big) / DASH(layout-dash)
            //   / 首次创建 / 持续刷新。
            Assert.Contains("layout-std", r.BenignDump, StringComparison.Ordinal);
            Assert.Contains("layout-big", r.BenignDump, StringComparison.Ordinal);
            Assert.Contains("layout-dash", r.BenignDump, StringComparison.Ordinal);
            Assert.Equal(r.BenignStruct, r.RefreshStruct);
            Assert.Equal(r.BenignStruct, r.RefreshAfterPayloadStruct);
        }

        [NodeScriptFact]
        public void 动态分组标识不逃逸出属性与选择器上下文()
        {
            HarnessResult r = HarnessResult.Run();

            // ★ D4：恶意分组标识清洗后只可能落在 [A-Za-z0-9_-]。
            Assert.NotEmpty(r.HostileIds);
            foreach (string id in r.HostileIds)
            {
                Assert.Matches(SafeIdPattern, id);
                Assert.DoesNotContain("\"", id, StringComparison.Ordinal);
                Assert.DoesNotContain("'", id, StringComparison.Ordinal);
                Assert.DoesNotContain("<", id, StringComparison.Ordinal);
            }
            foreach (string cls in r.HostileClasses)
            {
                // class 是"若干个令牌"的组合：逐个令牌检查，而不是整串检查。
                foreach (string token in cls.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.Matches(SafeIdPattern, token);
                }
            }
            foreach (string attr in r.HostileAttributes)
            {
                Assert.DoesNotContain("<", attr, StringComparison.Ordinal);
                Assert.DoesNotContain(">", attr, StringComparison.Ordinal);
                Assert.DoesNotContain("\"", attr, StringComparison.Ordinal);
            }

            // ★ 恶意分组标识本身只作为文本出现。
            Assert.Contains(PayloadGid, r.HostileText, StringComparison.Ordinal);

            // ★ 而它**绝不**出现在任何 id / class / 属性里（只以清洗后的形式出现）。
            foreach (string id in r.HostileIds) Assert.DoesNotContain(PayloadGid, id, StringComparison.Ordinal);
            foreach (string cls in r.HostileClasses) Assert.DoesNotContain(PayloadGid, cls, StringComparison.Ordinal);
            foreach (string attr in r.HostileAttributes) Assert.DoesNotContain(PayloadGid, attr, StringComparison.Ordinal);
        }

        [NodeScriptFact]
        public void HTTP与WebSocket两条数据通道渲染结果一致()
        {
            HarnessResult r = HarnessResult.Run();

            // ★ D6：两条通道的数据进的是同一个渲染入口 ⇒ 最终 DOM 完全一致。
            Assert.Equal(r.HttpDump, r.WsDump);
            Assert.NotEqual(0, r.HttpDump.Length);
        }

        [NodeScriptFact]
        public void 正常数据的布局与排序不变()
        {
            HarnessResult r = HarnessResult.Run();

            // ★ D7：布局与排序的金样（分组顺序、CSS order、卡片 class、布局类型、圆环与行的层级）。
            Assert.Equal(HarnessResult.GoldenDump, r.BenignDump);

            Assert.Equal(4, r.Cards.Count);
            Assert.Equal("处理器", r.Cards[0].Head);
            Assert.Equal("layout-std", r.Cards[0].LayoutClass);
            Assert.Equal("0", r.Cards[0].Order);
            Assert.Equal("card cs-0", r.Cards[0].CardClass);

            Assert.Equal("内存", r.Cards[1].Head);
            Assert.Equal("layout-std", r.Cards[1].LayoutClass);
            Assert.Equal("1", r.Cards[1].Order);
            Assert.Equal("card cs-1", r.Cards[1].CardClass);

            Assert.Equal("网络", r.Cards[2].Head);
            Assert.Equal("layout-big", r.Cards[2].LayoutClass);
            Assert.Equal("2", r.Cards[2].Order);

            Assert.Equal("仪表盘", r.Cards[3].Head);
            Assert.Equal("layout-dash", r.Cards[3].LayoutClass);
            Assert.Equal("-999", r.Cards[3].Order);
            Assert.Equal("card full-width", r.Cards[3].CardClass);
        }

        [NodeScriptFact]
        public void 前端协议行为_口令只在请求体_票据进WS地址_快照不带口令参数()
        {
            HarnessResult r = HarnessResult.Run();

            // ★ 登录：口令只出现在请求体里，URL 里没有。
            FetchCall login = r.FetchCalls.Find(c => c.Url.StartsWith("/api/auth/login", StringComparison.Ordinal));
            Assert.NotNull(login);
            Assert.Equal("POST", login.Method);
            Assert.DoesNotContain("pwd", login.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-pass", login.Url, StringComparison.Ordinal);
            Assert.Contains("secret-pass", login.Body, StringComparison.Ordinal);

            // ★ 取票据：必须带 CSRF 头。
            FetchCall ticket = r.FetchCalls.Find(c => c.Url.StartsWith("/api/auth/ticket", StringComparison.Ordinal));
            Assert.NotNull(ticket);
            Assert.Equal("POST", ticket.Method);
            Assert.True(ticket.Headers.ContainsKey("X-LiteMonitor-CSRF"), "取票据必须带 X-LiteMonitor-CSRF 头");
            Assert.Equal("csrf-token-value", ticket.Headers["X-LiteMonitor-CSRF"]);

            // ★ 快照：URL 上没有任何口令参数。
            FetchCall snapshot = r.FetchCalls.Find(c => c.Url.StartsWith("/api/snapshot", StringComparison.Ordinal));
            Assert.NotNull(snapshot);
            Assert.Equal("/api/snapshot", snapshot.Url);
            Assert.DoesNotContain("pwd", snapshot.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-pass", snapshot.Url, StringComparison.Ordinal);

            // ★ 全部请求的 URL 都不得含口令。
            foreach (FetchCall call in r.FetchCalls)
            {
                Assert.DoesNotContain("secret-pass", call.Url, StringComparison.Ordinal);
                Assert.DoesNotContain("pwd", call.Url, StringComparison.OrdinalIgnoreCase);
            }

            // ★ WebSocket：地址里是一次性票据，不是口令。
            Assert.NotEmpty(r.WsUrls);
            Assert.Contains("ticket=ticket-value", r.WsUrls[0], StringComparison.Ordinal);
            Assert.DoesNotContain("secret-pass", r.WsUrls[0], StringComparison.Ordinal);
            Assert.DoesNotContain("pwd", r.WsUrls[0], StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================
        // 行为级用例的数据结构
        // ================================================================

        private sealed class FetchCall
        {
            public string Url { get; set; } = "";
            public string Method { get; set; } = "";
            public string Body { get; set; } = "";
            public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        }

        private sealed class CardInfo
        {
            public string Head { get; set; } = "";
            public string HeadClass { get; set; } = "";
            public string LayoutClass { get; set; } = "";
            public string LayoutId { get; set; } = "";
            public string CardClass { get; set; } = "";
            public string Order { get; set; } = "";
            public int ChildCount { get; set; }
        }

        private sealed class HarnessResult
        {
            public string BenignDump { get; set; } = "";
            public string BenignStruct { get; set; } = "";
            public string PayloadDump { get; set; } = "";
            public string PayloadStruct { get; set; } = "";
            public string PayloadText { get; set; } = "";
            public List<string> PayloadAttributes { get; set; } = new List<string>();
            public int PayloadScriptElements { get; set; }
            public string RefreshStruct { get; set; } = "";
            public string RefreshAfterPayloadStruct { get; set; } = "";
            public string HostileText { get; set; } = "";
            public List<string> HostileIds { get; set; } = new List<string>();
            public List<string> HostileClasses { get; set; } = new List<string>();
            public List<string> HostileAttributes { get; set; } = new List<string>();
            public string HttpDump { get; set; } = "";
            public string WsDump { get; set; } = "";
            public List<string> Violations { get; set; } = new List<string>();
            public List<string> Payloads { get; set; } = new List<string>();
            public List<FetchCall> FetchCalls { get; set; } = new List<FetchCall>();
            public List<string> WsUrls { get; set; } = new List<string>();
            public List<CardInfo> Cards { get; set; } = new List<CardInfo>();

            /// <summary>
            /// 正常数据的布局与排序金样（D7）。
            /// 由行为级用例真实渲染 <c>BENIGN</c> 数据集得到：
            /// 分组顺序（处理器 → 内存 → 网络 → 仪表盘）、每张卡片的 class 与 CSS order、
            /// 布局类型（layout-std / layout-big / layout-dash）、圆环与行的层级、取值文本。
            /// </summary>
            public const string GoldenDump =
                @"div#board(div.card.cs-0(div.card-head{处理器},div.layout-std(div.ring-container(div.ring-wrap.is-0#rw-CPU(svg.ring-svg(path.ring-bg,path.ring-val#rp-CPU),div.ring-data(div.rd-val#rv-CPU{23},div.rd-unit#ru-CPU{%})),div.rd-name#r" +
                @"n-CPU{CPU 负载}),div.detail-list#list-CPU(div.d-row.is-0(div.d-info(span.d-lbl{CPU 风扇},div(span.d-val-box{1200},span.d-unit{RPM})),div.d-bar-bg(div.d-bar-fill))))),div.card.cs-1(div.card-head{内存},div.layout-std(div.ring-contai" +
                @"ner(div.ring-wrap.is-1#rw-MEM(svg.ring-svg(path.ring-bg,path.ring-val#rp-MEM),div.ring-data(div.rd-val#rv-MEM{55},div.rd-unit#ru-MEM{%})),div.rd-name#rn-MEM{内存使用}),div.detail-list#list-MEM)),div.card.cs-0(div.card-head{网络},d" +
                @"iv.layout-big#big-NET(div.big-item.is-0(div.big-lbl{下载},div.big-val{1.2},div.big-unit{MB/s}))),div.card.full-width(div.card-head{仪表盘},div.layout-dash#dash-DASH(div.dash-item(div.dash-lbl{运行时间},div.dash-val.is-0{01:00}))))";

            public static HarnessResult Run()
            {
                string json = NodeRunner.RunHarness(ExtractScript(authRequired: false), ExtractScript(authRequired: true));
                HarnessResult? result = JsonSerializer.Deserialize<HarnessResult>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.True(result != null, "行为级用例没有拿到有效结果：" + json);
                return result!;
            }
        }
    }
}
