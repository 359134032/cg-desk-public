using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace CgDesk;

public partial class App : System.Windows.Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Windows.MessageBox.Show("窗口台没有继续运行。\n" + args.Exception.Message, "魔力窗口台");
            if (Windows.Count == 0)
            {
                Shutdown();
            }
        };

        if (!IsAdmin())
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                System.Windows.MessageBox.Show("窗口台没有打开。\n找不到程序自己的路径。", "魔力窗口台");
                Shutdown();
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            Shutdown();
            return;
        }

        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("窗口台没有打开。\n" + ex.Message, "魔力窗口台");
            Shutdown();
        }
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
