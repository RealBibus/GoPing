using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _typingTimer;
    private UserProfile _user;
    private IReadOnlyList<ChatSummary> _chats = Array.Empty<ChatSummary>();
    private string? _selectedChatId;
    private ChatSummary? _selectedChat;
    private string _messageSignature = string.Empty;
    private bool _suppressChatSelection;
    private bool _refreshing;
    private bool _sendingMessage;
    private bool _creatingChat;
    private bool _busy;
    private bool _dark;
    private bool _viewingPeer;
    private bool _typingPending;

    public MainWindow(ApiClient api, UserProfile user)
    {
        InitializeComponent();
        _api = api;
        _user = user;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _typingTimer.Tick += async (_, _) => await FlushTypingAsync();

        Loaded += async (_, _) =>
        {
            RenderUser();
            UpdateThemeIcon();
            await RefreshChatsAsync(selectFirst: true);
            FadeIn(ChatPanel);
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _typingTimer.Stop();
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
                Application.Current.Resources[key] = new SolidColorBrush(target);
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, duration) { EasingFunction = ease });
        }

        Color P(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        Anim("BgBrush",            P(dark ? "#0C0C0E" : "#F7F7F8"));
        Anim("SurfaceBrush",       P(dark ? "#141417" : "#FFFFFF"));
        Anim("SurfaceHoverBrush",  P(dark ? "#1C1C20" : "#F0F0F2"));
        Anim("BorderBrush",        P(dark ? "#26262B" : "#E6E6EA"));
        Anim("BorderStrongBrush",  P(dark ? "#3A3A40" : "#D2D2D8"));
        Anim("TextBrush",          P(dark ? "#F4F4F5" : "#1A1A1E"));
        Anim("MutedBrush",         P(dark ? "#9B9BA3" : "#6B6B74"));
        Anim("FaintBrush",         P(dark ? "#6B6B74" : "#9B9BA3"));
        Anim("AccentBrush",        P(dark ? "#5B9A74" : "#3F7A5C"));
        Anim("AccentSoftBrush",    P(dark ? "#151C18" : "#EEF2EF"));
        Anim("OnlineBrush",        P(dark ? "#5B9A74" : "#3F7A5C"));
        Anim("DangerBrush",        P(dark ? "#E87979" : "#C43C3C"));
        Anim("DangerSoftBrush",    P(dark ? "#2A1414" : "#F8EEEE"));
        Anim("PrimaryBrush",           P(dark ? "#EDEDEF" : "#1A1A1E"));
        Anim("PrimaryHoverBrush",      P(dark ? "#D8D8DC" : "#2C2C32"));
        Anim("PrimaryPressBrush",      P(dark ? "#C8C8CE" : "#0E0E10"));
        Anim("PrimaryForegroundBrush", P(dark ? "#141417" : "#FFFFFF"));
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

    // ---------- ЧАТЫ И СООБЩЕНИЯ ----------

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            try { await _api.HeartbeatAsync(); }
            catch (ApiException) { /* не мешаем опросу чатов */ }

            await RefreshChatsAsync(selectFirst: false);
            await LoadMessagesAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshChatsAsync(bool selectFirst, string? selectChatId = null)
    {
        try
        {
            ChatListResponse response = await _api.GetChatsAsync();
            string? previous = _selectedChatId;

            _chats = response.Items;
            _suppressChatSelection = true;
            ChatList.ItemsSource = null;
            ChatList.ItemsSource = _chats;
            ChatSummary? selected = _chats.FirstOrDefault(
                c => c.Id == (selectChatId ?? previous));
            if (selected is null && (selectFirst || previous is not null))
            {
                selected = _chats.FirstOrDefault();
            }
            ChatList.SelectedItem = selected;
            _suppressChatSelection = false;

            if (selected is null)
            {
                _selectedChatId = null;
                ShowEmptyState();
                return;
            }

            _selectedChatId = selected.Id;
            ShowThread(selected);
            if (previous != selected.Id)
            {
                _messageSignature = string.Empty;
                await LoadMessagesAsync();
            }
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
    }

    private void ShowThread(ChatSummary chat)
    {
        _selectedChat = chat;
        ThreadTitle.Text = chat.Title;
        ApplyAvatar(ThreadAvatar, ThreadAvatarInitial, chat.AvatarUrl, chat.Title);
        ThreadOnlineDot.Visibility = chat.PeerOnline ? Visibility.Visible : Visibility.Collapsed;
        UpdatePresenceStatus(chat);
        NoChatSelected.Visibility = Visibility.Collapsed;
        ThreadPanel.Visibility = Visibility.Visible;
    }

    private void UpdatePresenceStatus(ChatSummary chat)
    {
        if (chat.PeerTyping)
        {
            SetChatStatus("typing…", false);
            return;
        }

        if (chat.PeerOnline)
        {
            SetChatStatus("online", false);
            return;
        }

        SetChatStatus(FormatLastSeen(chat.PeerLastSeenAt), false);
    }

    private static string FormatLastSeen(DateTimeOffset? lastSeen)
    {
        if (lastSeen is null) return "offline";

        DateTimeOffset local = lastSeen.Value.ToLocalTime();
        TimeSpan ago = DateTimeOffset.Now - local;
        if (ago < TimeSpan.FromMinutes(1)) return "last seen just now";
        if (ago < TimeSpan.FromHours(1))
        {
            int minutes = Math.Max(1, (int)ago.TotalMinutes);
            return $"last seen {minutes} min ago";
        }
        if (local.Date == DateTime.Today)
            return "last seen today at " + local.ToString("HH:mm");
        if (local.Date == DateTime.Today.AddDays(-1))
            return "last seen yesterday at " + local.ToString("HH:mm");
        return "last seen " + local.ToString("dd MMM, HH:mm");
    }

    private static string FormatPeerPresence(UserProfile peer)
    {
        if (peer.Online) return "online";
        return FormatLastSeen(peer.LastSeenAt);
    }

    private async void ThreadTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) await OpenPeerProfileAsync();
    }

    private async void ThreadAvatar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) await OpenPeerProfileAsync();
    }

    private async Task OpenPeerProfileAsync()
    {
        if (_selectedChat is null) return;
        string? username = _selectedChat.Username;
        if (string.IsNullOrWhiteSpace(username)) return;
        try
        {
            UserProfile peer = await _api.GetUserByUsernameAsync(username);
            ShowPeerProfile(peer);
        }
        catch (ApiException ex)
        {
            SetProfileStatus(ex.Message, true);
        }
    }

    private void ShowPeerProfile(UserProfile peer)
    {
        _viewingPeer = true;
        ProfileHeadingText.Text = "Profile";
        ProfileSubtitleText.Text = "User profile";
        ApplyAvatar(ProfileAvatar, ProfileAvatarInitial, peer.AvatarUrl, peer.Nickname);
        PeerNicknameText.Text = peer.Nickname;
        PeerUsernameText.Text = "@" + peer.Username;
        PeerPresenceText.Text = FormatPeerPresence(peer);
        PeerBioText.Text = string.IsNullOrWhiteSpace(peer.Bio) ? "No bio yet." : peer.Bio;

        PeerNicknameText.Visibility = Visibility.Visible;
        PeerUsernameText.Visibility = Visibility.Visible;
        PeerPresenceText.Visibility = Visibility.Visible;
        PeerBioText.Visibility = Visibility.Visible;
        ProfileOnlineDot.Visibility = peer.Online ? Visibility.Visible : Visibility.Collapsed;

        NicknameLabel.Visibility = Visibility.Collapsed;
        UsernameLabel.Visibility = Visibility.Collapsed;
        UsernameHintText.Visibility = Visibility.Collapsed;
        BioLabel.Visibility = Visibility.Collapsed;
        NicknameBox.Visibility = Visibility.Collapsed;
        UsernameBox.Visibility = Visibility.Collapsed;
        BioBox.Visibility = Visibility.Collapsed;
        UploadAvatarButton.Visibility = Visibility.Collapsed;
        RemoveAvatarButton.Visibility = Visibility.Collapsed;
        OwnProfileActions.Visibility = Visibility.Collapsed;
        SignOutButton.Visibility = Visibility.Collapsed;
        PeerCloseButton.Visibility = Visibility.Visible;

        ProfileStatusText.Text = string.Empty;
        ChatPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
        FadeIn(ProfilePanel);
    }

    private void ShowOwnProfile()
    {
        _viewingPeer = false;
        ProfileHeadingText.Text = "Edit profile";
        ProfileSubtitleText.Text = "Changes are saved to your GoPing server.";
        RenderUser();

        PeerNicknameText.Visibility = Visibility.Collapsed;
        PeerUsernameText.Visibility = Visibility.Collapsed;
        PeerPresenceText.Visibility = Visibility.Collapsed;
        PeerBioText.Visibility = Visibility.Collapsed;
        ProfileOnlineDot.Visibility = Visibility.Collapsed;
        PeerCloseButton.Visibility = Visibility.Collapsed;

        NicknameLabel.Visibility = Visibility.Visible;
        UsernameLabel.Visibility = Visibility.Visible;
        UsernameHintText.Visibility = Visibility.Visible;
        BioLabel.Visibility = Visibility.Visible;
        NicknameBox.Visibility = Visibility.Visible;
        UsernameBox.Visibility = Visibility.Visible;
        BioBox.Visibility = Visibility.Visible;
        UploadAvatarButton.Visibility = Visibility.Visible;
        RemoveAvatarButton.Visibility = Visibility.Visible;
        OwnProfileActions.Visibility = Visibility.Visible;
        SignOutButton.Visibility = Visibility.Visible;

        ProfileStatusText.Text = string.Empty;
        ChatPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
        FadeIn(ProfilePanel);
        Keyboard.Focus(NicknameBox);
    }

    private void ShowEmptyState()
    {
        ThreadPanel.Visibility = Visibility.Collapsed;
        NoChatSelected.Visibility = Visibility.Visible;
        bool empty = _chats.Count == 0;
        EmptyStateTitle.Text = empty ? "No chats yet" : "Select a chat";
        EmptyStateText.Text = empty
            ? "Start a conversation with a friend by username."
            : "Pick a chat on the left to read the messages.";
        EmptyNewChatButton.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChatSelection || ChatList.SelectedItem is not ChatSummary chat)
        {
            return;
        }
        _selectedChatId = chat.Id;
        _messageSignature = string.Empty;
        ShowThread(chat);
        await LoadMessagesAsync();
    }

    private async Task LoadMessagesAsync()
    {
        string? chatId = _selectedChatId;
        if (chatId is null) return;

        try
        {
            MessageListResponse response = await _api.GetMessagesAsync(chatId);
            if (_selectedChatId != chatId) return;

            string signature = string.Join(",", response.Items.Select(m => m.Id));
            if (signature != _messageSignature)
            {
                _messageSignature = signature;
                RenderMessages(response.Items);
            }

            if (_selectedChat is not null)
            {
                UpdatePresenceStatus(_selectedChat);
            }
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
    }

    private void RenderMessages(IReadOnlyList<ChatMessage> messages)
    {
        MessagesPanel.Children.Clear();
        foreach (ChatMessage message in messages)
        {
            bool own = message.Sender.Id == _user.Id;
            var row = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };

            if (!own)
            {
                var avatarRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var avatarGrid = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
                var avatarBg = new Ellipse { Width = 26, Height = 26 };
                avatarBg.SetResourceReference(Shape.FillProperty, "SurfaceHoverBrush");
                avatarGrid.Children.Add(avatarBg);

                string nick = message.Sender.Nickname?.Trim() ?? "?";
                var initial = new TextBlock
                {
                    Text = nick.Length == 0 ? "?" : StringInfo.GetNextTextElement(nick).ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                initial.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                avatarGrid.Children.Add(initial);

                if (!string.IsNullOrWhiteSpace(message.Sender.AvatarUrl))
                {
                    try
                    {
                        var avatar = new Image
                        {
                            Width = 26,
                            Height = 26,
                            Stretch = Stretch.UniformToFill,
                            Source = new BitmapImage(new Uri(message.Sender.AvatarUrl)),
                            Clip = new EllipseGeometry(new Point(13, 13), 13, 13)
                        };
                        avatarGrid.Children.Add(avatar);
                    }
                    catch
                    {
                        // оставляем инициал
                    }
                }

                avatarRow.Children.Add(avatarGrid);

                var sender = new TextBlock
                {
                    Text = message.Sender.Nickname,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sender.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                avatarRow.Children.Add(sender);
                row.Children.Add(avatarRow);
            }

            var bubble = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 8, 11, 8),
                MaxWidth = 430,
                BorderThickness = new Thickness(1)
            };
            bubble.SetResourceReference(Border.BackgroundProperty, own ? "PrimaryBrush" : "SurfaceBrush");
            bubble.SetResourceReference(Border.BorderBrushProperty, own ? "PrimaryBrush" : "BorderBrush");

            var content = new StackPanel();
            var text = new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };
            text.SetResourceReference(
                TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "TextBrush");
            content.Children.Add(text);

            var time = new TextBlock
            {
                Text = message.CreatedAt.ToLocalTime().ToString("HH:mm"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Opacity = own ? 0.7 : 1.0
            };
            time.SetResourceReference(
                TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "FaintBrush");
            content.Children.Add(time);

            bubble.Child = content;
            row.Children.Add(bubble);
            MessagesPanel.Children.Add(row);
        }

        MessagesScroll.ScrollToEnd();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await SendCurrentMessageAsync();

    private async void MessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedChatId is null || string.IsNullOrWhiteSpace(MessageBox.Text))
        {
            return;
        }
        _typingPending = true;
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    private async Task FlushTypingAsync()
    {
        _typingTimer.Stop();
        if (!_typingPending || _selectedChatId is null) return;
        _typingPending = false;
        try
        {
            await _api.NotifyTypingAsync(_selectedChatId);
        }
        catch (ApiException)
        {
            // тихо: индикатор не критичен
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        if (_sendingMessage || _selectedChatId is null) return;

        string text = MessageBox.Text.Trim();
        if (text.Length == 0) return;

        _sendingMessage = true;
        SendButton.IsEnabled = false;
        MessageBox.IsEnabled = false;
        SetChatStatus("Sending…", false);
        try
        {
            await _api.SendMessageAsync(_selectedChatId, text);
            MessageBox.Text = string.Empty;
            _messageSignature = string.Empty;
            await LoadMessagesAsync();
            await RefreshChatsAsync(selectFirst: false);
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
        finally
        {
            _sendingMessage = false;
            SendButton.IsEnabled = true;
            MessageBox.IsEnabled = true;
            Keyboard.Focus(MessageBox);
        }
    }

    private void SetChatStatus(string message, bool isError)
    {
        ChatStatusText.Text = message;
        ChatStatusText.SetResourceReference(
            TextBlock.ForegroundProperty, isError ? "DangerBrush" : "MutedBrush");
    }

    // ---------- НОВЫЙ ЧАТ ----------

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        NewChatUsernameBox.Text = string.Empty;
        SetNewChatStatus(string.Empty, false);
        NewChatPanel.Visibility = Visibility.Visible;
        Keyboard.Focus(NewChatUsernameBox);
    }

    private void NewChatCancelButton_Click(object sender, RoutedEventArgs e)
        => NewChatPanel.Visibility = Visibility.Collapsed;

    private async void NewChatCreateButton_Click(object sender, RoutedEventArgs e)
        => await CreateChatAsync();

    private async void NewChatUsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CreateChatAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NewChatPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task CreateChatAsync()
    {
        if (_creatingChat) return;

        string username = NewChatUsernameBox.Text.Trim();
        if (username.Length == 0)
        {
            SetNewChatStatus("Enter a username.", true);
            Keyboard.Focus(NewChatUsernameBox);
            return;
        }

        _creatingChat = true;
        NewChatCreateButton.IsEnabled = false;
        NewChatCancelButton.IsEnabled = false;
        SetNewChatStatus("Creating…", false);
        try
        {
            ChatSummary chat = await _api.CreateChatAsync(username);
            NewChatPanel.Visibility = Visibility.Collapsed;
            await RefreshChatsAsync(selectFirst: false, selectChatId: chat.Id);
        }
        catch (ApiException ex)
        {
            SetNewChatStatus(ex.Message, true);
        }
        finally
        {
            _creatingChat = false;
            NewChatCreateButton.IsEnabled = true;
            NewChatCancelButton.IsEnabled = true;
        }
    }

    private void SetNewChatStatus(string message, bool isError)
    {
        NewChatStatusText.Text = message;
        NewChatStatusText.SetResourceReference(
            TextBlock.ForegroundProperty, isError ? "DangerBrush" : "MutedBrush");
    }

    private void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowOwnProfile();
    }

    private void PeerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _viewingPeer = false;
        ProfilePanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        FadeIn(ChatPanel);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingPeer)
        {
            _viewingPeer = false;
            ProfilePanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
            FadeIn(ChatPanel);
            return;
        }
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
            _viewingPeer = false;
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
