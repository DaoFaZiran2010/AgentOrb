using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentOrb.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AgentOrb.Desktop;

public partial class MainWindow : Window, IAgentStatusSink
{
    private const double IslandExpandedWidth = 360;
    private const double IslandExpandedHeight = 64;
    private const double IslandCollapsedWidth = 72;
    private const double IslandCollapsedHeight = 34;
    private const double SnapThreshold = 28;
    private const int HotKeyId = 0x4F4F;
    private const int WmHotKey = 0x0312;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;

    private readonly IDesktopController _desktopController;
    private readonly bool _isDemo;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _islandTimer;
    private IntPtr _previousForegroundWindow;
    private DateTimeOffset _activityStartedAt;
    private DateTimeOffset? _autoCollapseAt;
    private AgentState _currentState = AgentState.Disconnected;
    private bool _hovering;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggleItem;
    private HwndSource? _windowSource;
    private bool _hotKeyRegistered;
    private bool _viewerVisible;
    private bool _expanded;
    private bool _trackingPointer;
    private bool _dragged;
    private bool _menuOpen;
    private bool _shutdownRequested;
    private System.Windows.Point _pointerStart;
    private double _dragStartLeft;
    private double _dragStartTop;
    private FrameworkElement? _captureElement;

    public MainWindow(IDesktopController desktopController, bool isDemo)
    {
        _desktopController = desktopController;
        _isDemo = isDemo;
        InitializeComponent();

        Title = isDemo ? "AgentOrb 灵动岛（演示模式）" : "AgentOrb 灵动岛";
        DesktopSummaryText.Text = isDemo
            ? "演示模式 · 点击查看/隐藏"
            : "点击查看 AI 桌面";

        _islandTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _islandTimer.Tick += IslandTimer_Tick;
        _islandTimer.Start();

        ApplyStatus(AgentStatusEvent.Create(
            isDemo ? AgentState.Waiting : AgentState.Disconnected,
            isDemo ? "演示状态准备中" : "等待 Agent 状态桥接",
            isDemo ? "demo.startup" : "desktop.pipe",
            isDemo ? "demo-agent" : "unknown",
            isDemo ? "演示 Agent（非 Anode）" : "AgentOrb"));

        SourceInitialized += MainWindow_SourceInitialized;
        DpiChanged += MainWindow_DpiChanged;
    }

