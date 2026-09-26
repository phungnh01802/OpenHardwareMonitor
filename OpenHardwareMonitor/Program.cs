using System;
using System.Windows.Forms;
using OpenHardwareMonitor.UI;

namespace OpenHardwareMonitor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Crasher.Listen();

        bool disableDriver = HasNoDriverArgument(args);

        if (!OSHelper.IsCompatible(false, out string errorMessage, out var fixAction))
        {
            if (fixAction != null)
            {
                if (MessageBox.Show(errorMessage, Updater.ApplicationName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    fixAction();
                }
            }
            else
            {
                MessageBox.Show(errorMessage, Updater.ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Environment.Exit(0);
        }

        if (!System.Diagnostics.Debugger.IsAttached && WinApiHelper.CheckRunningInstances(true, true))
        {
            // fallback
            MessageBox.Show($"{Updater.ApplicationName} is already running.", Updater.ApplicationName,
              MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (MainForm form = new MainForm(disableDriver))
        {
            form.FormClosed += delegate
            {
                Application.Exit();
            };
            Application.Run();
        }
    }

    // Allows starting the application without loading the WinRing0 kernel driver,
    // which Windows Defender reports as "VulnerableDriver:WinNT/WinRing0".
    private static bool HasNoDriverArgument(string[] args)
    {
        if (args == null)
            return false;

        foreach (string arg in args)
        {
            switch (arg?.Trim().ToLowerInvariant())
            {
                case "--no-driver":
                case "--no-ring0":
                case "-nd":
                    return true;
            }
        }

        return false;
    }
}
