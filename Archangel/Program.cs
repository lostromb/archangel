using Durandal.API;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Archangel
{
    static class Program
    {
        private static NotifyIcon _trayIcon;
        private static ContextMenu _trayMenu;
        private static MainApp _app;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        public static void Main()
        {
            // Is another instance of this app already running?
            Process[] sharedInstances = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
            if (sharedInstances.Length > 1)
            {
                // If so, die
                Environment.Exit(-1);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Create a simple tray menu with only one item.
            _trayMenu = new ContextMenu();
            //_trayMenu.MenuItems.Add("Quit", TrayMenuExit);
            
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "Archangel";
            _trayIcon.Icon = Icons.icon;

            // Add menu to tray icon and show it.
            _trayIcon.ContextMenu = _trayMenu;
            _trayIcon.Visible = true;
            _trayIcon.MouseClick += TrayMouseDown;

            using (_app = new MainApp())
            {
                Application.ApplicationExit += ApplicationShutdown;
                Application.Run();
            }
        }

        private static async void TrayMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button.HasFlag(MouseButtons.Left))
            {
                await _app.AnnounceTimeRemaining();
            }
        }

        private static void TrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button.HasFlag(MouseButtons.Left))
            {
                
            }
        }

        private static void TrayMenuExit(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private static void ApplicationShutdown(object sender, EventArgs e)
        {
            _app?.Dispose();
        }
    }
}
