namespace LiteMonitor.src.WebServer
{
    public static class WebPageContent
    {
        /// <summary>
        /// 主题 CSS 变量块的占位符（A11 挂载点）。
        /// 由 <c>WebSessionManager</c> 在组装 HTML 时替换成
        /// <c>WebThemeCss.BuildCssVariables(...)</c> 的结果。
        /// </summary>
        public const string ThemeCssPlaceholder = "{{THEME_CSS}}";

        public static string GetAppIconBase64()
        {
            if (_cachedFaviconBase64 != null) return _cachedFaviconBase64;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                if (icon != null)
                {
                    using var ms = new System.IO.MemoryStream();
                    icon.ToBitmap().Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    _cachedFaviconBase64 = Convert.ToBase64String(ms.ToArray());
                    return _cachedFaviconBase64;
                }
            }
            catch { }
            return ""; 
        }
        private static string _cachedFaviconBase64 = null;

        public const string IndexHtml = @"
<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    {{FAVICON}}
    <title>LiteMonitor++ WebServer</title>
    <style>
        :root {
            --bg: #09090b;
            --card: #141417;
            --border: #27272a;
            --text-main: #f4f4f5;
            --text-sub: #71717a;
            
            /* 基础三色 */
            --c-0: #10b981; /* Green */
            --c-1: #f59e0b; /* Orange */
            --c-2: #ef4444; /* Red */
        }

        /* --- 运行时主题变量（挂载点：由 WebThemeCss 在服务端注入） ---
           UI 层发布颜色快照后，这里会被替换成一段 :root 变量块。
           未发布快照时保持上面那组默认值 → 页面外观与注入前完全一致。 */
        {{THEME_CSS}}

        body { 
            margin: 0; padding: 20px; 
            background: var(--bg); color: var(--text-main); 
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
        }

        /* --- Header --- */
        .header {
            max-width: 1200px; margin: 0 auto 30px;
            display: flex; justify-content: space-between; align-items: flex-end;
            padding-bottom: 20px; border-bottom: 1px solid var(--border);
            gap: 20px;
        }
        .brand { 
            font-size: 1.6rem; font-weight: 800; letter-spacing: 1px; 
            white-space: nowrap; flex-shrink: 0; line-height: 1;
        }
        .brand span { color: var(--c-0); }
        
        .sys-info { 
            font-family: 'Consolas', monospace; color: var(--text-sub); 
            font-size: 0.9rem; 
            display: flex; gap: 10px; flex-wrap: wrap; justify-content: flex-end;
        }
        .tag { 
            background: #1f1f22; 
            padding: 5px 12px; 
            border-radius: 6px; 
            border: 1px solid #333;
            display: flex; align-items: center; 
            white-space: nowrap; 
        }
        .tag b { color: var(--text-main); margin-right: 6px; opacity: 0.5; font-weight: normal; }

