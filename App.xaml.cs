using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace ToConvert;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var splash = new SplashWindow();
        MainWindow = splash;
        splash.Show();

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(350);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        splash.Close();
    }
}

