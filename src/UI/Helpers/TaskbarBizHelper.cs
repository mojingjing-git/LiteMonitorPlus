using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using static LiteMonitor.src.UI.Helpers.TaskbarWinHelper;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏业务助手 (Business Helper)
    /// 职责：布局计算、位置定位、主题检测、菜单与双击逻辑
    /// </summary>
    public class TaskbarBizHelper
    {
        private readonly Form _form;
        private readonly Settings _cfg;
        private readonly TaskbarWinHelper _winHelper;
        
        private Rectangle _taskbarRect = Rectangle.Empty;
        private int _taskbarHeight = 32;
        private IntPtr _hTaskbar = IntPtr.Zero;
        private IntPtr _hTray = IntPtr.Zero;

        // ★ 挂载时记录的内层子句柄：Explorer 重建子层（根 HWND 保留）时该句柄会变，
        //   变了就说明"当前挂载已过期" ⇒ 必须重新解析并重挂（母计划 F04 第 2 步）。
        private IntPtr _attachedInner = IntPtr.Zero;

        private bool _isWin11;
        
        // 样式相关
        private bool _lastIsLightTheme = false;
        private Color _transparentKey = Color.Black;

        public int Height => _taskbarHeight;
        public Rectangle Rect => _taskbarRect;
        public IntPtr HandleTaskbar => _hTaskbar;

        /// <summary>挂载时记录的内层子句柄（诊断 / 测试用）。</summary>
        public IntPtr AttachedInnerHandle => _attachedInner;
        public Color TransparentKey => _transparentKey;
        public bool LastIsLightTheme => _lastIsLightTheme;

        public TaskbarBizHelper(Form form, Settings cfg, TaskbarWinHelper winHelper)
        {
            _form = form;
            _cfg = cfg;
            _winHelper = winHelper;
            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);
        }

        // =================================================================
        // 样式与主题
        // =================================================================
        public void CheckTheme(bool force = false)
        {
            bool isLight = _winHelper.IsSystemLightTheme();
            if (!force && isLight == _lastIsLightTheme) return;
            _lastIsLightTheme = isLight;

            if (_cfg.TaskbarCustomStyle)
            {
                try 
                {
                    Color customColor = ColorTranslator.FromHtml(_cfg.TaskbarColorBg);
                    if (customColor.R == customColor.G && customColor.G == customColor.B)
                    {
                        int r = customColor.R;
                        int g = customColor.G;
                        int b = customColor.B;
                        if (b >= 255) b = 254; else b += 1;
                        _transparentKey = Color.FromArgb(r, g, b);
                    }
                    else
                    {
                        _transparentKey = customColor;
                    }
                } 
                catch { _transparentKey = Color.Black; }
            }
            else
            {
                if (isLight) _transparentKey = Color.FromArgb(210, 210, 211); 
                else _transparentKey = Color.FromArgb(40, 40, 41);       
            }

            _winHelper.ApplyLayeredStyle(_transparentKey, _cfg.TaskbarClickThrough);
        }

        // =================================================================
        // 布局与定位
        // =================================================================
        public void FindHandles()
        {
            var handles = _winHelper.FindHandles(_cfg.TaskbarMonitorDevice);
            _hTaskbar = handles.hTaskbar;
            _hTray = handles.hTray;
        }

        /// <summary>
        /// 解析目标任务栏宿主（P-F04）。
        /// <para>★ 目标设备不存在时返回"未就绪"，**不回退主屏**（#573 / #583）。</para>
        /// </summary>
        public TaskbarHostResolution ResolveTarget()
        {
            string device = _cfg.TaskbarMonitorDevice ?? string.Empty;
            bool specified = !string.IsNullOrEmpty(device);

            TaskbarMonitorInfo? monitor = specified
                ? _winHelper.FindMonitor(device)
                : _winHelper.FindPrimaryMonitor();

            // 自动模式（未指定设备）⇒ 主屏始终视为就绪；指定模式 ⇒ 目标显示器必须真的存在。
            bool monitorPresent = specified ? monitor.HasValue : true;

            var handles = _winHelper.FindHandles(device);
            IntPtr root = handles.hTaskbar;
            IntPtr inner = handles.hTray;

            bool rootAlive = root != IntPtr.Zero && TaskbarWinHelper.IsWindow(root);
            bool innerAlive = inner == IntPtr.Zero || TaskbarWinHelper.IsWindow(inner);

            return new TaskbarHostResolution(root, inner, specified, monitorPresent, rootAlive, innerAlive);
        }

        /// <summary>
        /// 面板当前是否**确实**挂在目标任务栏上。
        /// <para>★ 不能只看"根 HWND 非零/有效"：必须同时校验
        /// ① 实际父子关系 ② 内层子句柄 ③ 目标显示器 ④ 面板自身句柄有效。</para>
        /// </summary>
        public bool IsPanelAttached(TaskbarHostResolution resolution)
        {
            if (!resolution.IsReady) return false;
            if (!_form.IsHandleCreated) return false;
            if (!TaskbarWinHelper.IsWindow(_form.Handle)) return false;

            // ① 实际父子关系
            if (!_winHelper.IsAttachedToTaskbar(resolution.Root)) return false;

            // ② 内层子句柄（子层被重建 ⇒ 挂载已过期）
            if (resolution.Inner != IntPtr.Zero && !resolution.InnerAlive) return false;
            if (resolution.Inner != _attachedInner) return false;

            // ③ 目标显示器（挂错屏 ⇒ 视为未挂好，必须重解析）
            if (!IsOnTargetMonitor(resolution)) return false;

            return true;
        }

        /// <summary>
        /// 当前任务栏根窗口是否位于**用户指定的目标显示器**上。
        /// <para>★ 自动模式（未指定设备）不做该检查。</para>
        /// </summary>
        public bool IsOnTargetMonitor(TaskbarHostResolution resolution)
        {
            string device = _cfg.TaskbarMonitorDevice ?? string.Empty;
            if (string.IsNullOrEmpty(device)) return true;

            var target = _winHelper.FindMonitor(device);
            if (!target.HasValue) return false;    // 目标显示器不存在 ⇒ 尚未就绪

            if (!_winHelper.GetWindowRectWrapper(resolution.Root, out Rectangle rootRect)) return false;

            return target.Value.Bounds.Contains(rootRect.Location) ||
                   target.Value.Bounds.IntersectsWith(rootRect);
        }

        /// <summary>面板自身的 HWND 是否仍然是有效窗口。</summary>
        public bool IsPanelHandleAlive()
        {
            if (!_form.IsHandleCreated) return false;
            return TaskbarWinHelper.IsWindow(_form.Handle);
        }

        /// <summary>按解析结果执行一次挂载（重解析后的重挂路径）。</summary>
        public void AttachToResolved(TaskbarHostResolution resolution)
        {
            if (!resolution.IsReady) return;

            _hTaskbar = resolution.Root;
            _hTray = resolution.Inner;
            _attachedInner = resolution.Inner;

            // 任务栏 HWND 可能换了 ⇒ 旧的任务栏矩形缓存必须失效（#539）。
            _winHelper.InvalidateTaskbarRectCache();

            _winHelper.AttachToTaskbar(resolution.Root);
            UpdateTaskbarRect();
        }

        public bool IsTaskbarValid()
        {
            if (_hTaskbar == IntPtr.Zero) return false;
            return TaskbarWinHelper.IsWindow(_hTaskbar);
        }

        public void AttachToTaskbar()
        {
            if (_hTaskbar == IntPtr.Zero) FindHandles();
            if (_hTaskbar == IntPtr.Zero) return;
            _winHelper.AttachToTaskbar(_hTaskbar);
        }

        public void UpdateTaskbarRect()
        {
            _taskbarRect = _winHelper.GetTaskbarRect(_hTaskbar, _cfg.TaskbarMonitorDevice);
            _taskbarHeight = Math.Max(24, _taskbarRect.Height);
        }

        public bool IsVertical()
        {
            return _taskbarRect.Height > _taskbarRect.Width;
        }

        public void UpdatePlacement(int panelWidth)
        {
            if (_hTaskbar == IntPtr.Zero) return;

            int leftScreen = _taskbarRect.Left;
            int topScreen;

            // ★★★ 垂直任务栏定位 ★★★
            if (IsVertical())
            {
                // 如果策略自带布局逻辑 (如 Win10 挤占模式)，直接委托处理
                if (_winHelper.UsesInternalLayout)
                {
                    // 注意：垂直模式下，Width 是固定的（任务栏宽度），Height 是 Monitor 高度
                    _winHelper.SetPosition(_hTaskbar, 0, 0, _taskbarRect.Width, _form.Height, _cfg.TaskbarManualOffset, _cfg.TaskbarAlignLeft);
                    return;
                }

                // 尝试定位到托盘上方
                int bottomLimit = _taskbarRect.Bottom;
                
                if (_hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle trayRect))
                {
                    if (trayRect.Top >= _taskbarRect.Top && trayRect.Bottom <= _taskbarRect.Bottom)
                    {
                        bottomLimit = trayRect.Top;
                    }
                }

                topScreen = bottomLimit - _form.Height - _cfg.TaskbarManualOffset - 6;
                if (topScreen < _taskbarRect.Top) topScreen = _taskbarRect.Top;

                _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, _taskbarRect.Width, _form.Height);
                return;
            }

            // ★★★ 水平任务栏定位 ★★★

            var currentMonitor = _winHelper.Screens.GetMonitorForRectangle(_taskbarRect)
                                 ?? _winHelper.FindPrimaryMonitor();
            bool isPrimary = currentMonitor?.IsPrimary ?? true;

            bool sysCentered = TaskbarWinHelper.IsCenterAligned();
            
            int rawWidgetWidth = TaskbarWinHelper.GetWidgetsWidth();      
            int manualOffset = _cfg.TaskbarManualOffset; 
            int leftModeTotalOffset = rawWidgetWidth + manualOffset;
            int sysRightAvoid = sysCentered ? 0 : rawWidgetWidth;
            int rightModeTotalOffset = sysRightAvoid + manualOffset;

            int timeWidth = _isWin11 ? 90 : 0; 
            bool alignLeft = _cfg.TaskbarAlignLeft && sysCentered; 

            topScreen = _taskbarRect.Top;

            if (alignLeft)
            {
                int startX = _taskbarRect.Left + 6;
                if (leftModeTotalOffset > 0) startX += leftModeTotalOffset;
                leftScreen = startX;
            }
            else
            {
                // 如果策略自带布局逻辑 (如 Win10 挤占模式)，直接委托处理
                // 我们不需要计算 Right 偏移，位置由 Strategy 内部决定
                if (_winHelper.UsesInternalLayout)
                {
                    _winHelper.SetPosition(_hTaskbar, 0, 0, panelWidth, _taskbarHeight, _cfg.TaskbarManualOffset, _cfg.TaskbarAlignLeft);
                    return;
                }

                if (isPrimary && _hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle tray))
                {
                    leftScreen = tray.Left - panelWidth - 6;
                    leftScreen -= rightModeTotalOffset;
                }
                else
                {
                    leftScreen = _taskbarRect.Right - panelWidth - 10;
                    leftScreen -= rightModeTotalOffset;
                    leftScreen -= timeWidth;
                }
            }

            _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, panelWidth, _taskbarHeight);
        }

        public void BuildVerticalLayout(List<Column> cols)
        {
            var s = _cfg.GetStyle(); 
            
            int w = _taskbarRect.Width;
            if (w < 20) w = 60; 

            int itemHeight = (int)(s.Size * 1.5f + 6); 
            if (itemHeight < 20) itemHeight = 20;

            int margin = Math.Max(0, s.Inner / 2);
            int contentWidth = w - (margin * 2);

            int y = 0;
            foreach (var col in cols)
            {
                col.Bounds = Rectangle.Empty;
                col.BoundsTop = Rectangle.Empty;
                col.BoundsBottom = Rectangle.Empty;

                if (col.Top != null)
                {
                    col.BoundsTop = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Bottom != null)
                {
                    col.BoundsBottom = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Top != null && col.Bottom == null)
                {
                    col.Bounds = col.BoundsTop;
                }

                y += s.VOff;
            }
            _form.Width = w;
            _form.Height = y;
        }

        // =================================================================
        // 交互动作
        // =================================================================
        public async void HandleDoubleClick(MainForm mainForm, UIController ui)
        {
            switch (_cfg.TaskbarDoubleClickAction)
            {
                case 1: try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { } break;
                case 2: 
                    foreach (Form f in Application.OpenForms) { if (f is SettingsForm) { f.Activate(); return; } }
                    new SettingsForm(_cfg, ui, mainForm).Show(); 
                    break;
                case 3: 
                    foreach (Form f in Application.OpenForms) { if (f is TrafficHistoryForm) { f.Activate(); return; } }
                    new TrafficHistoryForm(_cfg).Show(); 
                    break;
                case 4: 
                    try { using (var form = new CleanMemoryForm()) await form.StartCleaningAsync(); } catch { } 
                    break;
                case 5:
                    Core.Actions.WebActions.OpenWebMonitor(_cfg);
                    break;
                case 0: 
                default:
                    if (mainForm.Visible) mainForm.HideMainWindow(); else mainForm.ShowMainWindow();
                    break;
            }
        }
    }
}
