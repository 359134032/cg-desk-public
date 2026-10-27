using System.Windows;
using CgDesk.Models;
using CgDesk.Services;

namespace CgDesk;

public partial class UpdateSettingsWindow : Window
{
    public string Primary => PrimaryBox.Text.Trim();
    public string Backup => BackupBox.Text.Trim();
    public string Channel => ChannelBox.IsChecked == true ? "beta" : "stable";
    public UpdateSettingsWindow(AppConfig config)
    {
        InitializeComponent();
        PrimaryBox.Text = config.UpdateManifestUrl;
        BackupBox.Text = config.UpdateBackupManifestUrl;
        ChannelBox.IsChecked = config.UpdateChannel == "beta";
        SourceInitialized += (_, _) => DeskTheme.ApplyTitleBar(this);
    }
    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        PrimaryBox.Text = UpdateSourceDefaults.Official;
        ErrorText.Text = "已选择官方公开发布源，可保存后检查更新。国内连接不畅时可配置备用镜像。";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Primary.Length > 0) UpdateService.PublicHttps(Primary);
            if (Backup.Length > 0) UpdateService.PublicHttps(Backup);
            if (Primary.Length == 0 && Backup.Length > 0) throw new UpdateException("请先填写主清单地址。");
            DialogResult = true;
        }
        catch (UpdateException error) { ErrorText.Text = error.Message; }
    }
}
