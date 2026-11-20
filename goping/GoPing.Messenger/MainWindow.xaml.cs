using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private UserProfile _user;
    private bool _busy;
    private bool _dark;

    public MainWindow(ApiClient api, UserProfile user)
    {
        InitializeComponent();
        _api = api;
        _user = user;
        Loaded += async (_, _) =>
        {
            RenderUser();
            UpdateThemeIcon();
            await LoadChatsAsync();
            FadeIn(ChatPanel);
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "\uE922";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "\uE923";
        }
    }

    // ---------- ТЕМА ----------

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        UpdateThemeIcon();
        ApplyTheme(_dark);
    }

    private void UpdateThemeIcon()
    {
        ThemeToggleButton.Content = _dark ? "\uE706" : "\uE708";
        ThemeToggleButton.ToolTip = _dark ? "Switch to light theme" : "Switch to dark theme";
    }

    private void ApplyTheme(bool dark)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(320));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        void Anim(string key, Color target)
        {
            if (Application.Current.Resources[key] is not SolidColorBrush brush)
                return;

            if (brush.IsFrozen)
            {
                // На случай, если UnfreezeBrushes в App не сработал —
                // мгновенно меняем на клон (анимация невозможна).
                Application.Current.Resources[key] = new SolidColorBrush(target);
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, duration) { EasingFunction = ease });
        }

        Color P(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        Anim("BgBrush",            P(dark ? "#09090B" : "#FFFFFF"));
        Anim("SurfaceBrush",       P(dark ? "#131316" : "#FAFAFA"));
        Anim("SurfaceHoverBrush",  P(dark ? "#1F1F23" : "#F4F4F5"));
        Anim("BorderBrush",        P(dark ? "#27272A" : "#E4E4E7"));
        Anim("BorderStrongBrush",  P(dark ? "#3F3F46" : "#D4D4D8"));
        Anim("TextBrush",          P(dark ? "#FAFAFA" : "#18181B"));
        Anim("MutedBrush",         P(dark ? "#A1A1AA" : "#71717A"));
        Anim("FaintBrush",         P(dark ? "#71717A" : "#A1A1AA"));
        Anim("AccentBrush",        P(dark ? "#22C55E" : "#16A34A"));
        Anim("AccentSoftBrush",    P(dark ? "#052E16" : "#DCFCE7"));
        Anim("DangerBrush",        P(dark ? "#F87171" : "#DC2626"));
        Anim("DangerSoftBrush",    P(dark ? "#450A0A" : "#FEF2F2"));
        Anim("PrimaryBrush",           P(dark ? "#FAFAFA" : "#18181B"));
        Anim("PrimaryHoverBrush",      P(dark ? "#E4E4E7" : "#27272A"));
        Anim("PrimaryPressBrush",      P(dark ? "#D4D4D8" : "#09090B"));
        Anim("PrimaryForegroundBrush", P(dark ? "#18181B" : "#FFFFFF"));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- ПРОФИЛЬ ----------

    private void RenderUser()
    {
        NicknameText.Text = _user.Nickname;
        UsernameText.Text = "@" + _user.Username;
        ServerLabel.Text = _api.BaseUrl.Replace("https://", "").Replace("http://", "");

        NicknameBox.Text = _user.Nickname;
        UsernameBox.Text = _user.Username;
        BioBox.Text = _user.Bio;

        ApplyAvatar(AvatarEllipse, AvatarInitial, _user.AvatarUrl, _user.Nickname);
        ApplyAvatar(ProfileAvatar, ProfileAvatarInitial, _user.AvatarUrl, _user.Nickname);
    }

    private void ApplyAvatar(Ellipse ellipse, TextBlock initial, string? url, string name)
    {
        string trimmed = name.Trim();
        initial.Text = trimmed.Length == 0
            ? "?"
            : StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
        initial.Visibility = Visibility.Visible;

        ellipse.SetResourceReference(Shape.FillProperty, "SurfaceHoverBrush");

        if (string.IsNullOrWhiteSpace(url)) return;

        _ = LoadAvatarAsync(ellipse, initial, url);
    }

    private async Task LoadAvatarAsync(Ellipse ellipse, TextBlock initial, string url)
    {
        try
        {
            byte[] bytes = await _api.DownloadAvatarAsync(url);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            ellipse.Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            initial.Visibility = Visibility.Collapsed;
            ProfileStatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            ProfileStatusText.Text = "Avatar load failed: " + ex.Message;
        }
    }

    private async Task LoadChatsAsync()
    {
        try { await _api.GetChatsAsync(); }
        catch (ApiException) { }
    }

    private void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        RenderUser();
        ProfileStatusText.Text = string.Empty;
        ChatPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
        FadeIn(ProfilePanel);
        Keyboard.Focus(NicknameBox);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RenderUser();
        ProfileStatusText.Text = string.Empty;
        ProfilePanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        FadeIn(ChatPanel);
    }

    private void FadeIn(Grid panel)
    {
        if (panel.Visibility != Visibility.Visible) return;
        var sb = new Storyboard();
        var anim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220)));
        Storyboard.SetTarget(anim, panel);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(anim);
        sb.Begin(panel);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string nickname = NicknameBox.Text.Trim();
        string username = UsernameBox.Text.Trim();
        string bio = BioBox.Text.Trim();

        if (nickname.Length == 0)
        {
            SetProfileStatus("Enter a nickname.", true);
            Keyboard.Focus(NicknameBox);
            return;
        }
        if (username.Length < 3)
        {
            SetProfileStatus("Username must be at least 3 characters.", true);
            Keyboard.Focus(UsernameBox);
            return;
        }

        SetBusy(true);
        SetProfileStatus("Saving…", false);
        try
        {
            _user = await _api.UpdateProfileAsync(nickname, username, bio);
            RenderUser();
            SetProfileStatus("Saved.", false);
            ChatPanel.Visibility = Visibility.Visible;
            FadeIn(ChatPanel);
            ProfilePanel.Visibility = Visibility.Collapsed;
        }
        catch (ApiException ex) { SetProfileStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async void UploadAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose avatar",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true);
        SetProfileStatus("Uploading avatar…", false);
        try
        {
            _user = await _api.UploadAvatarAsync(dialog.FileName);
            RenderUser();
            SetProfileStatus("Avatar updated.", false);
        }
        catch (ApiException ex) { SetProfileStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async void RemoveAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        SetProfileStatus("Removing avatar…", false);
        try
        {
            _user = await _api.DeleteAvatarAsync();
            RenderUser();
            SetProfileStatus("Avatar removed.", false);
        }
        catch (ApiException ex) { SetProfileStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        await _api.LogoutAsync();
        SessionStore.Clear();
        ((App)Application.Current).OpenLogin(_api);
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        UploadAvatarButton.IsEnabled = !busy;
        RemoveAvatarButton.IsEnabled = !busy;
        EditProfileButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy;
    }

    private void SetProfileStatus(string message, bool isError)
    {
        ProfileStatusText.Text = message;
        ProfileStatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "DangerBrush" : "MutedBrush");
    }
}