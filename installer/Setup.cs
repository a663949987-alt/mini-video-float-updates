using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MiniVideoFloat");
    private static readonly string Player = Path.Combine(Root, "MiniVideoFloat", "MiniVideoFloat.exe");
    private const string AppName = "微型短视频播放器";
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MiniVideoFloat";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        if (Array.IndexOf(args, "--uninstall") >= 0) { Uninstall(); return; }
        if (Array.IndexOf(args, "--update") >= 0)
        {
            try
            {
                int index = Array.IndexOf(args, "--wait-pid");
                int id;
                if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out id))
                {
                    try { using (var p = Process.GetProcessById(id)) if (!p.WaitForExit(30000)) throw new Exception("播放器未退出，更新已停止。"); }
                    catch (ArgumentException) { }
                }
                Install(true);
                Process.Start(Player);
            }
            catch (Exception ex) { MessageBox.Show("自动更新失败：" + ex.Message, AppName); }
            return;
        }

        using (var form = new Form { Text = AppName + " 安装", Width = 450, Height = 210, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen })
        using (var label = new Label { Left = 22, Top = 20, Width = 390, Height = 75, Text = "安装微型播放器和红果播放组件。\r\n\r\n安装位置：" + Root })
        using (var install = new Button { Text = "安装", Width = 100, Height = 32, Left = 210, Top = 115 })
        using (var cancel = new Button { Text = "取消", Width = 90, Height = 32, Left = 320, Top = 115, DialogResult = DialogResult.Cancel })
        {
            form.Controls.AddRange(new Control[] { label, install, cancel });
            form.CancelButton = cancel;
            install.Click += async delegate
            {
                install.Enabled = false; cancel.Enabled = false; label.Text = "正在安装，请稍候……";
                try
                {
                    await Task.Run(() => Install(false));
                    MessageBox.Show(form, "安装完成。", AppName);
                    Process.Start(Player);
                    form.Close();
                }
                catch (Exception ex) { label.Text = "安装失败：" + ex.Message; install.Enabled = true; cancel.Enabled = true; }
            };
            Application.Run(form);
        }
    }

    private static void Install(bool update)
    {
        Directory.CreateDirectory(Root);
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (String.IsNullOrEmpty(entry.Name)) continue;
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包路径无效。");
                if (update && entry.FullName.StartsWith("hongguo-v1.2.17-windows-amd64/", StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (var source = entry.Open())
                using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(target);
            }
        }
        File.WriteAllText(Path.Combine(Root, "MiniVideoFloat", ".installed"), "1");
        var uninstall = Path.Combine(Root, "Uninstall.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, uninstall, true);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
        var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        CreateShortcut(start);
        using (var key = Registry.CurrentUser.CreateSubKey(RegistryKey))
        {
            key.SetValue("DisplayName", AppName); key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("DisplayIcon", Player); key.SetValue("InstallLocation", Root);
            key.SetValue("UninstallString", "\"" + uninstall + "\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static void CreateShortcut(string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(type);
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = Player; shortcut.WorkingDirectory = Path.GetDirectoryName(Player); shortcut.Save();
    }

    private static void Uninstall()
    {
        if (MessageBox.Show("卸载微型短视频播放器？", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            foreach (var p in Process.GetProcessesByName("MiniVideoFloat"))
            using (p) if (String.Equals(p.MainModule.FileName, Player, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("请先退出播放器，再卸载。", AppName); return; }
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false);
            foreach (var dir in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                var link = Path.Combine(Environment.GetFolderPath(dir), AppName + ".lnk");
                if (File.Exists(link)) File.Delete(link);
            }
            var cmd = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Root + "\"") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false };
            Process.Start(cmd);
            MessageBox.Show("播放器已卸载，红果的用户数据仍保留。", AppName);
        }
        catch (Exception ex) { MessageBox.Show("卸载失败：" + ex.Message, AppName); }
    }
}
