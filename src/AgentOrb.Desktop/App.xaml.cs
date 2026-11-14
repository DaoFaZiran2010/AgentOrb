using System.Windows;
using System.Windows.Threading;
using AgentOrb.Anode;
using AgentOrb.Core;

namespace AgentOrb.Desktop;

public partial class App : System.Windows.Application
{
    private IDesktopController? _desktopController;
    private NamedPipeStatusListener? _statusListener;
    private DemoStatusPublisher? _demoStatusPublisher;
    private MainWindow? _mainWindow;
    private Task? _shutdownTask;
    private SingleInstanceGuard? _singleInstanceGuard;
    private bool _fatalErrorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var isDemo = e.Args.Any(arg => string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase));

        try
        {
            _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
            if (!_singleInstanceGuard.IsOwner)
            {
                var activated = _singleInstanceGuard.ActivateExisting();

                // The Agent seat logs in as the same user, so the per-user Run key starts a
                // second copy there. That copy must exit silently: the real UI lives in the
                // console session and a dialog here would linger in the seat forever.
                if (!activated && SingleInstanceGuard.IsRunningInConsoleSession())
                {
                    System.Windows.MessageBox.Show(
                        "AgentOrb 已在运行，但未能找到现有窗口。请从托盘菜单退出旧实例后重试。",
                        "AgentOrb 单实例提示",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }

                _singleInstanceGuard.Dispose();
                _singleInstanceGuard = null;
                Shutdown();
                return;
            }

            _desktopController = isDemo
                ? new DemoDesktopController()
                : new AnodeDesktopController();
            _mainWindow = new MainWindow(_desktopController, isDemo);
            _mainWindow.Show();

            if (isDemo)
            {
                _demoStatusPublisher = new DemoStatusPublisher(_mainWindow);
                _demoStatusPublisher.Start();
            }
            else
            {
                _statusListener = new NamedPipeStatusListener(_mainWindow);
                _statusListener.Start();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"AgentOrb 启动失败：{ex.Message}",
                "AgentOrb 启动错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            _mainWindow?.PrepareForShutdown();
            _mainWindow?.Close();
            _singleInstanceGuard?.Dispose();
            _singleInstanceGuard = null;
            Shutdown(-1);
            Environment.Exit(-1);
        }
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _mainWindow?.PrepareForShutdown();

        try
        {
            if (_demoStatusPublisher is not null)
            {
                await _demoStatusPublisher.StopAsync().ConfigureAwait(true);
            }

            if (_statusListener is not null)
            {
                await _statusListener.StopAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // 退出必须继续；OnExit 和下面的 Dispose 会做最后清理。
        }
        finally
        {
            _demoStatusPublisher?.Dispose();
            _statusListener?.Dispose();
        }

        try
        {
            (_desktopController as IDisposable)?.Dispose();
            _mainWindow?.Close();
        }
        finally
        {
            Shutdown();
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)    {
        e.Handled = true;

        if (_fatalErrorShown || _shutdownTask is not null)
        {
            return;
        }

        _fatalErrorShown = true;
        System.Windows.MessageBox.Show(
            $"AgentOrb 遇到界面异常：{e.Exception.Message}\n\n程序将尝试安全退出。",
            "AgentOrb 界面错误",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);

        if (_mainWindow is null || !_mainWindow.IsVisible)
        {
            Shutdown(-1);
            return;
        }

        _ = ShutdownAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demoStatusPublisher?.Dispose();
        _statusListener?.Dispose();
        (_desktopController as IDisposable)?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }
}