        /* --- Grid --- */
        .dashboard {
            max-width: 1200px; margin: 0 auto;
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(min(450px, 100%), 1fr));
            gap: 24px;
        }

        /* --- Card --- */
        .card {
            background: var(--card); 
            border: 1px solid var(--border); /* 始终保持基础边框，不再变色 */
            border-radius: 16px; padding: 24px;
            display: flex; flex-direction: column; gap: 20px;
            position: relative; overflow: hidden;
            box-shadow: 0 4px 20px rgba(0,0,0,0.2);
            /* 移除 transition border-color，因为不再改边框 */
        }
        
        /* 顶部彩色条：指示颜色的主要元素 */
        .card::before {
            content: ''; position: absolute; top: 0; left: 0; right: 0; height: 3px;
            background: var(--card-color, var(--border));
            box-shadow: 0 0 15px var(--card-color, transparent);
            opacity: 0.8;
            transition: background 0.3s;
        }
        .card-head { 
            font-size: 1.1rem; font-weight: 700; color: var(--text-sub); 
            text-transform: uppercase; letter-spacing: 1px;
        }

        /* --- Layout Standard (CPU/GPU) --- */
        .layout-std { display: flex; gap: 30px; align-items: center; }
        
        .ring-wrap { 
            position: relative; width: 130px; height: 130px; flex-shrink: 0; 
        }
        .ring-container {
            display: flex; flex-direction: column; align-items: center;
            flex-shrink: 0;
        }
        .ring-svg { transform: rotate(-90deg); width: 100%; height: 100%; }
        .ring-bg { fill: none; stroke: #27272a; stroke-width: 3.5; }
        
        .ring-val { 
            fill: none; stroke: var(--item-color, var(--c-0)); 
            stroke-width: 3.5; stroke-linecap: round; 
            transition: stroke-dasharray 0.6s ease; 
        }
        .ring-data { 
            position: absolute; inset: 0; 
            display: flex; flex-direction: column; justify-content: center; align-items: center; 
        }
        .rd-name { 
            font-size: 0.9rem; color: var(--text-sub); margin-top: 8px; 
            font-weight: 600; letter-spacing: 1px;
            text-align: center;
        }
        .rd-val { font-size: 2.2rem; font-weight: 800; line-height: 1; margin-top: 10px;}
        .rd-unit { font-size: 1rem; color: var(--text-sub); margin-top: 2px; }

        .detail-list { flex: 1; display: flex; flex-direction: column; justify-content: center; gap: 10px; }
        .d-row { display: flex; flex-direction: column; gap: 4px; }
        
        .d-info { display: flex; justify-content: space-between; align-items: baseline; }
        .d-lbl { font-size: 1rem; color: var(--text-sub); }
        
        .d-val-box { 
            font-family: 'Consolas', monospace; 
            font-weight: 700; 
            font-size: 1.3rem; 
            color: var(--item-color, #fff);
            white-space: nowrap; /* 增加这一行 */
        }
        .d-unit { font-size: 1rem; color: var(--text-sub); font-weight: normal; margin-left: 2px; }
        
        .d-bar-bg { height: 6px; background: #27272a; border-radius: 3px; overflow: hidden; width: 100%; }
        .d-bar-fill { height: 100%; width: 0%; border-radius: 3px; transition: width 0.4s; background: var(--item-color, var(--c-0)); }

        /* --- Layout Big (Net/Disk) --- */
        .layout-big { display: flex; text-align: center; align-items: center; height: 100%; }
        .big-item { flex: 1; display: flex; flex-direction: column; gap: 5px; position: relative; }
        .big-item:first-child::after {
            content: ''; position: absolute; right: 0; top: 10%; bottom: 10%; width: 1px; background: var(--border);
        }
        .big-lbl { font-size: 1rem; color: var(--text-sub); font-weight: 600; }
        .big-val { font-size: 2.6rem; font-weight: 900; line-height: 1.1; font-family: 'Consolas', monospace; color: var(--item-color, #fff); }
        .big-unit { font-size: 1.1rem; color: var(--item-color, var(--text-sub)); font-weight: 700; opacity: 0.8; }

        /* --- Layout Dash (Info) --- */
        .full-width { grid-column: 1 / -1; }
        .layout-dash { 
            display: grid; 
            /* PC端优化：增加最小宽度到 200px，防止内容挤压换行 */
            grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
            gap: 16px; 
            align-items: stretch;
        }
        .dash-item {
            display: flex; flex-direction: column; 
            background: rgba(255,255,255,0.03); 
            padding: 12px 15px; border-radius: 10px;
            /* 移除固定宽度，交给 Grid 控制 */
            border: 1px solid var(--border);
            overflow: hidden;
        }
        .dash-lbl { font-size: 0.85rem; color: var(--text-sub); margin-bottom: 6px; letter-spacing: 0.5px; }
        .dash-val { 
            /* 字体大小自适应：最小 0.9rem，最大 1.2rem */
            font-size: clamp(0.9rem, 3.5vw, 1.2rem); 
            color: var(--text-main); font-weight: 600; 
            /* ★★★ 修复：DASH 值包含中文，使用微软雅黑优化显示 ★★★ */
            font-family: 'Microsoft YaHei UI', 'Microsoft YaHei', 'Segoe UI', Roboto, sans-serif;
            /* ★★★ 修复：防止数字/单词被强制截断，允许在必要时换行 ★★★ */
            white-space: pre-wrap; 
            word-break: break-word; /* 单词完整时不拆分 */
            overflow-wrap: anywhere; /* 只有长字符串（如IP）放不下才强制拆分 */
            line-height: 1.2;
        }
        /* ★★★ 修复：DASH 监控项变色 (同步主界面) ★★★ */
         .dash-val.is-0 { color: var(--c-0); } /* Normal (Green) */
         .dash-val.is-1 { color: var(--c-1); }
         .dash-val.is-2 { color: var(--c-2); }
         
         /* Auth Modal */
        .modal-overlay { display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.8); z-index: 999; justify-content: center; align-items: center; }
        .modal { background: var(--card); padding: 25px; border-radius: 12px; border: 1px solid var(--border); width: 300px; display: flex; flex-direction: column; gap: 15px; }
        .modal h3 { margin: 0; font-size: 1.2rem; }
        .inp { background: #000; border: 1px solid var(--border); color: #fff; padding: 10px; border-radius: 6px; outline: none; }
        .inp.error { border-color: var(--c-2); animation: shake 0.4s ease-in-out; }
        .btn { background: var(--c-0); border: none; padding: 10px; border-radius: 6px; color: #000; font-weight: bold; cursor: pointer; }
        
        @keyframes shake {
            0%, 100% { transform: translateX(0); }
            25% { transform: translateX(-5px); }
            75% { transform: translateX(5px); }
        }
         
         /* --- Color Definitions --- */

        /* --- Color Definitions --- */
        /* Card Status (Top Bar Color) */
        .cs-0 { --card-color: var(--c-0); }
        .cs-1 { --card-color: var(--c-1); }
        /* 【修复】红色告警使用呼吸灯光效，而不是改边框 */
        .cs-2 { --card-color: var(--c-2); animation: glow-pulse 2s infinite; }
        
        /* Item Status (Values & Bars) */
        .is-0 { --item-color: var(--c-0); }
        .is-1 { --item-color: var(--c-1); }
        .is-2 { --item-color: var(--c-2); }

        /* 【核心修复】呼吸光晕动画：只改变 box-shadow，不改变 border-color */
        @keyframes glow-pulse {
            0% { box-shadow: 0 4px 20px rgba(0,0,0,0.2); }
            50% { box-shadow: 0 0 30px rgba(239, 68, 68, 0.25); } /* 红色柔和光晕 */
            100% { box-shadow: 0 4px 20px rgba(0,0,0,0.2); }
        }

        @media (max-width: 600px) {
            .header { flex-direction: column; align-items: flex-start; gap: 10px; }
            .sys-info { justify-content: flex-start; width: 100%; font-size: 0.8rem; }
            .dashboard { grid-template-columns: 1fr; padding: 10px; }
            
            /* 强制保持左右布局，并缩小圆环比例 */
            .layout-std { flex-direction: row; gap: 15px; align-items: center; }
            .ring-wrap { width: 100px; height: 100px; } /* 手机端缩小圆环 */
            .rd-val { font-size: 1.5rem; }
            .rd-name { font-size: 0.9rem; margin-top: 5px; }
            .ring-container { flex-shrink: 0; }

            /* ★★★ DASH 手机端优化：强制双列 ★★★ */
            .layout-dash {
                /* 强制一行两列，平分宽度，确保至少显示两个 */
                grid-template-columns: repeat(2, 1fr);
                gap: 8px;
            }
            .dash-item {
                padding: 8px 10px; /* 减小内边距 */
            }
            .dash-lbl { font-size: 0.75rem; } /* 稍微缩小标签字体 */
        }
    </style>
</head>
<body>

    <div class='header'>
        <div class='brand'>Lite<span>Monitor</span></div>
        <div class='sys-info'>
            <div class='tag'><b>IP</b> <span id='sys-ip'>--</span></div>
            <div class='tag'><b>RUNTIME</b> <span id='sys-uptime'>--</span></div>
            <div id='btn-auth' class='tag' style='cursor:pointer;display:none' onclick='onAuthTagClick()' title='输入密码 / Enter Password'><b>AUTH</b></div>
             <div class='tag'>
                <div id='status-dot' style='width:8px; height:8px; border-radius:50%; background:var(--text-sub); margin-right:6px;'></div>
                <span id='status-text' style='font-weight:700; font-size:0.8rem;'>--</span>
            </div>
        </div>
    </div>

    <div class='dashboard' id='board'></div>

    <div id='auth-modal' class='modal-overlay'>
        <div class='modal'>
            <h3>访问密码 / Password</h3>
            <input type='password' id='pwd-input' class='inp' placeholder='输入密码 / Enter Password...' />
            <button class='btn' onclick='savePwd()'>连接 / Connect</button>
        </div>
    </div>

    <script>
        const AUTH_REQUIRED = {{AUTH_REQUIRED}};
        const board = document.getElementById('board');

        // =====================================================================
        // S2.1 前端协议（契约 W1 / W3 / W4 / W10 / W13 / W13.5）
        // =====================================================================
        //  · 口令只出现在登录请求的**请求体**里：不进 URL、不进任何长期存储、不进日志。
        //  · 主会话 = HttpOnly Cookie（lmsid）：浏览器自动携带，前端读不到（这是正常的）。
        //  · CSRF 令牌 = 登录响应体下发，**只存在内存变量**里。
        //  · WebSocket 握手前先取一次性票据（ticket：TTL 30 秒、一次性、仅限 WS）。
        let csrfToken = null;
        let ws = null;
        let reconnectTimer = null;
        let sessionRetryTimer = null;
        let connecting = false;
        let wsFailures = 0;

        // ★ 用无原型对象承载外部数据当键的映射：
        //   否则 gid / item key 等外部字符串可能撞上 __proto__ 之类的属性名。
        const cards = Object.create(null);

        const statusDot = document.getElementById('status-dot');
        const statusText = document.getElementById('status-text');

        // ---------------------------------------------------------------------
        // 外部数据 → DOM 的统一入口
        // ---------------------------------------------------------------------
        // 外部数据（分组名、监控名、插件标签、取值……）一律按**文本节点**处理：
        // 只用 textContent / createElement，绝不拼进 HTML 字符串。
        function text(v) {
            return (v === null || v === undefined) ? '' : String(v);
        }

        // 动态 DOM id / class 后缀：只保留 [A-Za-z0-9_-]，其余字符确定性替换为 '_'。
        const ID_SAFE = /[^A-Za-z0-9_-]/g;
        function safeToken(v) {
            return text(v).replace(ID_SAFE, '_');
        }

        // 原始 gid → 安全且唯一的 id 后缀（清洗后重名的分组不会互相覆盖）。
        const groupKeys = Object.create(null);
        const usedGroupKeys = Object.create(null);
        function groupKey(gid) {
            const raw = text(gid);
            let key = groupKeys[raw];
            if (key === undefined) {
                key = safeToken(raw);
                if (key === '') key = 'g';
                if (usedGroupKeys[key]) {
                    let n = 2;
                    while (usedGroupKeys[key + '_' + n]) n++;
                    key = key + '_' + n;
                }
                usedGroupKeys[key] = true;
                groupKeys[raw] = key;
            }
            return key;
        }

        // ---------------------------------------------------------------------
        // 会话：登录 / 票据 / 注销（契约 W3 / W4 / W13.5）
        // ---------------------------------------------------------------------
        // 升级 / 登出流程主动删除历史残留的明文口令（契约 W10）。
        function purgeLegacyPassword() {
            try { localStorage.removeItem('ws_pwd'); } catch (e) { }
        }

        // 改变状态的请求统一带上 CSRF 头（契约 W4）。
        function authHeaders(extra) {
            const h = extra || {};
            if (csrfToken) h['X-LiteMonitor-CSRF'] = csrfToken;
            return h;
        }

        // 登录：POST /api/auth/login，请求体是 JSON（password 字段放明文口令）。
        // 返回 'ok' | 'bad-password' | 'error'。
        async function login(password) {
            let res;
            try {
                res = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ password: password })
                });
            } catch (e) {
                console.error(e);
                return 'error';
            }
            if (res.status === 401) return 'bad-password';
            if (!res.ok) return 'error';
            let data = null;
            try { data = await res.json(); } catch (e) { data = null; }
            csrfToken = (data && typeof data.csrf === 'string' && data.csrf) ? data.csrf : null;
            return csrfToken ? 'ok' : 'error';
        }

        // 取一次性票据：POST /api/auth/ticket（契约 W3 / W13.5）。
        async function requestTicket() {
            let res;
            try {
                res = await fetch('/api/auth/ticket', {
                    method: 'POST',
                    headers: authHeaders({ 'Content-Type': 'application/json' }),
                    credentials: 'same-origin',
                    body: '{}'
                });
            } catch (e) {
                console.error(e);
                return null;
            }
            if (res.status === 401 || res.status === 403) { onSessionLost(); return null; }
            if (!res.ok) return null;
            let data = null;
            try { data = await res.json(); } catch (e) { return null; }
            return (data && typeof data.ticket === 'string' && data.ticket) ? data.ticket : null;
        }

        // 注销：POST /api/auth/logout（契约 W13.5）；顺带清掉历史明文口令。
        async function logout() {
            try {
                await fetch('/api/auth/logout', {
                    method: 'POST',
                    headers: authHeaders({ 'Content-Type': 'application/json' }),
                    credentials: 'same-origin',
                    body: '{}'
                });
            } catch (e) { console.error(e); }
            csrfToken = null;
            purgeLegacyPassword();
            if (ws) { try { ws.onclose = null; ws.close(); } catch (e) { } ws = null; }
            if (reconnectTimer) { clearInterval(reconnectTimer); reconnectTimer = null; }
        }

        // 会话失效（过期 / 改密 / 被撤销）：清掉内存中的 CSRF 令牌并重新弹出登录框，不静默失败。
        function onSessionLost() {
            csrfToken = null;
            purgeLegacyPassword();
            if (ws) { try { ws.onclose = null; ws.close(); } catch (e) { } ws = null; }
            if (reconnectTimer) { clearInterval(reconnectTimer); reconnectTimer = null; }
            statusDot.style.background = 'var(--c-2)';
            statusText.textContent = 'AUTH';
            if (AUTH_REQUIRED) { showAuth(); return; }
            // L0（无口令）：退避后重试登录，避免与后端互相死循环。
            if (sessionRetryTimer) return;
            sessionRetryTimer = setTimeout(() => { sessionRetryTimer = null; bootstrap(); }, 5000);
        }

        function showAuth() {
            const modal = document.getElementById('auth-modal');
            if (modal.style.display === 'flex') return;
            modal.style.display = 'flex';
            const inp = document.getElementById('pwd-input');
            inp.value = '';               // 前端不再保存口令 → 不回填
            inp.classList.remove('error');
            inp.focus();
        }

        async function savePwd() {
            const inp = document.getElementById('pwd-input');
            const newPwd = inp.value;
            let result = 'error';
            try {
                result = await login(newPwd);
            } catch (e) {
                console.error(e);
            }
            if (result !== 'ok') {
                // 密码错误：抖动 + 清空
                inp.classList.remove('error');
                void inp.offsetWidth; // trigger reflow
                inp.classList.add('error');
                inp.value = '';
                inp.placeholder = (result === 'bad-password') ? '密码错误 / Wrong Password' : '连接失败 / Connection Failed';
                return;
            }

            // 登录成功：口令不落任何存储，输入框里也不留。
            inp.value = '';
            document.getElementById('auth-modal').style.display = 'none';
            if (ws) { try { ws.onclose = null; ws.close(); } catch (e) { } ws = null; }
            connect();
            fetchSnapshot();
        }

        // AUTH 标签：已登录时先注销，再重新输入口令。
        async function onAuthTagClick() {
            if (csrfToken) await logout();
            showAuth();
        }

        document.getElementById('pwd-input').addEventListener('keyup', e => { if (e.key === 'Enter') savePwd(); });

        // L0（未配置口令）：也要先登录换取主会话 —— WebSocket 票据依赖主会话。
        async function bootstrap() {
            const result = await login('');
            if (result === 'ok') {
                connect();
                fetchSnapshot();
                return;
            }
            // 兜底：后端在无口令模式下可能不签发会话 —— 此时直接建立不带票据的连接。
            csrfToken = null;
            connect();
        }

        async function connect() {
            if (ws || connecting) return;
            connecting = true;
            try {
                // ★ 握手前先取一次性票据（契约 W3 / W10 / W13.5）。
                const ticket = csrfToken ? await requestTicket() : null;
                if (csrfToken && !ticket) return;   // 会话失效：requestTicket 内部已处理
                const protocol = location.protocol === 'https:' ? 'wss://' : 'ws://';
                const url = protocol + location.host + (ticket ? '/ws?ticket=' + encodeURIComponent(ticket) : '/ws');
                ws = new WebSocket(url);

                ws.onopen = () => {
                    wsFailures = 0;
                    statusDot.style.background = 'var(--c-0)';
                    statusText.textContent = 'LIVE';
                    if (reconnectTimer) { clearInterval(reconnectTimer); reconnectTimer = null; }
                };

                ws.onmessage = (event) => {
                    try {
                        applySnapshot(JSON.parse(event.data));
                    } catch(e) { console.error(e); }
                };

                ws.onclose = () => {
                    statusDot.style.background = 'var(--c-2)';
                    statusText.textContent = 'OFFLINE';
                    ws = null;
                    wsFailures++;

                    const handleReconnect = () => {
                        if (!reconnectTimer) reconnectTimer = setInterval(connect, 2000);
                    };

                    // 401 = 会话已失效（过期 / 改密 / 被撤销）：
                    //      清掉内存中的 CSRF 令牌并重新弹出登录框，不静默失败。
                    fetch('/api/snapshot', { credentials: 'same-origin' })
                        .then(res => {
                            if (res.status === 401) {
                                onSessionLost();
                                return;
                            }
                            if (!csrfToken && wsFailures >= 3) { statusText.textContent = 'AUTH'; return; }
                            handleReconnect();
                        })
                        .catch(() => handleReconnect());
                };

                ws.onerror = () => ws && ws.close();
            } finally {
                connecting = false;
            }
        }

        // 两条数据通道（HTTP 快照 / WebSocket 广播）共用同一个渲染入口（验收矩阵 D6）。
        function applySnapshot(payload) {
            if (!payload) return;
            if (payload.sys) {
                document.getElementById('sys-ip').textContent = text(payload.sys.ip) + ':' + text(payload.sys.port);
                document.getElementById('sys-uptime').textContent = text(payload.sys.uptime);
            }
            if (payload.items) render(payload.items);
        }

        // HTTP 通道：GET /api/snapshot —— 不带任何口令参数，靠 Cookie 携带会话。
        async function fetchSnapshot() {
            try {
                const res = await fetch('/api/snapshot', { credentials: 'same-origin' });
                if (res.status === 401) { onSessionLost(); return null; }
                if (!res.ok) return null;
                const data = await res.json();
                applySnapshot(data);
                return data;
            } catch (e) {
                console.error(e);
                return null;
            }
        }

        function render(items) {
            const groups = Object.create(null);
            const orderList = [];

            items.forEach(i => {
                const gid = text(i.gid || 'OTHER');
                if (!groups[gid]) {
                    // ★★★ 修复：读取后端传回的 gidx 用于 CSS 排序 ★★★
                    groups[gid] = { name: i.gn, core: null, subs: [], maxSts: 0, gidx: (i.gidx !== undefined ? i.gidx : 999) };
                    orderList.push(gid);
                }

                if (i.primary && i.sts > groups[gid].maxSts) groups[gid].maxSts = i.sts;

                const isBigMode = ['NET', 'DISK', 'DATA'].includes(gid);
                // [Fix] 判定核心指标 (Core/Ring) 的逻辑
                // 之前只通过 'Load' 关键字判断，但首次渲染时可能因为数据不全或其他原因导致误判
                // 这里增强判断逻辑：如果是 CPU/GPU/MEM 组，且是负载/使用率相关的项，则视为核心项
                const itemKey = text(i.k);
                const itemUnit = text(i.u);
                const isLoad = (itemKey.includes('Load') || (itemUnit.includes('%') && !itemKey.includes('Fan'))) && !itemKey.includes('Fan');

                // 确保只有特定的组才会有圆环 (Standard Layout)
                const isStdLayout = !isBigMode && gid !== 'DASH';

                if (isStdLayout && isLoad && !groups[gid].core) {
                    groups[gid].core = i;
                } else {
                    groups[gid].subs.push(i);
                }
            });

            // ★★★ 核心修复：确保 DASH 组始终在最上方 (现在使用 CSS order，这里不再需要手动调整 orderList) ★★★
            // const dashIdx = orderList.indexOf('DASH');
            // if (dashIdx > -1) { ... }

            orderList.forEach(gid => {
                const grp = groups[gid];
                const isBig = ['NET', 'DISK', 'DATA'].includes(gid);
                const isDash = gid === 'DASH'; // 识别 DASH 组

                if (!cards[gid]) {
                    // ★ 动态分组标识先过受限字符集，再进 id / class（验收矩阵 D4）。
                    const key = groupKey(gid);
                    const div = document.createElement('div');

                    // ★★★ 核心修复：应用 CSS order 属性，彻底解决乱序问题 ★★★
                    const gidx = Number(grp.gidx);
                    div.style.order = isDash ? -999 : (isFinite(gidx) ? gidx : 999);

                    let content = null;
                    let cont = null;
                    let core = null;
                    if (isDash) {
                        // DASH 布局
                        content = document.createElement('div');
                        content.className = 'layout-dash';
                        content.id = 'dash-' + key;
                        cont = content;
                    } else if (isBig) {
                        content = document.createElement('div');
                        content.className = 'layout-big';
                        content.id = 'big-' + key;
                        cont = content;
                    } else {
                        // [Fix] 无论当前是否有核心数据，Standard 布局都强制渲染圆环容器
                        // 这样即使数据晚到，DOM 结构也是完整的，后续 update 时能找到元素
                        const ringContainer = document.createElement('div');
                        ringContainer.className = 'ring-container';

                        const ringWrap = document.createElement('div');
                        ringWrap.className = 'ring-wrap';
                        ringWrap.id = 'rw-' + key;
                        ringWrap.style.opacity = grp.core ? '1' : '0';

                        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                        svg.setAttribute('class', 'ring-svg');
                        svg.setAttribute('viewBox', '0 0 36 36');

                        const bgPath = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                        bgPath.setAttribute('class', 'ring-bg');
                        bgPath.setAttribute('d', 'M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831');

                        const valPath = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                        valPath.setAttribute('class', 'ring-val');
                        valPath.id = 'rp-' + key;
                        valPath.setAttribute('stroke-dasharray', '0, 100');
                        valPath.setAttribute('d', 'M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831');

                        svg.appendChild(bgPath);
                        svg.appendChild(valPath);

                        const ringData = document.createElement('div');
                        ringData.className = 'ring-data';

                        const ringVal = document.createElement('div');
                        ringVal.className = 'rd-val';
                        ringVal.id = 'rv-' + key;
                        ringVal.textContent = '--';

                        const ringUnit = document.createElement('div');
                        ringUnit.className = 'rd-unit';
                        ringUnit.id = 'ru-' + key;
                        ringUnit.textContent = '%';

                        ringData.appendChild(ringVal);
                        ringData.appendChild(ringUnit);

                        ringWrap.appendChild(svg);
                        ringWrap.appendChild(ringData);
                        ringContainer.appendChild(ringWrap);

                        const ringName = document.createElement('div');
                        ringName.className = 'rd-name';
                        ringName.id = 'rn-' + key;
                        ringName.textContent = grp.core ? text(grp.core.n) : '--';
                        ringContainer.appendChild(ringName);

                        const layout = document.createElement('div');
                        layout.className = 'layout-std';

                        const list = document.createElement('div');
                        list.className = 'detail-list';
                        list.id = 'list-' + key;

                        layout.appendChild(ringContainer);
                        layout.appendChild(list);

                        content = layout;
                        cont = list;
                        // [Fix] 始终绑定 DOM 引用，防止后续数据到达时找不到元素
                        core = { wrap: ringWrap, p: valPath, v: ringVal, u: ringUnit, n: ringName };
                    }

                    // ★ 分组名只作为**文本节点**写入（外部数据 → DOM 的唯一入口）。
                    const head = document.createElement('div');
                    head.className = 'card-head';
                    head.textContent = text(grp.name);

                    div.appendChild(head);
                    div.appendChild(content);
                    board.appendChild(div);

                    // ★★★ DASH 全宽显示 ★★★
                    if (isDash) div.className = 'card full-width';

                    cards[gid] = {
                        el: div, isBig, isDash,
                        cont: cont,
                        rows: Object.create(null),
                        core: core
                    };
                }

                // 更新样式
                if (!isDash) cards[gid].el.className = 'card cs-' + safeToken(grp.maxSts);
                
                // ★★★ 修复：强制排序 (不再需要，CSS order 已接管) ★★★
                // board.appendChild(cards[gid].el);

                const cObj = cards[gid];

                if (isDash) {
                     grp.subs.forEach(item => {
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            el.className = 'dash-item';
                            // ★ 监控名只作为**文本节点**写入。
                            const lbl = document.createElement('div');
                            lbl.className = 'dash-lbl';
                            lbl.textContent = text(item.n);
                            const val = document.createElement('div');
                            val.className = 'dash-val';
                            val.textContent = '--';
                            el.appendChild(lbl);
                            el.appendChild(val);
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el, 
                                v: val
                            };
                            r = cObj.rows[item.k];
                        }
                        // 更新数值
                        const itemUnit = text(item.u);
                        let valStr = text(item.v);
                        if (itemUnit !== '') valStr += ' ' + itemUnit;
                        if (r.v.textContent !== valStr) r.v.textContent = valStr;
                        
                        // ★★★ 修复：应用颜色状态 (is-0, is-1, is-2) ★★★
                        // 先移除旧的状态类
                        r.v.classList.remove('is-0', 'is-1', 'is-2');
                        // 添加新的状态类 (如果 sts >= 0)
                        if (item.sts >= 0) r.v.classList.add('is-' + safeToken(item.sts));

                     });
                } else if (isBig) {
                    grp.subs.forEach((item, idx) => {
                        if (idx > 1) return; 
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            // ★ 监控名只作为**文本节点**写入。
                            const lbl = document.createElement('div');
                            lbl.className = 'big-lbl';
                            lbl.textContent = text(item.n);
                            const bigVal = document.createElement('div');
                            bigVal.className = 'big-val';
                            bigVal.textContent = '--';
                            const bigUnit = document.createElement('div');
                            bigUnit.className = 'big-unit';
                            bigUnit.textContent = '--';
                            el.appendChild(lbl);
                            el.appendChild(bigVal);
                            el.appendChild(bigUnit);
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el, 
                                v: bigVal,
                                u: bigUnit
                            };
                            r = cObj.rows[item.k];
                        }
                        r.el.className = 'big-item is-' + safeToken(item.sts);
                        if (r.v.textContent !== text(item.v)) r.v.textContent = text(item.v);
                        if (r.u.textContent !== text(item.u)) r.u.textContent = text(item.u);
                    });
                } else {
                    // [Fix] 渲染或更新 Core Ring 数据
                    // 如果之前 core 是隐藏的（因为首次加载时没数据），现在有数据了就显示出来
                    if (grp.core && cObj.core) {
                        cObj.core.wrap.style.opacity = '1'; // 确保显示
                        cObj.core.wrap.className = 'ring-wrap is-' + safeToken(grp.core.sts);
                        cObj.core.v.textContent = text(grp.core.v);
                        cObj.core.u.textContent = text(grp.core.u);
                        cObj.core.n.textContent = text(grp.core.n);
                        const corePct = Number(grp.core.pct);
                        let pct = Math.min(Math.max(isFinite(corePct) ? corePct : 0, 0), 100);
                        cObj.core.p.setAttribute('stroke-dasharray', `${pct}, 100`);
                    }

                    grp.subs.forEach(item => {
                        let r = cObj.rows[item.k];
                        if (!r) {
                            const el = document.createElement('div');
                            // ★ 监控名只作为**文本节点**写入。
                            const info = document.createElement('div');
                            info.className = 'd-info';
                            const lbl = document.createElement('span');
                            lbl.className = 'd-lbl';
                            lbl.textContent = text(item.n);
                            const box = document.createElement('div');
                            const rowVal = document.createElement('span');
                            rowVal.className = 'd-val-box';
                            rowVal.textContent = '--';
                            const rowUnit = document.createElement('span');
                            rowUnit.className = 'd-unit';
                            box.appendChild(rowVal);
                            box.appendChild(rowUnit);
                            info.appendChild(lbl);
                            info.appendChild(box);
                            const barBg = document.createElement('div');
                            barBg.className = 'd-bar-bg';
                            const barFill = document.createElement('div');
                            barFill.className = 'd-bar-fill';
                            barBg.appendChild(barFill);
                            el.appendChild(info);
                            el.appendChild(barBg);
                            cObj.cont.appendChild(el);
                            cObj.rows[item.k] = { 
                                el,
                                v: rowVal,
                                u: rowUnit,
                                b: barFill
                            };
                            r = cObj.rows[item.k];
                        }
                        r.el.className = 'd-row is-' + safeToken(item.sts);
                        if (r.v.textContent !== text(item.v)) r.v.textContent = text(item.v);
                        if (r.u.textContent !== text(item.u)) r.u.textContent = text(item.u);
                        const rowPct = Number(item.pct);
                        r.b.style.width = Math.min(Math.max(isFinite(rowPct) ? rowPct : 0, 0), 100) + '%';
                    });
                }
            });
        }

        // ---------------------------------------------------------------------
        // 启动
        // ---------------------------------------------------------------------
        // ★ 升级流程：主动删除历史残留的明文口令（契约 W10）。
        purgeLegacyPassword();
        if (AUTH_REQUIRED) {
            document.getElementById('btn-auth').style.display = 'flex';
            showAuth();
        } else {
            bootstrap();
        }
    </script>
</body>
</html>";
    }
}