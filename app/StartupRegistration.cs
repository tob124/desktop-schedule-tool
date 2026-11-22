using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScdDesktop
{
    internal sealed class StartupRegistration
    {
        private readonly string directory;
        private readonly string executable;
        private readonly string link;

        public StartupRegistration(string directory, string executable)
        {
            this.directory = directory;
            this.executable = Path.GetFullPath(executable);
            link = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "日程表.lnk");
        }

        public bool IsEnabled()
        {
            return link != null && File.Exists(link) && ReadTarget() == executable;
        }

        public void SetEnabled(bool enabled)
        {
            if (link == null || !Directory.Exists(directory))
                throw new IOException("找不到当前用户的 Windows 启动文件夹。");
            if (File.Exists(link))
            {
                if (ReadTarget() != executable)
                    throw new IOException("启动文件夹中已有同名快捷方式，并指向其他程序。");
                if (!enabled) File.Delete(link);
                return;
            }
            if (!enabled) return;
            UseShortcut(delegate(dynamic shortcut)
            {
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                shortcut.IconLocation = executable + ",0";
                shortcut.Description = "打开桌面日程表";
                shortcut.Save();
                return null;
            });
        }

        private string ReadTarget()
        {
            var target = UseShortcut(delegate(dynamic shortcut) { return (string)shortcut.TargetPath; });
            return string.Equals(target, executable, StringComparison.OrdinalIgnoreCase) ? executable : target;
        }

        private string UseShortcut(Func<dynamic, string> operation)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            object shortcut = null;
            try
            {
                shortcut = ((dynamic)shell).CreateShortcut(link);
                return operation(shortcut);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
