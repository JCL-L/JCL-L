using System;
using System.IO;
using System.Windows;
using NinOne.App.Runtime;
using NinOne.Infrastructure.Configuration;
using NinOne.Infrastructure.Logging;

namespace NinOne.App
{
    public partial class App : System.Windows.Application
    {
        private DeviceRuntime _runtime;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var systemPath = Path.Combine(baseDirectory, "config", "System.ini");
                var productPath = Path.Combine(baseDirectory, "config", "Product", "5615", "cfg.ini");
                var config = new SystemConfigLoader().Load(systemPath, productPath);
                var logger = new FileAppLogger(Path.Combine(baseDirectory, "logs"));
                _runtime = new DeviceRuntime(config, logger);
                MainWindow = new MainWindow(_runtime);
                MainWindow.Show();
            }
            catch (Exception exception)
            {
                MessageBox.Show("程序启动失败：\n" + exception, "NinOne 手动模式", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_runtime != null) _runtime.Dispose();
            base.OnExit(e);
        }
    }
}
