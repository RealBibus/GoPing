using System.Windows;

namespace Aerisflow.Messenger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var loginWindow = new MessengerLoginWindow();
        loginWindow.Show();
    }
}