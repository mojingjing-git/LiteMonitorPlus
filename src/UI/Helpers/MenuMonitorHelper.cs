using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 菜单监控项生成助手
    /// 职责：生成监控项列表、处理分组、排序、动态标签及首次校准提示
    /// </summary>
    public static class MenuMonitorHelper
    {
        public static ToolStripMenuItem Build(MainForm form, Settings cfg, UIController? ui, bool isTaskbarMode)
        {
            var monitorRoot = new ToolStripMenuItem(LanguageManager.T("Menu.MonitorItemDisplay"));

            // [新增] 插件管理入口 (Emoji + 跳转)
            var pluginMgr = new ToolStripMenuItem(LanguageManager.T("Menu.Plugins")); 
            pluginMgr.Click += (_, __) => 
            {
                try
                {
                    using (var f = new LiteMonitor.src.UI.SettingsForm(cfg, ui, form))
                    {
                        f.SwitchPage("Plugins"); 
                        f.ShowDialog(form);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Open Settings Failed: " + ex.Message);
                }
            };
            monitorRoot.DropDownItems.Add(pluginMgr);
            monitorRoot.DropDownItems.Add(new ToolStripSeparator());

            // --- 内部辅助函数：首次开启时的最大值设定引导 ---
            void CheckAndRemind(string name)
            {
                if (cfg.MaxLimitTipShown) return;

                string msg = cfg.Language == "zh"
                    ? $"您是首次开启 {name}。\n\n建议设置一下“电脑{name}”实际最大值，让进度条显示更准确。\n\n是否现在去设置？\n\n点“否”将不再提示，程序将在高负载时（如大型游戏时）进行动态学习最大值"
                    : $"First launch of {name}.\n\nSet the actual maximum value for accurate progress bar display.\n\nGo to settings now?\n\nSelect \"No\" to skip permanently. App will auto-learn max value in high-load scenarios (e.g., gaming).";

                cfg.MaxLimitTipShown = true;
                cfg.Save();

                if (MessageBox.Show(msg, "LiteMonitor++ Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        using (var f = new LiteMonitor.src.UI.SettingsForm(cfg, ui, form))
                        {
                            f.SwitchPage("System"); // 跳转到可以设置最大值的页面
                            f.ShowDialog(form);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("设置面板启动失败: " + ex.Message);
                    }
                }
            }

            // --- 内部辅助函数：判断是否为需要校准的硬件项 ---
            bool IsHardwareItem(string key)
            {
                return (key.Contains("Clock") || key.Contains("Power") || 
                       key.Contains("Fan") || key.Contains("Pump")) && !key.Contains("BAT");
            }

            // [Optimization] Shared handler for Taskbar items
            EventHandler onTaskbarItemCheck = (s, e) => 
            {
                if (s is ToolStripMenuItem item && item.Tag is MonitorItemConfig conf)
                {
                    conf.VisibleInTaskbar = item.Checked;
                    cfg.Save();
                    // 在菜单交互中，无需重建菜单 (rebuildMenus: false)
                    AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);

                    if (item.Checked && IsHardwareItem(conf.Key))
                    {
                        // [Refactor] Use unified resolver instead of outdated DisplayLabel property
                        string full = MetricLabelResolver.ResolveLabel(conf);
                        if (string.IsNullOrEmpty(full))
                        {
                            full = LanguageManager.T("Items." + conf.Key);
                            if (full.StartsWith("Items.")) 
                            {
                                full = conf.Key;
                                // [Fix] Handle untranslated plugin keys (e.g. DASH.UniversalAPI.0.val)
                                if (full.StartsWith("DASH.") && full.Contains("."))
                                {
                                    int lastDot = full.LastIndexOf('.');
                                    if (lastDot >= 0) full = full.Substring(lastDot + 1);
                                }
                            }
                        }
                        CheckAndRemind(full);
                    }
                }
            };

            // [Optimization] Shared handler for Panel items
            EventHandler onPanelItemCheck = (s, e) => 
            {
                if (s is ToolStripMenuItem item && item.Tag is MonitorItemConfig conf)
                {
                    conf.VisibleInPanel = item.Checked;
                    cfg.Save();
                    // 在菜单交互中，无需重建菜单 (rebuildMenus: false)
                    AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);

                    if (item.Checked && IsHardwareItem(conf.Key))
                    {
                        string full = conf.DisplayLabel;
                        if (string.IsNullOrEmpty(full))
                        {
                             // [Optimization] Intern the key to prevent duplicates
                             full = LanguageManager.T(UIUtils.Intern("Items." + conf.Key));
                             if (full.StartsWith("Items.")) full = conf.Key;
                        }
                        CheckAndRemind(full);
                    }
                }
            };

            if (isTaskbarMode)
            {
                // --- 模式 A: 任务栏 (平铺排序 + 显示全称和简称) ---
                var sortedItems = cfg.MonitorItems.OrderBy(x => x.TaskbarSortIndex).ToList();
                
                foreach (var itemConfig in sortedItems)
                {
                    // [Refactor] 使用统一解析器
                    string labelResolved = MetricLabelResolver.ResolveLabel(itemConfig);
                    string full;
                    if (!string.IsNullOrEmpty(labelResolved))
                    {
                        full = labelResolved;
                    }
                    else
                    {
                        full = LanguageManager.T(UIUtils.Intern("Items." + itemConfig.Key));
                        if (full.StartsWith("Items.")) full = itemConfig.Key;
                    }
                    
                    // Short Name
                    string shortResolved = MetricLabelResolver.ResolveShortLabel(itemConfig);
                    string shortName;
                    
                    if (!string.IsNullOrEmpty(shortResolved) && shortResolved != " ")
                    {
                        shortName = shortResolved;
                    }
                    else
                    {
                         // If hidden or empty, fallback to default localized short name for the menu text
                         shortName = LanguageManager.T(UIUtils.Intern("Short." + itemConfig.Key));
                         if (shortName.StartsWith("Short.")) shortName = itemConfig.Key;
                    }

                    // 2. 构造菜单显示文本
                    string finalLabel = $"{full} ({shortName})";

                    // 2. 创建菜单
                    var itemMenu = new ToolStripMenuItem(finalLabel)
                    {
                        Checked = itemConfig.VisibleInTaskbar,
                        CheckOnClick = true,
                        Tag = itemConfig // Store context
                    };

                    // 3. 事件与提示
                    itemMenu.CheckedChanged += onTaskbarItemCheck;

                    // 4. 鼠标悬停提示
                    if (IsHardwareItem(itemConfig.Key))
                        itemMenu.ToolTipText = LanguageManager.T("Menu.CalibrationTip");

                    monitorRoot.DropDownItems.Add(itemMenu);
                }
            }
            else
            {
                // --- 模式 B: 主界面 (HOST分组 + 组内排序) ---
                var sortedItems = cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
                var groups = sortedItems.GroupBy(x => x.UIGroup); // 利用 UIGroup 自动识别 HOST

                // 辅助函数：创建单个菜单项
                ToolStripMenuItem CreateItemMenu(MonitorItemConfig itemConfig)
                {
                     // [Refactor] 使用统一解析器
                    string labelResolved = MetricLabelResolver.ResolveLabel(itemConfig);

                    // Label: Resolved > Loc(Items.Key) > Key
                    string def = LanguageManager.T(UIUtils.Intern("Items." + itemConfig.Key));
                    if (def.StartsWith("Items.")) 
                    {
                        def = itemConfig.Key;
                        // [Fix] Handle untranslated plugin keys
                        if (def.StartsWith("DASH.") && def.Contains("."))
                        {
                            int lastDot = def.LastIndexOf('.');
                            if (lastDot >= 0) def = def.Substring(lastDot + 1);
                        }
                    }
                    
                    string finalLabel = !string.IsNullOrEmpty(labelResolved) ? labelResolved : def;

                    var itemMenu = new ToolStripMenuItem(finalLabel)
                    {
                        Checked = itemConfig.VisibleInPanel,
                        CheckOnClick = true,
                        Tag = itemConfig // Store context
                    };

                    itemMenu.CheckedChanged += onPanelItemCheck;

                    if (IsHardwareItem(itemConfig.Key))  
                        itemMenu.ToolTipText = LanguageManager.T("Menu.CalibrationTip");
                        
                    return itemMenu;
                }

                // 定义需要纯开关模式的组 (点击组名即全开/全关，无子项)
                var toggleGroups = new HashSet<string> { "DISK", "NET", "DATA" };

                foreach (var g in groups)
                {
                    // 分组标题
                    string gName = LanguageManager.T(UIUtils.Intern("Groups." + g.Key));
                    if (cfg.GroupAliases.ContainsKey(g.Key)) gName = cfg.GroupAliases[g.Key];
                    
                    if (g.Key == "BAT")
                    {
                        // 电池组：保持折叠子项模式
                        var batRoot = new ToolStripMenuItem(gName);
                        foreach (var itemConfig in g)
                        {
                            batRoot.DropDownItems.Add(CreateItemMenu(itemConfig));
                        }
                        monitorRoot.DropDownItems.Add(batRoot);
                    }
                    else if (toggleGroups.Contains(g.Key))
                    {
                        // 磁盘/网络/流量：纯开关模式 (无子项)
                        // 使用 CheckOnClick = true 简化逻辑，自动处理 UI 勾选状态
                        var groupItem = new ToolStripMenuItem(gName)
                        {
                            CheckOnClick = true,
                            Checked = g.Any(x => x.VisibleInPanel)
                        };
                        
                        // 事件: 状态改变时同步到所有子项
                        groupItem.CheckedChanged += (s, e) => 
                        {
                            bool newState = groupItem.Checked;
                            foreach (var itemConfig in g)
                                itemConfig.VisibleInPanel = newState;
                            
                            cfg.Save();
                            // 在菜单交互中，无需重建菜单 (rebuildMenus: false)
                            AppActions.ApplyMonitorLayout(cfg, ui, form, rebuildMenus: false);
                        };
                        
                        monitorRoot.DropDownItems.Add(groupItem);
                    }
                    else
                    {
                        // 其他组：平铺模式 (标题不可点 + 子项列表)
                        monitorRoot.DropDownItems.Add(new ToolStripMenuItem(gName) { Enabled = false, ForeColor = Color.Gray });
                        foreach (var itemConfig in g)
                        {
                            monitorRoot.DropDownItems.Add(CreateItemMenu(itemConfig));
                        }
                    }
                    
                    monitorRoot.DropDownItems.Add(new ToolStripSeparator());
                }
                
                // 删掉最后多余的分割线
                if (monitorRoot.DropDownItems.Count > 0 && monitorRoot.DropDownItems[monitorRoot.DropDownItems.Count - 1] is ToolStripSeparator)
                    monitorRoot.DropDownItems.RemoveAt(monitorRoot.DropDownItems.Count - 1);
            }

            return monitorRoot;
        }
    }
}
