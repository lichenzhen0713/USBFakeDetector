using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace UsbCheck
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArgument(args, "--selftest"))
            {
                string output = args != null && args.Length > 1 ? args[1] : null;
                Environment.ExitCode = SelfTest.Run(output);
                return;
            }

            bool demo = HasArgument(args, "--demo");

            int probeIndex = -1;
            string probeOutput = null;
            int runIndex = -1;
            string runMode = null;
            string runOutput = null;
            string fileRoot = null;
            string fileOutput = null;
            long fileMaxBytes = 0;
            string safeLetter = null;
            string safeOutput = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
                    {
                        int.TryParse(args[i + 1], out probeIndex);
                        probeOutput = args[i + 2];
                        break;
                    }
                }

                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--run", StringComparison.OrdinalIgnoreCase) && i + 3 < args.Length)
                    {
                        int.TryParse(args[i + 1], out runIndex);
                        runMode = args[i + 2];
                        runOutput = args[i + 3];
                        break;
                    }
                }

                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--file", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
                    {
                        fileRoot = args[i + 1];
                        fileOutput = args[i + 2];
                        if (i + 3 < args.Length) long.TryParse(args[i + 3], out fileMaxBytes);
                        break;
                    }
                }

                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--safe", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
                    {
                        safeLetter = args[i + 1];
                        safeOutput = args[i + 2];
                        break;
                    }
                }
            }

            if (!demo && !IsAdministrator() &&
                Environment.GetEnvironmentVariable("USBCHECK_SKIP_ELEVATION") != "1")
            {
                if (!RelaunchAsAdministrator(args))
                {
                    MessageBox.Show(
                        "本软件需要管理员权限才能直接读写U盘扇区。\r\n\r\n" +
                        "请关闭本窗口，右键程序选择“以管理员身份运行”。",
                        "需要管理员权限",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            if (probeIndex >= 0 && !string.IsNullOrEmpty(probeOutput))
            {
                Diagnostics.RunProbe(probeIndex, probeOutput);
                return;
            }

            if (runIndex >= 0 && !string.IsNullOrEmpty(runOutput) && runMode != null)
            {
                bool full = string.Equals(runMode, "full", StringComparison.OrdinalIgnoreCase);
                Environment.ExitCode = Diagnostics.RunDetection(runIndex, full, runOutput);
                return;
            }

            if (!string.IsNullOrEmpty(fileRoot) && !string.IsNullOrEmpty(fileOutput))
            {
                Environment.ExitCode = Diagnostics.RunFileDetection(fileRoot, fileMaxBytes, fileOutput);
                return;
            }

            if (!string.IsNullOrEmpty(safeLetter) && !string.IsNullOrEmpty(safeOutput))
            {
                Environment.ExitCode = Diagnostics.RunSafeScan(safeLetter, safeOutput);
                return;
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("程序发生未处理的错误：\r\n\r\n" + e.Exception.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm(demo));
        }

        private static bool HasArgument(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool RelaunchAsAdministrator(string[] args)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Assembly.GetExecutingAssembly().Location;
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (args != null && args.Length > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (string arg in args)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        if (arg.IndexOf(' ') >= 0 || arg.IndexOf('"') >= 0)
                            sb.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                        else
                            sb.Append(arg);
                    }
                    info.Arguments = sb.ToString();
                }
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is Win32Exception))
                {
                    MessageBox.Show("无法以管理员权限启动：\r\n\r\n" + ex.Message,
                        "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false;
            }
        }
    }
}



