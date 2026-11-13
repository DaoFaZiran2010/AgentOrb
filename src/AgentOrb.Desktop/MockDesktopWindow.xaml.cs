using System.ComponentModel;
using System.Windows;
using AgentOrb.Core;

namespace AgentOrb.Desktop;

public partial class MockDesktopWindow : Window
{
    private bool _allowClose;

    public MockDesktopWindow()
    {
        InitializeComponent();
    }

    public void UpdateStatus(AgentStatusEvent status)
    {
        ActivityText.Text = $"当前演示状态：{status.Message}\n来源：{status.ToolName ?? "demo"} · 此状态不对应真实桌面操作";
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