    public ValueTask PublishAsync(AgentStatusEvent status, CancellationToken cancellationToken = default)
    {
        if (_shutdownRequested || cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
        {
            return ValueTask.CompletedTask;
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyStatus(status);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    if (!_shutdownRequested)
                    {
                        ApplyStatus(status);
                    }
                },
                DispatcherPriority.DataBind,
                cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public void PrepareForShutdown()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        _lifetime.Cancel();
        _islandTimer.Stop();
        MainContextMenu.IsOpen = false;
        DisposeTray();
        UnregisterGlobalHotKey();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownRequested)
        {
            e.Cancel = true;
            _ = ((App)System.Windows.Application.Current).ShutdownAsync();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _islandTimer.Stop();
        DisposeTray();
        UnregisterGlobalHotKey();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;
        if (!_shutdownRequested && !_menuOpen)
        {
            ExpandBubble();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovering = false;
        if (!_menuOpen && !IsActiveState(_currentState) && _autoCollapseAt is null)
        {
            CollapseBubble();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyOverlayWindowStyles();
        PlaceAtDefaultPosition();
        RegisterGlobalHotKey();
        InitializeTray();
    }

    private void ApplyOverlayWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    private void MainWindow_DpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CollapseBubble();
            PlaceAtDefaultPosition();
        }), DispatcherPriority.Render);
    }

    private void ApplyStatus(AgentStatusEvent status)
    {
        var previousState = _currentState;
        _currentState = status.State;
        AgentNameText.Text = string.IsNullOrWhiteSpace(status.AgentName) ? "Agent" : status.AgentName;
        StateText.Text = StateToChinese(status.State);
        MessageText.Text = string.IsNullOrWhiteSpace(status.Message) ? StateToChinese(status.State) : status.Message;
        ToolNameText.Text = string.IsNullOrWhiteSpace(status.ToolName) ? "status pipe" : status.ToolName;
        CompactStateText.Text = GetCompactStateText(status.State);

        var stateBrush = GetStateBrush(status.State);
        DotStateEllipse.Fill = stateBrush;
        ExpandedStateEllipse.Fill = stateBrush;
        CompactStateText.Foreground = stateBrush;

        var now = DateTimeOffset.UtcNow;
        if (IsActiveState(status.State))
        {
            if (!IsActiveState(previousState))
            {
                _activityStartedAt = now;
            }

            _autoCollapseAt = null;
            ExpandBubble();
        }
        else if (status.State == AgentState.Completed)
        {
            if (_activityStartedAt == default)
            {
                _activityStartedAt = now;
            }

            ExpandBubble();
            _autoCollapseAt = now.AddSeconds(5);
        }
        else if (status.State == AgentState.Error)
        {
            ExpandBubble();
            _autoCollapseAt = now.AddSeconds(8);
        }
        else if (status.State == AgentState.Idle)
        {
            if (_activityStartedAt != default || status.Message.Contains("已连接", StringComparison.Ordinal))
            {
                ExpandBubble();
                _autoCollapseAt = now.AddSeconds(4);
            }
        }
        else if (status.State == AgentState.Disconnected)
        {
            _autoCollapseAt = null;
            if (!_hovering && !_menuOpen)
            {
                CollapseBubble();
            }
        }

        UpdateElapsedText();

        if (_desktopController is DemoDesktopController demoController)
        {
            demoController.UpdateStatus(status);
        }
    }

    private void IslandTimer_Tick(object? sender, EventArgs e)
    {
        UpdateElapsedText();
        if (_autoCollapseAt is { } deadline
            && DateTimeOffset.UtcNow >= deadline
            && !_hovering
            && !_menuOpen)
        {
            _autoCollapseAt = null;
            CollapseBubble();
        }
    }

    private void UpdateElapsedText()
    {
        if (_activityStartedAt == default)
        {
            ElapsedText.Text = string.Empty;
            CompactTimerText.Text = string.Empty;
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _activityStartedAt;
        var text = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        ElapsedText.Text = text;
        CompactTimerText.Text = text;
    }

    private void ExpandBubble()
    {
        if (_expanded || _shutdownRequested)
        {
            return;
        }

        _expanded = true;
        CompactContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        AnimateIsland(IslandExpandedWidth, IslandExpandedHeight, expand: true);
    }

    private void CollapseBubble()
    {
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        if (!IsActiveState(_currentState))
        {
            _activityStartedAt = default;
        }

        CompactContent.Visibility = Visibility.Visible;
        AnimateIsland(IslandCollapsedWidth, IslandCollapsedHeight, expand: false);
    }

    private void AnimateIsland(double targetWidth, double targetHeight, bool expand)
    {
        var duration = TimeSpan.FromMilliseconds(expand ? 260 : 200);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var width = new DoubleAnimation(IslandBorder.ActualWidth, targetWidth, duration)
        {
            EasingFunction = easing
        };
        var height = new DoubleAnimation(IslandBorder.ActualHeight, targetHeight, duration)
        {
            EasingFunction = easing
        };
        width.Completed += (_, _) =>
        {
            IslandBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
            IslandBorder.Width = targetWidth;
        };
        height.Completed += (_, _) =>
        {
            IslandBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
            IslandBorder.Height = targetHeight;
        };
        IslandBorder.BeginAnimation(FrameworkElement.WidthProperty, width, HandoffBehavior.SnapshotAndReplace);
        IslandBorder.BeginAnimation(FrameworkElement.HeightProperty, height, HandoffBehavior.SnapshotAndReplace);

        var opacity = new DoubleAnimation(expand ? 0 : 1, expand ? 1 : 0, duration)
        {
            EasingFunction = easing
        };
        opacity.Completed += (_, _) =>
        {
            if (expand)
            {
                ExpandedContent.BeginAnimation(OpacityProperty, null);
                ExpandedContent.Opacity = 1;
            }
            else
            {
                ExpandedContent.BeginAnimation(OpacityProperty, null);
                ExpandedContent.Opacity = 0;
                ExpandedContent.Visibility = Visibility.Collapsed;
            }
        };
        ExpandedContent.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlaceAtDefaultPosition()
    {
        var workArea = GetWorkArea();
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + 2;
    }

    private Rect GetWorkArea()
    {
        if (Forms.Screen.AllScreens.Length == 0)
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? Forms.Screen.PrimaryScreen : Forms.Screen.FromHandle(handle);
        screen ??= Forms.Screen.AllScreens[0];
        var area = screen.WorkingArea;

        return new Rect(
            area.X / dpi.DpiScaleX,
            area.Y / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX,
            area.Height / dpi.DpiScaleY);
    }

    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shutdownRequested)
        {
            return;
        }

        _trackingPointer = true;
        _dragged = false;
        _pointerStart = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _captureElement = (FrameworkElement)sender;
        _captureElement.CaptureMouse();
        e.Handled = true;
    }

    private async void IslandBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _shutdownRequested)
        {
            return;
        }

        e.Handled = true;
        await ToggleDesktopAsync();
    }

    private void IslandBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MainContextMenu.PlacementTarget = (FrameworkElement)sender;
        MainContextMenu.IsOpen = true;
    }

    private void RootGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_trackingPointer)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetPointerTracking();
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (current.X - _pointerStart.X) / dpi.DpiScaleX;
        var deltaY = (current.Y - _pointerStart.Y) / dpi.DpiScaleY;

        if (!_dragged &&
            (Math.Abs(deltaX) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(deltaY) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _dragged = true;
            if (_captureElement is not null)
            {
                _captureElement.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
        }

        if (_dragged)
        {
            Left = _dragStartLeft + deltaX;
            Top = _dragStartTop + deltaY;
        }

        e.Handled = true;
    }

    private async void RootGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_trackingPointer)
        {
            return;
        }

        var wasDragged = _dragged;
        ResetPointerTracking();
        e.Handled = true;

        if (wasDragged)
        {
            FinishDrag();
        }
        else
        {
            await ToggleDesktopAsync();
        }
    }

    private void RootGrid_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_trackingPointer && ReferenceEquals(_captureElement, sender) && _captureElement?.IsMouseCaptured == false)
        {
            ResetPointerTracking();
        }
    }

    private void ResetPointerTracking()
    {
        _trackingPointer = false;
        _dragged = false;
        if (_captureElement is not null)
        {
            _captureElement.Cursor = null;
            if (_captureElement.IsMouseCaptured)
            {
                _captureElement.ReleaseMouseCapture();
            }
        }
        _captureElement = null;
    }

    private void FinishDrag()
    {
        var workArea = GetWorkArea();
        if (Left <= workArea.Left + SnapThreshold)
        {
            Left = workArea.Left;
        }
        else if (Left + Width >= workArea.Right - SnapThreshold)
        {
            Left = workArea.Right - Width;
        }

        Top = Math.Clamp(Top, workArea.Top, workArea.Bottom - Height);
        CollapseBubble();
    }

    private void MainContextMenu_Opened(object sender, RoutedEventArgs e) => _menuOpen = true;

    private void MainContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsMouseOver && !_trackingPointer && !IsActiveState(_currentState))
            {
                CollapseBubble();
            }
        }), DispatcherPriority.Input);
    }

    private void ExpandedBorder_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        MainContextMenu.PlacementTarget = (FrameworkElement)sender;
        MainContextMenu.IsOpen = true;
    }

    private async void StartDesktopMenuItem_Click(object sender, RoutedEventArgs e) => await StartDesktopAsync();

    private async void ToggleDesktopMenuItem_Click(object sender, RoutedEventArgs e) => await ToggleDesktopAsync();

    private async void EmergencyStopMenuItem_Click(object sender, RoutedEventArgs e) => await EmergencyStopAsync();

    private async void ExitMenuItem_Click(object sender, RoutedEventArgs e) => await ((App)System.Windows.Application.Current).ShutdownAsync();

    private async Task StartDesktopAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var status = await _desktopController.GetStatusAsync(_lifetime.Token);
            if (ReportIfUnavailable(status))
            {
                return;
            }

            await _desktopController.StartHiddenAsync(_lifetime.Token);
            UpdateControllerStatus(await _desktopController.GetStatusAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_shutdownRequested)
        {
        }
        catch (Exception ex)
        {
            ShowOperationError("启动桌面", ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task ToggleDesktopAsync()
    {
        if (_shutdownRequested || !await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var status = await _desktopController.GetStatusAsync(_lifetime.Token);
            if (ReportIfUnavailable(status))
            {
                return;
            }

            if (status.IsViewerVisible)
            {
                await _desktopController.HideAsync(_lifetime.Token);
                await Task.Delay(100, _lifetime.Token);
                RestorePreviousForegroundWindow();
            }
            else
            {
                _previousForegroundWindow = GetForegroundWindow();
                await _desktopController.ShowAsync(_lifetime.Token);
            }

            UpdateControllerStatus(await _desktopController.GetStatusAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_shutdownRequested)
        {
        }
        catch (Exception ex)
        {
            ShowOperationError("切换桌面查看器", ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task EmergencyStopAsync()
    {
        if (!await _operationLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var status = await _desktopController.GetStatusAsync(_lifetime.Token);
            if (ReportIfUnavailable(status))
            {
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                this,
                "紧急停止会立即终止当前桌面任务并隐藏查看器，未保存的任务状态可能丢失。\n\n确认执行紧急停止吗？",
                "AgentOrb · 确认紧急停止",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            await _desktopController.StopAsync(_lifetime.Token);
            UpdateControllerStatus(await _desktopController.GetStatusAsync(_lifetime.Token));
            DesktopSummaryText.Text = "已执行紧急停止 · 可通过右键菜单重新启动";
        }
        catch (OperationCanceledException) when (_shutdownRequested)
        {
        }
        catch (Exception ex)
        {
            ShowOperationError("紧急停止", ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private bool ReportIfUnavailable(DesktopStatus status)
    {
        if (status.IsInstalled || !string.Equals(status.State, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_shutdownRequested)
        {
            ApplyStatus(AgentStatusEvent.Create(
                AgentState.Disconnected,
                string.IsNullOrWhiteSpace(status.Summary) ? "Anode 尚未接入" : status.Summary,
                "desktop.unavailable",
                "unknown",
                "AgentOrb"));
        }

        return true;
    }

    private void UpdateControllerStatus(DesktopStatus status)
    {
        _viewerVisible = status.IsViewerVisible;
        var toggleText = _viewerVisible ? "隐藏桌面" : "查看桌面";
        ToggleDesktopMenuItem.Header = toggleText;
        if (_trayToggleItem is not null)
        {
            _trayToggleItem.Text = toggleText;
        }

        if (!string.IsNullOrWhiteSpace(status.Summary))
        {
            DesktopSummaryText.Text = status.Summary;
        }
    }

    private void RestorePreviousForegroundWindow()
    {
        var previous = _previousForegroundWindow;
        _previousForegroundWindow = IntPtr.Zero;
        if (previous != IntPtr.Zero && IsWindow(previous))
        {
            _ = SetForegroundWindow(previous);
        }
    }

    private void ShowOperationError(string operation, Exception ex)
    {
        if (!_shutdownRequested)
        {
            System.Windows.MessageBox.Show(
                this,
                $"{operation}失败：{ex.Message}",
                "AgentOrb 桌面操作错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void RegisterGlobalHotKey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _hotKeyRegistered = RegisterHotKey(
            handle,
            HotKeyId,
            ModControl | ModAlt | ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(Key.O));

        if (!_hotKeyRegistered)
        {
            System.Windows.MessageBox.Show(
                this,
                "全局热键 Ctrl+Alt+O 注册失败，可能已被其他程序占用。\n仍可使用悬浮球或托盘菜单切换查看器。",
                "AgentOrb 热键提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void UnregisterGlobalHotKey()
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        if (_hotKeyRegistered)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId);
            _hotKeyRegistered = false;
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(() => _ = ToggleDesktopAsync()), DispatcherPriority.Input);
        }

        return IntPtr.Zero;
    }

    private void InitializeTray()
    {
        try
        {
            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.Items.Add("启动桌面", null, async (_, _) => await StartDesktopAsync());
            _trayToggleItem = new Forms.ToolStripMenuItem(_viewerVisible ? "隐藏桌面" : "查看桌面");
            _trayToggleItem.Click += async (_, _) => await ToggleDesktopAsync();
            _trayMenu.Items.Add(_trayToggleItem);
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add("紧急停止", null, async (_, _) => await EmergencyStopAsync());
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, async (_, _) => await ((App)System.Windows.Application.Current).ShutdownAsync());

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = Drawing.SystemIcons.Application,
                Text = _isDemo ? "AgentOrb 演示监督（非真实桌面）" : "AgentOrb 悬浮监督",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    Dispatcher.BeginInvoke(new Action(() => _ = ToggleDesktopAsync()), DispatcherPriority.Input);
                }
            };
        }
        catch (Exception ex)
        {
            DisposeTray();
            System.Windows.MessageBox.Show(
                this,
                $"托盘图标初始化失败：{ex.Message}\n悬浮球仍可使用。",
                "AgentOrb 托盘提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void DisposeTray()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayToggleItem = null;
    }

    private static bool IsActiveState(AgentState state) => state is
        AgentState.Starting or
        AgentState.Stopping or
        AgentState.Observing or
        AgentState.Clicking or
        AgentState.Typing or
        AgentState.Launching or
        AgentState.Scrolling or
        AgentState.Waiting;

    private static string GetCompactStateText(AgentState state) => state switch
    {
        AgentState.Disconnected => "待连",
        AgentState.Stopped => "停",
        AgentState.Starting => "启",
        AgentState.Stopping => "停",
        AgentState.Idle => "闲",
        AgentState.Observing => "看",
        AgentState.Clicking => "点",
        AgentState.Typing => "输",
        AgentState.Launching => "启",
        AgentState.Scrolling => "滚",
        AgentState.Waiting => "等",
        AgentState.Completed => "完成",
        AgentState.Error => "错",
        _ => "AI"
    };

    private static string StateToChinese(AgentState state) => state switch
    {
        AgentState.Disconnected => "未连接",
        AgentState.Stopped => "已停止",
        AgentState.Starting => "启动中",
        AgentState.Stopping => "停止中",
        AgentState.Idle => "空闲",
        AgentState.Observing => "观察中",
        AgentState.Clicking => "点击中",
        AgentState.Typing => "输入中",
        AgentState.Launching => "启动应用",
        AgentState.Scrolling => "滚动中",
        AgentState.Waiting => "等待中",
        AgentState.Completed => "已完成",
        AgentState.Error => "异常",
        _ => "未知状态"
    };

    private static System.Windows.Media.Brush GetStateBrush(AgentState state) => state switch
    {
        AgentState.Error => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 104)),
        AgentState.Observing => new SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 210, 255)),
        AgentState.Clicking or AgentState.Typing or AgentState.Launching or AgentState.Scrolling =>
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 240, 177)),
        AgentState.Waiting or AgentState.Starting or AgentState.Stopping =>
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 202, 112)),
        AgentState.Completed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 240, 177)),
        AgentState.Idle => new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 190, 205)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(138, 145, 158))
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
