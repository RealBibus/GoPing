using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class LoginWindow : Window
{
    private static readonly Regex TokenPattern = new(
        "^GOPING-[A-Za-z0-9_-]{12}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ApiClient _api;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    public LoginWindow(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        Loaded += (_, _) => Keyboard.Focus(TokenBox);
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy)
        {
            e.Handled = true;
            Close();
        }
    }

    private void TokenBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        TokenPlaceholder.Visibility = TokenBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (StatusText.Text.Length > 0)
        {
            SetStatus(string.Empty, false);
        }
    }

    private async void TokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SignInWithTokenAsync();
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
        => await SignInWithTokenAsync();

    private async Task SignInWithTokenAsync()
    {
        if (_busy)
        {
            return;
        }

        string token = TokenBox.Text.Trim();
        if (!TokenPattern.IsMatch(token))
        {
            SetStatus("The token must look like GOPING-XXXXXXXXXXXX.", true);
            Keyboard.Focus(TokenBox);
            return;
        }

        SetBusy(true, "Signing in…");
        try
        {
            AuthResult auth = await _api.LoginWithTokenAsync(token, _cts.Token);
            SessionStore.Save(new StoredSession(token, auth.RefreshToken));
            OpenMain(auth);
        }
        catch (OperationCanceledException)
        {
            // Окно закрыли.
        }
        catch (ApiException ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void GithubButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Opening GitHub…");
        try
        {
            GithubStart start = await _api.StartGithubAsync(_cts.Token);
            Process.Start(new ProcessStartInfo(start.AuthorizeUrl) { UseShellExecute = true });
            SetStatus("Confirm the sign-in in your browser. Waiting…", false);

            while (DateTimeOffset.UtcNow < start.ExpiresAt)
            {
                await Task.Delay(1500, _cts.Token);
                GithubPoll poll = await _api.PollGithubAsync(start.State, _cts.Token);
                if (poll.Status == "ready" && poll.Auth is not null)
                {
                    SessionStore.Save(new StoredSession(null, poll.Auth.RefreshToken));
                    OpenMain(poll.Auth);
                    return;
                }
            }

            SetStatus("GitHub sign-in timed out. Try again.", true);
        }
        catch (OperationCanceledException)
        {
            // Окно закрыли.
        }
        catch (ApiException ex)
        {
            SetStatus(ex.Message, true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the browser: " + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenMain(AuthResult auth)
    {
        _busy = false;
        ((App)Application.Current).OpenMain(_api, auth.User);
        Close();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        GithubButton.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        TokenBox.IsEnabled = !busy;
        if (status is not null)
        {
            SetStatus(status, false);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "MutedBrush");
    }
}
