using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor;
using LiteMonitor.Contracts;          // B2：消费平台无关目录，不再引用 LHM
using LiteMonitor.src.SystemServices;
using LiteMonitor.SystemServices;     // B2：HardwareClientAdapter（默认组装用，见构造函数注释）
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI
{
    public class HardwareInfoForm : Form
    {
        /// <summary>传感器窗口的需求租约身份（E01-B：按消费者身份登记，关闭时只释放自己那一份）。</summary>
        private const string SensorWindowConsumerId = "sensor-window";

        private LiteTreeView _tree;
        private System.Windows.Forms.Timer _refreshTimer;
        private Panel _headerPanel;
         private TextBox _searchInput;

        private Settings _settings = Settings.Load();

        // B2：目录缓存与查找（每次刷新整体原子替换，树节点持有条目快照而非 LHM 活对象）
        private readonly IHardwareClient _client;
        private HardwareCatalog? _catalog;
        private ILookup<string?, HardwareCatalogEntry>? _children;
        private Dictionary<string, HardwareCatalogEntry>? _sensorById;

        private string T(string en, string zh) => _settings.Language.ToLower().StartsWith("zh") ? zh : en;

        // B3:客户端必传(组合根经网关注入,MenuManager 构造);不再默认就地组装
        // ★ 形参声明为可空：调用方（MenuManager）拿到的是 HardwareGateway.Client（可能为 null），
        //   但构造函数内部依旧用 ?? throw ArgumentNullException 拒绝 null ——
        //   异常类型、参数名、抛出位置全部不变，运行期行为完全一致。
        public HardwareInfoForm(IHardwareClient? hardwareClient)
        {
            _client = hardwareClient ?? throw new ArgumentNullException(nameof(hardwareClient));
            this.Text = T("LiteMonitor++ - Hardware Info", "LiteMonitor++ - 系统硬件详情");
            this.Size = new Size(UIUtils.S(600), UIUtils.S(750)); // 稍微加宽一点
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;
            this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 搜索栏
            var pnlToolbar = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(40), Padding = new Padding(10), BackColor = Color.WhiteSmoke };
            _searchInput = new TextBox { 
                Dock = DockStyle.Fill, 
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9f), 
                PlaceholderText = T("Search sensor name...", "搜索传感器名称...") 
            };
            _searchInput.TextChanged += (s, e) => RebuildTree(_searchInput.Text.Trim());
            pnlToolbar.Controls.Add(_searchInput);

            // 表头
            _headerPanel = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(24), BackColor = Color.FromArgb(250, 250, 250) };
            _headerPanel.Paint += HeaderPanel_Paint;
            _headerPanel.Resize += (s, e) => _headerPanel.Invalidate();

            // --- 这里的菜单定义代码需要完全替换 ---
            _tree = new LiteTreeView { Dock = DockStyle.Fill };
            
            var cms = new ContextMenuStrip();
            
            // 1. 定义菜单项 (保留引用以便后续控制显示)
            var itemCopyName = cms.Items.Add(T("Copy Name", "复制名称"), null, (s, e) => CopyInfo("Name"));
            var itemCopyId = cms.Items.Add(T("Copy ID", "复制传感器ID"), null, (s, e) => CopyInfo("ID"));
            var itemCopyVal = cms.Items.Add(T("Copy Value", "复制数值"), null, (s, e) => CopyInfo("Value"));
            
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add(T("Expand All", "全部展开"), null, (s, e) => _tree.ExpandAll());
            cms.Items.Add(T("Collapse All", "全部折叠"), null, (s, e) => _tree.CollapseAll());

            // 2. ★★★ 新增：Opening 事件，根据选中节点的类型动态显示/隐藏菜单项 ★★★
            cms.Opening += (s, e) => 
            {
                var node = _tree.SelectedNode;
                if (node == null)
                {
                    e.Cancel = true; // 没选中东西就不显示菜单
                    return;
                }

                bool isSensor = node.Tag is HardwareCatalogEntry { SensorType: not null };
                
                // 任何节点都可以复制名称
                itemCopyName.Visible = true;
                
                // 只有传感器才有 ID 和 Value
                itemCopyId.Visible = isSensor;
                itemCopyVal.Visible = isSensor;
            };

            _tree.ContextMenuStrip = cms;

            this.Controls.Add(_tree);
            this.Controls.Add(_headerPanel);
            this.Controls.Add(pnlToolbar);

            // B2：显式登记传感器窗口需求（契约侧存储，通道合流在 B3 接线），关闭时在 OnFormClosed 释放
            // ★ E01-B：按**消费者身份**登记（租约）——关闭本窗口只释放自己那一份，不影响其他消费者。
            _client.SubmitDemand(SensorWindowConsumerId, new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: true));
            _ = LoadCatalogAsync(rebuild: true);

            // 局部刷新定时器：刷新目录缓存后只更新可见节点数值，不重建树（保持展开状态）
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => { _ = LoadCatalogAsync(rebuild: false); UpdateVisibleNodesSmart(); };
            _refreshTimer.Start();
        }

        private async Task LoadCatalogAsync(bool rebuild)
        {
            try
            {
                var catalog = await _client.GetCatalogAsync(default).ConfigureAwait(true);
                if (IsDisposed) return;

                // ★ E01-B：目录**版本**变化才说明结构变了（值刷新不重建树）。
                bool structureChanged = _catalog == null || catalog.Version != _catalog.Version;

                _catalog = catalog;
                _children = catalog.Entries.ToLookup(e => e.ParentStableId);
                _sensorById = catalog.Entries.Where(e => e.SensorType != null).ToDictionary(e => e.StableId);

                // 结构没变 ⇒ 只刷新可见节点的值（不重建整棵树，展开状态天然保持）。
                if (rebuild || structureChanged) RebuildTree(_searchInput.Text.Trim());
            }
            catch (ObjectDisposedException)
            {
                // 关闭竞态：窗体/适配器已释放，静默退出
            }
        }

        private void UpdateVisibleNodesSmart()
        {
            if (!this.Visible || _tree.IsDisposed) return;
            TreeNode node = _tree.TopNode;
            while (node != null)
            {
                if (node.Bounds.Top > _tree.ClientSize.Height) break;
                if (node.Tag is HardwareCatalogEntry { SensorType: not null } entry)
                {
                    // 用最新目录快照原位替换 Tag（值/峰值更新），不重建节点
                    if (_sensorById != null && _sensorById.TryGetValue(entry.StableId, out var latest)) node.Tag = latest;
                    _tree.InvalidateSensorValue(node);
                }
                node = node.NextVisibleNode;
            }
        }

        // ★ PaintEventHandler 委托的 sender 形参按 .NET 约定就是可空的，
        //   这里如实声明（方法体不使用 sender）。
        private void HeaderPanel_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            // 使用 ClientSize 确保不包含边框宽度
            int w = _headerPanel.ClientSize.Width; 

            // 1. 绘制底部分割线
            using (var pen = new Pen(Color.FromArgb(230, 230, 230)))
                g.DrawLine(pen, 0, _headerPanel.Height - 1, w, _headerPanel.Height - 1);

            var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold); 
            
            // --- 坐标计算 (从右向左推，基准必须与 LiteTreeView 完全一致) ---
            // 布局逻辑: [窗口右边] - [右边距] - [图标占位] - [间距] - [Max列] - [间距] - [Value列]
            
            int rightMargin = UIUtils.S(_tree.RightMargin);
            int iconWidth = UIUtils.S(_tree.IconWidth);
            int colMaxW = UIUtils.S(_tree.ColMaxWidth);
            int colValW = UIUtils.S(_tree.ColValueWidth);
            int gap = UIUtils.S(10); // 列之间的间距

            // 计算各列的 X 坐标 (Left)
            int xIconLeft = w - rightMargin - iconWidth;
            int xMaxLeft = xIconLeft - gap - colMaxW-20;
            int xValueLeft = xMaxLeft - gap - colValW;

            // --- 绘制文本 ---
            // 关键修复：添加 SingleLine | EndEllipsis 防止文字乱码换行

            // 2. 绘制 "Sensor" (左侧)
            // 使用 Rectangle 而不是 Point，并垂直居中，防止位置跑偏
            Rectangle titleRect = new Rectangle(30, 0, xValueLeft - 10, _headerPanel.Height);
            TextRenderer.DrawText(g, " " + T("Sensor", "硬件 > 传感器"), font, titleRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // 3. 绘制 "Max"
            Rectangle maxRect = new Rectangle(xMaxLeft, 0, colMaxW, _headerPanel.Height);
            TextRenderer.DrawText(g, T("Max", "最大记录"), font, maxRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

            // 4. 绘制 "Value"
            Rectangle valRect = new Rectangle(xValueLeft, 0, colValW, _headerPanel.Height);
            TextRenderer.DrawText(g, T("Value", "数值"), font, valRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
            
            font.Dispose();
        }

        private void RebuildTree(string filter)
        {
            // ★ E01-B：目录版本变化（结构变了）才重建树；重建前先按 StableId 记下展开状态，重建后恢复。
            var expanded = CaptureExpandedState();

            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            var catalog = _catalog;
            var children = _children;
            if (catalog == null || children == null || catalog.Entries.Count == 0)
            {
                _tree.Nodes.Add(new TreeNode(T("Initializing...", "初始化中...")));
                _tree.EndUpdate();
                return;
            }

            bool isFirstHardware = true;
            foreach (var hw in children[null].Where(e => e.SensorType == null))
            {
                AddHardwareNode(_tree.Nodes, hw, filter, !string.IsNullOrEmpty(filter), isFirstHardware && string.IsNullOrEmpty(filter));
                isFirstHardware = false;
            }
            _tree.EndUpdate();

            RestoreExpandedState(expanded);
        }

        /// <summary>
        /// 采集当前树的展开状态（键 = 设备条目的 StableId；传感器类型分组用 "父StableId|类型"）。
        /// </summary>
        private HashSet<string> CaptureExpandedState()
        {
            var expanded = new HashSet<string>(StringComparer.Ordinal);
            CollectExpandedState(_tree.Nodes, string.Empty, expanded);
            return expanded;
        }

        private static void CollectExpandedState(TreeNodeCollection nodes, string parentKey, HashSet<string> expanded)
        {
            foreach (TreeNode node in nodes)
            {
                string key = NodeStateKey(node, parentKey);
                if (node.IsExpanded) expanded.Add(key);
                CollectExpandedState(node.Nodes, key, expanded);
            }
        }

        /// <summary>重建后按 StableId 恢复展开状态。</summary>
        private void RestoreExpandedState(HashSet<string> expanded)
        {
            if (expanded.Count == 0) return;
            RestoreExpandedState(_tree.Nodes, string.Empty, expanded);
        }

        private static void RestoreExpandedState(TreeNodeCollection nodes, string parentKey, HashSet<string> expanded)
        {
            foreach (TreeNode node in nodes)
            {
                string key = NodeStateKey(node, parentKey);
                if (expanded.Contains(key)) node.Expand();
                RestoreExpandedState(node.Nodes, key, expanded);
            }
        }

        /// <summary>节点的状态键：设备条目用 StableId；分组节点用 "父键|分组名"。</summary>
        private static string NodeStateKey(TreeNode node, string parentKey)
        {
            if (node.Tag is HardwareCatalogEntry entry) return entry.StableId;
            string group = node.Tag as string ?? node.Text;
            return string.IsNullOrEmpty(parentKey) ? group : parentKey + "|" + group;
        }

        private void AddHardwareNode(TreeNodeCollection parentNodes, HardwareCatalogEntry hw, string filter, bool isSearch, bool isFirstHardware)
        {
            string typeStr = GetHardwareTypeString(hw.DeviceType);
            // ★★★ 替换这里：使用强力白名单清洗 ★★★
            string cleanName = SanitizeHardwareName(hw.Name);
            string label = $"{typeStr} {cleanName}";

            var hwNode = new TreeNode(label) { Tag = hw };
            bool hasContent = false;

            var groups = _children![hw.StableId].Where(e => e.SensorType != null)
                .GroupBy(s => s.SensorType).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                string typeIcon = GetSensorTypeString(group.Key);
                string typeName = $"{typeIcon} {group.Key}";

                // ★★★ 修改：创建节点时，把传感器类型字符串存入 Tag ★★★
                var typeNode = new TreeNode(typeName) { Tag = group.Key };

                bool groupHasMatch = false;
                foreach (var s in group)
                {
                    if (isSearch && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    typeNode.Nodes.Add(new TreeNode(s.Name) { Tag = s });
                    groupHasMatch = true;
                }

                if (groupHasMatch)
                {
                    hwNode.Nodes.Add(typeNode);
                    if (isSearch) typeNode.Expand(); // 只有搜索模式下才展开传感器类型分组
                    hasContent = true;
                }
            }

            foreach (var subHw in _children![hw.StableId].Where(e => e.SensorType == null))
            {
                // 如果当前是第一个硬件节点，其子硬件也需要展开分组
                AddHardwareNode(hwNode.Nodes, subHw, filter, isSearch, isFirstHardware);
            }
            if (hwNode.Nodes.Count > 0) hasContent = true;

            if (!isSearch || hasContent || hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                parentNodes.Add(hwNode);
                
                // ★★★ 默认行为调整 ★★★
                if (isSearch)
                {
                    hwNode.Expand(); // 搜索时全展开
                }
                else
                {
                    if (isFirstHardware)
                    {
                        hwNode.Expand(); // 第一个硬件节点展开，显示所有传感器分组
                    }
                    // 其他硬件节点保持折叠
                }
            }
        }

        private void CopyInfo(string type)
        {
            var node = _tree.SelectedNode;
            if (node == null) return;

            if (type == "Name")
            {
                // ★★★ 智能复制逻辑 (升级版) ★★★
                if (node.Tag is HardwareCatalogEntry { SensorType: null } hw)
                {
                    // 1. 硬件/子硬件：使用与显示逻辑一致的“强力清洗”
                    Clipboard.SetText(SanitizeHardwareName(hw.Name));
                }
                else if (node.Tag is HardwareCatalogEntry s)
                {
                    // 2. 传感器：直接复制名称 (如 "CPU Core #1")
                    Clipboard.SetText(s.Name ?? "");
                }
                else if (node.Tag is string st)
                {
                    // 3. ★新增★ 类型分组 (如 "Temperature")：只复制纯文本名称，不带 Emoji
                    Clipboard.SetText(st);
                }
                else
                {
                    // 4. 其他情况：复制显示文本 (兜底)
                    Clipboard.SetText(node.Text ?? "");
                }
            }
            else if (node.Tag is HardwareCatalogEntry sensorEntry)
            {
                if (type == "Value") Clipboard.SetText(sensorEntry.Value?.ToString() ?? "");
                else if (type == "ID") Clipboard.SetText(sensorEntry.RuntimeId);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            // ★ E01-B：释放**本窗口**的需求租约（只释放 sensor-window 这一份，
            //   其它消费者（面板 / 任务栏 / Web / 历史）的需求不受影响）。
            try
            {
                _client.ReleaseDemand(SensorWindowConsumerId);
            }
            catch (ObjectDisposedException) { }
            catch (NotSupportedException) { /* 尚未实现需求租约的客户端：忽略 */ }
            base.OnFormClosed(e);
            this.Dispose();
        }

        private string GetHardwareTypeString(string type)
        {
            switch (type) {
                case "Cpu": return T("[CPU]", "[处理器]");
                case "GpuNvidia":
                case "GpuAmd":
                case "GpuIntel": return T("[GPU]", "[显卡]");
                case "Memory": return T("[Memory]", "[内存]");
                case "Motherboard": return T("[Motherboard]", "[主板]");
                case "Storage": return T("[Storage]", "[硬盘]");
                case "Network": return T("[Network]", "[网卡]");
                case "SuperIO": return T("[SuperIO]", "[IO芯片]");
                // 可选：如果遇到水冷控制器等
                case "Cooler": return T("[Cooler]", "[散热器]");
                default: return $"[{type}]";
            }
        }
        // ★ 运行期事实：调用方是 GroupBy(s => s.SensorType) 的分组键，而 SensorType 本身是 string?；
        //   上面已用 Where(e => e.SensorType != null) 过滤过 ⇒ 运行期这里不会拿到 null。
        //   但类型上确实是 string?，如实声明为可空：null 会落到 default 分支输出 "[null]"，
        //   不会引入新的异常路径。
        private string GetSensorTypeString(string? type)
        {
            switch (type) {
                case "Temperature": return T("[Temperature]", "[温度]");
                case "Load": return T("[Load]", "[负载]");
                case "Fan": return T("[Fan]", "[风扇]");
                case "Power": return T("[Power]", "[功耗]");
                case "Clock": return T("[Clock]", "[频率]");
                case "Control": return T("[Control]", "[控制]");
                case "Voltage": return T("[Voltage]", "[电压]");
                case "Data": return T("[Data]", "[数据]");
                case "SmallData": return T("[SmallData]", "[小型数据]");
                case "Throughput": return T("[Throughput]", "[吞吐量]");
                // ★★★ 新增以下三项 ★★★
                case "Level": return T("[Level]", "[剩余/寿命]"); // 用于 SSD 寿命或油箱液位
                case "Factor": return T("[Factor]", "[系数]");      // 用于写入放大系数等
                case "Timing": return T("[Timing]", "[时序]");
                default: return $"[{type}]";
            }
        }

        // 强力清洗：只保留看着像“正常名字”的字符
        private string SanitizeHardwareName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // 1. 过滤：只保留 字母、数字、标点符号、空格
            // 内存名称里通常只有这些：A-Z a-z 0-9 - _ ( ) [ ] . 空格
            char[] cleanChars = input.Where(c => 
                char.IsLetterOrDigit(c) || 
                c == ' ' || c == '-' || c == '_' || c == '.' || 
                c == '(' || c == ')' || c == '[' || c == ']' ||
                c == '#' || c == '/' || c == '+'  // 允许 #1, Ddr4/5 等符号
            ).ToArray();

            string result = new string(cleanChars).Trim();

            // 2. 兜底：如果清洗完只剩下一堆怪字符或者太短，说明这次读取彻底废了
            // 这种情况下，与其显示 "A??>}", 不如显示一个通用的 "Unknown Memory"
            if (result.Length < 2) return "Generic Hardware"; 

            // 3. 移除可能存在的连续空格
            while (result.Contains("  ")) result = result.Replace("  ", " ");

            return result;
        }
    }
}