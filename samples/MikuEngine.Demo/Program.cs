namespace MikuEngine.Demo;

internal static class Program
{
    /// <summary>
    /// 应用入口：只负责起 WinForms 消息循环。窗口内容见 <see cref="MainForm"/>。
    /// 高 DPI 模式由 csproj 的 &lt;ApplicationHighDpiMode&gt; 提供，经
    /// <see cref="ApplicationConfiguration.Initialize"/> 生效。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
