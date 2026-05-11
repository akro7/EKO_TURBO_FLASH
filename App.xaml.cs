using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EkoTurboTool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                CrashLog("Fatal", ex.ExceptionObject?.ToString());

            DispatcherUnhandledException += (s, ex) =>
            {
                CrashLog("UI", ex.Exception?.ToString());
                ex.Handled = true;
            };

            base.OnStartup(e);

            // AHMED YOUNIS
            // EKO TURBO 

            var win = new MainWindow();

            try
            {
                var sri = GetResourceStream(new Uri("app.ico", UriKind.Relative));
                if (sri != null)
                {
                    win.Icon = BitmapFrame.Create(sri.Stream);
                }
            }
            catch
            {
                // AHMED YOUNIS
            }

            win.Show();
        }

        public static void CrashLog(string src, string? msg)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "EkoTurboTool_crash.log");

                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {src}:{Environment.NewLine}{msg}{Environment.NewLine}{Environment.NewLine}");

                MessageBox.Show(
                    $"{src}:{Environment.NewLine}{Environment.NewLine}{msg}",
                    "EKO TURBO Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }
        }
    }
}