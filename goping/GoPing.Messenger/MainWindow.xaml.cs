using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
    private readonly RealtimeClient _realtime;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<ChatMessage> _pendingMessages = new();
    private readonly ObservableCollection<ChatSummary> _chatItems = new();
    private readonly Dictionary<string, (ChatMessage Message, UIElement Row)> _messageRows = new();
    private UserProfile _user;
    private IReadOnlyList<ChatSummary> _chats = Array.Empty<ChatSummary>();
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();
    private string? _selectedChatId;
    private ChatSummary? _selectedChat;
    private ChatMessage? _replyingTo;
    private ChatMessage? _popupMessage;
    private Point _popupScreenAnchor;
    private string _messageSignature = string.Empty;
    private string _readSignature = string.Empty;
    private bool _suppressChatSelection;
    private bool _refreshing;
    private bool _handlingRealtime;
    private bool _allowDeleteOthers = true;
    private bool _maximized;
    private Rect _restoreBounds;
    private bool _sendingMessage;
    private bool _creatingChat;
    private bool _busy;
    private bool _dark;
    private bool _viewingPeer;
    private int _pendingSequence;

    public MainWindow(ApiClient api, UserProfile user)
    {
        InitializeComponent();
        _api = api;
        _user = user;
        ChatList.ItemsSource = _chatItems;

        _realtime = new RealtimeClient(_api, OnRealtimeEventAsync);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        Loaded += async (_, _) =>
        {
            RenderUser();
            UpdateThemeIcon();
            await LoadClientConfigAsync();
            await RefreshChatsAsync(selectFirst: true);
            FadeIn(ChatPanel);
            _refreshTimer.Start();
            _realtime.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _realtime.Dispose();
        };
    }

    private async Task LoadClientConfigAsync()
    {
        try
        {
            ClientConfig config = await _api.GetClientConfigAsync();
            _allowDeleteOthers = config.AllowDeleteOthersMessages;
        }
        catch (ApiException)
        {
            _allowDeleteOthers = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? node = e.OriginalSource as DependencyObject;
             node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button) return;
        }

        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, new RoutedEventArgs());
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed && !_maximized) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        // WindowStyle=None + WindowState.Maximized прячет панель задач, поэтому
        // разворачиваем окно вручную в пределах рабочей области экрана.
        if (_maximized)
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            RootBorder.CornerRadius = new CornerRadius(10);
            MaximizeButton.Content = "\uE922";
            MaximizeButton.ToolTip = "Maximize";
            _maximized = false;
            return;
        }

        _restoreBounds = new Rect(Left, Top, Width, Height);
        Rect area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        RootBorder.CornerRadius = new CornerRadius(0);
        MaximizeButton.Content = "\uE923";
        MaximizeButton.ToolTip = "Restore";
        _maximized = true;
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
        ServerLabel.Text = _api.BaseUrl.Replace("https://", "").Replace("http://", "");
        ProfileButton.ToolTip = _user.Nickname + " (@" + _user.Username + ")";

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

    private void SyncChatList(IReadOnlyList<ChatSummary> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i < _chatItems.Count)
            {
                if (_chatItems[i] != items[i]) _chatItems[i] = items[i];
            }
            else
            {
                _chatItems.Add(items[i]);
            }
        }
        while (_chatItems.Count > items.Count)
        {
            _chatItems.RemoveAt(_chatItems.Count - 1);
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
            SyncChatList(response.Items);
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
                ResetConversationState();
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
        bool unchanged = _selectedChat is not null
            && _selectedChat.Id == chat.Id
            && _selectedChat.Title == chat.Title
            && _selectedChat.AvatarUrl == chat.AvatarUrl;
        if (!unchanged)
        {
            _selectedChat = chat;
            ThreadTitle.Text = chat.Title;
            ApplyAvatar(ThreadAvatar, ThreadAvatarInitial, chat.AvatarUrl, chat.Title);
        }
        if (!_sendingMessage && ChatStatusText.Text != "Encrypted")
        {
            SetChatStatus("Encrypted", false);
        }
        NoChatSelected.Visibility = Visibility.Collapsed;
        ThreadPanel.Visibility = Visibility.Visible;
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
        PeerBioText.Text = string.IsNullOrWhiteSpace(peer.Bio) ? "No bio yet." : peer.Bio;

        PeerNicknameText.Visibility = Visibility.Visible;
        PeerUsernameText.Visibility = Visibility.Visible;
        PeerBioText.Visibility = Visibility.Visible;

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
        PeerBioText.Visibility = Visibility.Collapsed;
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

    private void ResetConversationState()
    {
        _pendingMessages.Clear();
        _messages = Array.Empty<ChatMessage>();
        _messageSignature = string.Empty;
        _readSignature = string.Empty;
        CloseMessageActions();
        _messageRows.Clear();
        MessagesPanel.Children.Clear();
        PinnedList.ItemsSource = null;
        PinnedBar.Visibility = Visibility.Collapsed;
        ClearReply();
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
        ResetConversationState();
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

            PrunePendingDuplicates(response.Items);
            _messages = response.Items;
            RenderPinnedBar();

            string signature = BuildMessageSignature(response.Items);
            if (signature != _messageSignature)
            {
                _messageSignature = signature;
                RenderMessages();
            }

            string readSignature = chatId + ":" + signature;
            if (_readSignature != readSignature)
            {
                await _api.MarkReadAsync(chatId);
                _readSignature = readSignature;
            }
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
    }

    private static string BuildMessageSignature(IReadOnlyList<ChatMessage> messages)
        => string.Join("|", messages.Select(m =>
            $"{m.Id};{m.Status};{m.PinScope};{m.ReplyTo?.Id};{m.Text.Length};{m.Text}"));

    private void RenderPinnedBar()
    {
        List<ChatMessage> pinned = _messages
            .Where(m => !string.IsNullOrWhiteSpace(m.PinScope))
            .OrderByDescending(m => m.CreatedAt)
            .ToList();
        PinnedList.ItemsSource = pinned;
        PinnedBar.Visibility = pinned.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PinnedItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message) return;
        if (_messageRows.TryGetValue(message.Id, out var row)
            && row.Row is FrameworkElement element)
        {
            element.BringIntoView();
        }
    }

    private void PrunePendingDuplicates(IReadOnlyList<ChatMessage> remoteMessages)
    {
        if (_pendingMessages.Count == 0) return;

        foreach (ChatMessage message in remoteMessages)
        {
            if (message.Sender.Id != _user.Id) continue;
            _pendingMessages.RemoveAll(pending =>
                pending.Text == message.Text
                && Math.Abs((pending.CreatedAt - message.CreatedAt).TotalSeconds) < 30);
        }
    }

    private void RenderMessages()
    {
        List<ChatMessage> ordered = _messages
            .Concat(_pendingMessages)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        bool wasAtBottom = MessagesScroll.VerticalOffset + MessagesScroll.ViewportHeight
            >= MessagesScroll.ExtentHeight - 48;
        string? lastId = ordered.Count == 0 ? null : ordered[^1].Id;
        bool lastIsNew = lastId is not null && !_messageRows.ContainsKey(lastId);

        var desiredIds = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (ChatMessage message in ordered)
        {
            desiredIds.Add(message.Id);

            if (_messageRows.TryGetValue(message.Id, out var existing) && existing.Message == message)
            {
                int current = MessagesPanel.Children.IndexOf(existing.Row);
                if (current != index && current >= 0)
                {
                    MessagesPanel.Children.RemoveAt(current);
                    MessagesPanel.Children.Insert(index, existing.Row);
                }
                index++;
                continue;
            }

            if (existing.Row is not null)
            {
                MessagesPanel.Children.Remove(existing.Row);
                _messageRows.Remove(message.Id);
            }

            UIElement row = BuildMessageRow(message);
            MessagesPanel.Children.Insert(index, row);
            _messageRows[message.Id] = (message, row);
            index++;
        }

        foreach (string id in _messageRows.Keys.Except(desiredIds).ToList())
        {
            MessagesPanel.Children.Remove(_messageRows[id].Row);
            _messageRows.Remove(id);
        }

        if (_popupMessage is not null && !desiredIds.Contains(_popupMessage.Id))
        {
            CloseMessageActions();
        }

        // Не дёргаем прокрутку, если пользователь читает старые сообщения.
        if (wasAtBottom || lastIsNew)
        {
            MessagesScroll.ScrollToEnd();
        }
    }

    private UIElement BuildMessageRow(ChatMessage message)
    {
            bool own = message.Sender.Id == _user.Id;
            var row = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
                ToolTip = "Right-click for actions"
            };
            row.MouseRightButtonUp += (_, e) => ShowMessageActions(e, message);

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

            if (message.ReplyTo is not null)
            {
                var quote = new Border
                {
                    Padding = new Thickness(0, 0, 0, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                quote.SetResourceReference(Border.BorderBrushProperty,
                    own ? "PrimaryForegroundBrush" : "AccentBrush");

                var quoteStack = new StackPanel();
                var quoteSender = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(message.ReplyTo.SenderNickname)
                        ? "Reply"
                        : message.ReplyTo.SenderNickname,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                quoteSender.SetResourceReference(TextBlock.ForegroundProperty,
                    own ? "PrimaryForegroundBrush" : "AccentBrush");
                quoteStack.Children.Add(quoteSender);

                var quoteText = new TextBlock
                {
                    Text = Shorten(message.ReplyTo.Text, 120),
                    FontSize = 11,
                    Opacity = 0.75,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                quoteText.SetResourceReference(TextBlock.ForegroundProperty,
                    own ? "PrimaryForegroundBrush" : "TextBrush");
                quoteStack.Children.Add(quoteText);

                quote.Child = quoteStack;
                content.Children.Add(quote);
            }

            var text = new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };
            text.SetResourceReference(
                TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "TextBrush");
            content.Children.Add(text);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };

            if (!string.IsNullOrWhiteSpace(message.PinScope))
            {
                var pin = new TextBlock
                {
                    Text = "\uE718",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = message.PinScope == "chat"
                        ? "Pinned for both of you"
                        : "Pinned for you"
                };
                pin.SetResourceReference(TextBlock.ForegroundProperty,
                    own ? "PrimaryForegroundBrush" : "AccentBrush");
                footer.Children.Add(pin);
            }

            var time = new TextBlock
            {
                Text = message.CreatedAt.ToLocalTime().ToString("HH:mm"),
                FontSize = 10,
                Opacity = own ? 0.7 : 1.0
            };
            time.SetResourceReference(
                TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "FaintBrush");
            footer.Children.Add(time);

            string? status = own ? FormatDeliveryStatus(message.Status) : null;
            if (status is not null)
            {
                var statusText = new TextBlock
                {
                    Text = "  " + status,
                    FontSize = 10,
                    Opacity = 0.8
                };
                statusText.SetResourceReference(
                    TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "FaintBrush");
                footer.Children.Add(statusText);
            }
            content.Children.Add(footer);

            bubble.Child = content;
            row.Children.Add(bubble);
            return row;
    }

    private static string Shorten(string value, int maxLength)
    {
        string oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..(maxLength - 1)] + "…";
    }

    private static string? FormatDeliveryStatus(string? status)
        => status?.ToLowerInvariant() switch
        {
            "sending" => "Sending…",
            "sent" => "Sent",
            "read" => "Read",
            _ => null
        };

    private bool CanDeleteMessage(ChatMessage message)
        => message.Sender.Id == _user.Id || _allowDeleteOthers;

    private void ShowMessageActions(MouseButtonEventArgs e, ChatMessage message)
    {
        e.Handled = true;
        _popupMessage = message;
        _popupScreenAnchor = PointToScreen(e.GetPosition(this));

        bool canDelete = CanDeleteMessage(message);
        MessageDeleteButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        MessageDeleteHint.Text = message.Sender.Id == _user.Id
            ? "Choose who will lose this message"
            : "Allowed by server settings";

        bool canPin = !message.Id.StartsWith("local:", StringComparison.Ordinal);
        MessagePinButton.Visibility = canPin ? Visibility.Visible : Visibility.Collapsed;
        MessagePinHint.Text = message.PinScope switch
        {
            "chat" => "Pinned for both of you",
            "me" => "Pinned for you",
            _ => "For you or for both of you"
        };

        ShowMessageActionsView();
        MessageActionsPopup.IsOpen = true;
    }

    private void ShowMessageActionsView()
    {
        MessageActionsMainPanel.Visibility = Visibility.Visible;
        MessageActionsDeletePanel.Visibility = Visibility.Collapsed;
        MessageActionsPinPanel.Visibility = Visibility.Collapsed;
        MessageActionsHeading.Text = "MESSAGE ACTIONS";

        int actions = 0;
        if (MessageReplyButton.Visibility == Visibility.Visible) actions++;
        if (MessagePinButton.Visibility == Visibility.Visible) actions++;
        if (MessageDeleteButton.Visibility == Visibility.Visible) actions++;
        MessageActionsPopup.Height = 68 + (50 * actions);
        PositionMessageActions();
    }

    private void ShowDeleteConfirmView()
    {
        MessageActionsMainPanel.Visibility = Visibility.Collapsed;
        MessageActionsDeletePanel.Visibility = Visibility.Visible;
        MessageActionsPinPanel.Visibility = Visibility.Collapsed;
        MessageActionsHeading.Text = "DELETE MESSAGE";
        MessageActionsPopup.Height = 256;
        PositionMessageActions();
    }

    private void ShowPinChoiceView()
    {
        string? scope = _popupMessage?.PinScope;
        MessageActionsMainPanel.Visibility = Visibility.Collapsed;
        MessageActionsDeletePanel.Visibility = Visibility.Collapsed;
        MessageActionsPinPanel.Visibility = Visibility.Visible;
        MessageActionsHeading.Text = "PIN MESSAGE";

        MessagePinForMeButton.Visibility = scope is null ? Visibility.Visible : Visibility.Collapsed;
        MessagePinForChatButton.Visibility = scope == "chat" ? Visibility.Collapsed : Visibility.Visible;
        MessageUnpinButton.Visibility = scope is null ? Visibility.Collapsed : Visibility.Visible;
        MessageUnpinText.Text = scope == "chat" ? "Remove shared pin" : "Remove my pin";
        MessageUnpinHint.Text = scope == "chat"
            ? "Everyone in this chat will lose it"
            : "Only you will lose it";
        MessageActionsPopup.Height = scope == "chat" ? 192 : 236;
        PositionMessageActions();
    }

    private void PositionMessageActions()
    {
        const double gap = 6;
        const double margin = 4;
        double width = MessageActionsPopup.Width;
        double height = MessageActionsPopup.Height;
        Point screen = _popupScreenAnchor;
        Rect area = SystemParameters.WorkArea;

        double x = screen.X + gap;
        double y = screen.Y + gap;
        if (x + width > area.Right - margin) x = screen.X - width - gap;
        if (y + height > area.Bottom - margin) y = screen.Y - height - gap;

        // AbsolutePoint: смещения задаются в экранных координатах.
        MessageActionsPopup.HorizontalOffset =
            Math.Max(area.Left + margin, Math.Min(x, area.Right - width - margin));
        MessageActionsPopup.VerticalOffset =
            Math.Max(area.Top + margin, Math.Min(y, area.Bottom - height - margin));
    }

    private void CloseMessageActions_Click(object sender, RoutedEventArgs e)
        => CloseMessageActions();

    private void CloseMessageActions()
    {
        MessageActionsPopup.IsOpen = false;
        _popupMessage = null;
    }

    private void MessageActionReply_Click(object sender, RoutedEventArgs e)
    {
        ChatMessage? message = _popupMessage;
        CloseMessageActions();
        if (message is not null) SetReplyTarget(message);
    }

    private void MessageActionDelete_Click(object sender, RoutedEventArgs e)
        => ShowDeleteConfirmView();

    private void MessageActionDeleteCancel_Click(object sender, RoutedEventArgs e)
        => ShowMessageActionsView();

    private void MessageActionPin_Click(object sender, RoutedEventArgs e)
        => ShowPinChoiceView();

    private void MessageActionPinCancel_Click(object sender, RoutedEventArgs e)
        => ShowMessageActionsView();

    private async void MessageActionPinForMe_Click(object sender, RoutedEventArgs e)
        => await SetPinAsync("me", pinned: true);

    private async void MessageActionPinForChat_Click(object sender, RoutedEventArgs e)
        => await SetPinAsync("chat", pinned: true);

    private async void MessageActionUnpin_Click(object sender, RoutedEventArgs e)
    {
        string? scope = _popupMessage?.PinScope;
        if (scope is null) return;
        await SetPinAsync(scope, pinned: false);
    }

    private async Task SetPinAsync(string scope, bool pinned)
    {
        ChatMessage? message = _popupMessage;
        CloseMessageActions();
        if (message is null || message.Id.StartsWith("local:", StringComparison.Ordinal)) return;

        try
        {
            if (pinned)
            {
                await _api.PinMessageAsync(message.ChatId, message.Id, scope);
            }
            else
            {
                await _api.UnpinMessageAsync(message.ChatId, message.Id, scope);
            }
            _messageSignature = string.Empty;
            await LoadMessagesAsync();
            SetChatStatus(pinned ? "Message pinned" : "Pin removed", false);
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
    }

    private async void MessageActionDeleteForMe_Click(object sender, RoutedEventArgs e)
    {
        ChatMessage? message = _popupMessage;
        CloseMessageActions();
        if (message is not null) await DeleteMessageAsync(message, forEveryone: false);
    }

    private async void MessageActionDeleteForEveryone_Click(object sender, RoutedEventArgs e)
    {
        ChatMessage? message = _popupMessage;
        CloseMessageActions();
        if (message is not null) await DeleteMessageAsync(message, forEveryone: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !MessageActionsPopup.IsOpen) return;
        e.Handled = true;
        HandlePopupEscape();
    }

    private void MessageActionsPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        HandlePopupEscape();
    }

    private void HandlePopupEscape()
    {
        if (MessageActionsDeletePanel.Visibility == Visibility.Visible
            || MessageActionsPinPanel.Visibility == Visibility.Visible)
        {
            ShowMessageActionsView();
        }
        else
        {
            CloseMessageActions();
        }
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

    private async Task SendCurrentMessageAsync()
    {
        if (_sendingMessage || _selectedChatId is null) return;

        string text = MessageBox.Text.Trim();
        if (text.Length == 0) return;

        ChatMessage? previousReply = _replyingTo;
        string? replyToId = previousReply?.Id;
        ReplyPreview? replyPreview = previousReply is null
            ? null
            : new ReplyPreview(
                previousReply.Id, Shorten(previousReply.Text, 120), previousReply.Sender.Nickname);

        var pending = new ChatMessage(
            "local:" + (++_pendingSequence),
            _selectedChatId,
            new ChatMessageSender(_user.Id, _user.Username, _user.Nickname, _user.AvatarUrl),
            text,
            string.Empty,
            DateTimeOffset.UtcNow,
            replyPreview,
            "sending");

        _pendingMessages.Add(pending);
        RenderMessages();
        MessageBox.Text = string.Empty;
        ClearReply();

        _sendingMessage = true;
        SetChatStatus("Sending…", false);
        try
        {
            await _api.SendMessageAsync(_selectedChatId, text, replyToId);
            _pendingMessages.Remove(pending);
            PrunePendingDuplicates(_messages);
            _messageSignature = string.Empty;
            await LoadMessagesAsync();
            await RefreshChatsAsync(selectFirst: false);
            SetChatStatus("Encrypted", false);
        }
        catch (ApiException ex)
        {
            _pendingMessages.Remove(pending);
            if (MessageBox.Text.Trim().Length == 0) MessageBox.Text = text;
            if (previousReply is not null) SetReplyTarget(previousReply);
            RenderMessages();
            SetChatStatus(ex.Message, true);
        }
        finally
        {
            _sendingMessage = false;
        }
    }

    private void SetReplyTarget(ChatMessage message)
    {
        if (message.Id.StartsWith("local:", StringComparison.Ordinal)) return;

        _replyingTo = message;
        ReplySenderText.Text = string.IsNullOrWhiteSpace(message.Sender.Nickname)
            ? message.Sender.Username
            : message.Sender.Nickname;
        ReplyPreviewText.Text = Shorten(message.Text, 140);
        ReplyBanner.Visibility = Visibility.Visible;
        Keyboard.Focus(MessageBox);
    }

    private void CancelReplyButton_Click(object sender, RoutedEventArgs e) => ClearReply();

    private void ClearReply()
    {
        _replyingTo = null;
        ReplyBanner.Visibility = Visibility.Collapsed;
        ReplySenderText.Text = string.Empty;
        ReplyPreviewText.Text = string.Empty;
    }

    private async Task DeleteMessageAsync(ChatMessage message, bool forEveryone)
    {
        if (message.Id.StartsWith("local:", StringComparison.Ordinal))
        {
            _pendingMessages.Remove(message);
            RenderMessages();
            return;
        }

        if (!CanDeleteMessage(message))
        {
            SetChatStatus("Deleting others' messages is disabled on this server.", true);
            return;
        }

        try
        {
            await _api.DeleteMessageAsync(message.ChatId, message.Id, forEveryone);
            if (ReferenceEquals(_replyingTo, message)) ClearReply();
            _messageSignature = string.Empty;
            _readSignature = string.Empty;
            await LoadMessagesAsync();
            await RefreshChatsAsync(selectFirst: false);
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
    }

    private Task OnRealtimeEventAsync(string payload)
        => Dispatcher.InvokeAsync(() => HandleRealtimeEventAsync(payload)).Task.Unwrap();

    private async Task HandleRealtimeEventAsync(string payload)
    {
        if (_handlingRealtime) return;
        _handlingRealtime = true;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeNode)
                ? typeNode.GetString() ?? string.Empty
                : string.Empty;

            // Присутствие и набор текста намеренно игнорируются клиентом.
            if (type is "presence" or "typing" or "pong") return;

            string? chatId = root.TryGetProperty("chatId", out JsonElement chatNode)
                ? chatNode.GetString()
                : null;

            switch (type)
            {
                case "message.created":
                    if (root.TryGetProperty("message", out JsonElement messageNode))
                    {
                        ChatMessage? created = JsonSerializer.Deserialize<ChatMessage>(
                            messageNode.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (created is not null) PrunePendingDuplicates(new[] { created });
                    }
                    await RefreshChatsAsync(selectFirst: false);
                    if (chatId == _selectedChatId)
                    {
                        _messageSignature = string.Empty;
                        await LoadMessagesAsync();
                    }
                    break;

                case "message.deleted":
                case "message.pinned":
                case "messages.read":
                    await RefreshChatsAsync(selectFirst: false);
                    if (chatId == _selectedChatId)
                    {
                        _messageSignature = string.Empty;
                        await LoadMessagesAsync();
                    }
                    break;

                case "chat.created":
                case "chat.updated":
                    await RefreshChatsAsync(selectFirst: false);
                    break;
            }
        }
        catch (JsonException)
        {
            // Некорректное событие игнорируем.
        }
        catch (ApiException)
        {
            // Сеть уже обработана в обычном опросе.
        }
        finally
        {
            _handlingRealtime = false;
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
        CloseMessageActions();
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
        ProfileButton.IsEnabled = !busy;
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
