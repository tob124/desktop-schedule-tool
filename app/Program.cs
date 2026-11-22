using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("日程表")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace ScdDesktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            NativeWindow.EnablePerMonitorDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScdDesktop");
                string source = null;
                for (int index = 0; index + 1 < args.Length; index++)
                {
                    if (args[index] == "--workbook") source = args[++index];
                    else if (args[index] == "--state-dir") directory = args[++index];
                }
                directory = Path.GetFullPath(directory);
                string suffix;
                using (var sha = SHA256.Create())
                {
                    suffix = Convert.ToBase64String(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("/", "_");
                }
                string key = @"Local\ScdDesktop-" + suffix;
                bool first;
                using (var owner = new Mutex(true, key, out first))
                using (var reopen = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-open"))
                {
                    if (!first)
                    {
                        reopen.Set();
                        return;
                    }
                    using (var window = new ScheduleWindow(directory, source))
                    {
                        var handle = window.Handle;
                        var wait = ThreadPool.RegisterWaitForSingleObject(reopen, delegate
                        {
                            try { window.BeginInvoke(new Action(window.ShowExpanded)); }
                            catch (InvalidOperationException) { }
                        }, null, Timeout.Infinite, false);
                        try { Application.Run(window); }
                        finally { wait.Unregister(null); }
                    }
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "无法打开日程挂件", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
