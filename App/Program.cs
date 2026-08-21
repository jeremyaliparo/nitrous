using System;
using System.Windows.Forms;
using Nitrous.Managers;
using Nitrous.Ui;

namespace Nitrous;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!AcerWmiManager.IsHardwareSupported())
        {
            MessageBox.Show("Acer WMI instances not found. This app only works on supported Acer hardware.",
                            "Hardware Not Supported", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayApplication());
    }
}
