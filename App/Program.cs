using System;
using System.Threading;
using System.Windows.Forms;
using Nitrous.Managers;
using Nitrous.Ui;

namespace Nitrous;

static class Program
{
    // Declare the Mutex at the class level so the garbage collector doesn't destroy it
    private static Mutex? mutex = null;

    [STAThread]
    static void Main()
    {
        const string appName = "Nitrous_SingleInstance_Mutex_Lock";
        bool createdNew;

        // Attempt to create a unique system-wide lock
        mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Another instance is already running! Exit silently and immediately.
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!AcerWmiManager.IsHardwareSupported())
        {
            MessageBox.Show("Acer WMI instances not found. This app only works on supported Acer hardware.",
                            "Hardware Not Supported", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayApplication());

        // Ensure the mutex stays alive until the application physically exits
        GC.KeepAlive(mutex);
    }
}
