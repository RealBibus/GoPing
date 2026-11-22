using System.IO;
using System.Windows;
using System.Windows.Media;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    e.Exception.ToString());
            }
            catch { }

            MessageBox.Show(e.Exception.ToString(), "Unhandled exception");
            e.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // КРИТИЧНО: разморозить brush-ресурсы ДО загрузки окон.
        // Иначе BeginAnimation бросит InvalidOperationException и тема
        // переключится без анимации (или приложение упадёт).
        UnfreezeBrushes();

        var api = new ApiClient();
        StoredSession? stored = SessionStore.Load();

        if (stored is not null)
        {
            try
            {
                AuthResult auth;
                if (!string.IsNullOrWhiteSpace(stored.ServerToken))
                {
                    auth = await api.LoginWithTokenAsync(stored.ServerToken);
                    SessionStore.Save(new StoredSession(stored.ServerToken, auth.RefreshToken));
                }
                else if (!string.IsNullOrWhiteSpace(stored.RefreshToken))
                {
                    api.UseRefreshToken(stored.RefreshToken);
                    auth = await api.RefreshAsync();
                    SessionStore.Save(new StoredSession(null, auth.RefreshToken));
                }
                else throw new ApiException("EMPTY_SESSION", "Сохранённая сессия пуста.");

                OpenMain(api, auth.User);
                return;
            }
            catch (ApiException ex) when (ex.Code is "UNAUTHORIZED"
                                              or "INVALID_REFRESH_TOKEN"
                                              or "INVALID_TOKEN"
                                              or "EMPTY_SESSION")
            {
                // Сессия действительно недействительна — удаляем её.
                SessionStore.Clear();
            }
            catch
            {
                // Сервер недоступен или временный сбой. Сессию сохраняем,
                // иначе после её возврата пользователя снова просят войти.
            }
        }

        OpenLogin(api);
    }

    private void UnfreezeBrushes()
    {
        string[] keys =
        {
            "BgBrush", "SurfaceBrush", "SurfaceHoverBrush",
            "HoverBrush", "SelectedBrush", "SearchFieldBrush", "SplitterBrush", "SplitterHoverBrush",
            "BorderBrush", "BorderStrongBrush",
            "TextBrush", "MutedBrush", "FaintBrush",
            "AccentBrush", "AccentSoftBrush",
            "DangerBrush", "DangerSoftBrush",
            "PrimaryBrush", "PrimaryHoverBrush", "PrimaryPressBrush",
            "PrimaryForegroundBrush", "OnlineBrush", "FavoritesBrush",
            "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbPressedBrush"
        };

        foreach (var key in keys)
        {
            if (Resources[key] is SolidColorBrush brush && brush.IsFrozen)
            {
                // Новый SolidColorBrush никогда не frozen → можно анимировать.
                Resources[key] = new SolidColorBrush(brush.Color);
            }
        }
    }

    public void OpenLogin(ApiClient api)
    {
        var login = new LoginWindow(api);
        MainWindow = login;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        login.Show();
    }

    public void OpenMain(ApiClient api, UserProfile user)
    {
        var main = new MainWindow(api, user);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}