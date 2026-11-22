using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GoPing.Helpers;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private readonly RealtimeClient _realtime;
    private readonly CallService _calls;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _callHideTimer;
    private readonly DispatcherTimer _callDurationTimer;
    private DateTime? _callConnectedAt;
    private bool _remoteScreenSharing;
    private WriteableBitmap? _remoteVideoBitmap;
    private readonly List<ChatMessage> _pendingMessages = new();
    private readonly ObservableCollection<ChatSummary> _chatItems = new();
    private readonly Dictionary<string, (ChatMessage Message, UIElement Row)> _messageRows = new();
    private UserProfile _user;
    private IReadOnlyList<ChatSummary> _chats = Array.Empty<ChatSummary>();
    private ChatSummary? _favoritesChat;
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
    private readonly DispatcherTimer _userSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _userSearchVersion;
    private UserProfile? _selectedSearchUser;
    private string _chatSearchQuery = string.Empty;
    private bool _globalSearch;
    private GridLength _chatListWidthBeforePeer;
    private bool _busy;
    private bool _dark;
    private bool _viewingPeer;
    private bool _connectionFailureDialogShown;
    private bool _realtimeWasDisconnected;
    private bool _callEngineReady;
    private AppSettings _appSettings = AppSettingsStore.Load();
    private bool _suppressSettingsSave;
    private bool _pausedByMinimize;
    private TaskCompletionSource<bool>? _permissionTcs;
    private TaskCompletionSource<ScreenShareSource?>? _screenPickerTcs;
    private int _pendingSequence;
    private int _avatarLoadVersion;
    private const int EmojiPageSize = 56;
    private int _emojiPage;
    private bool _unicodeEmojiTab;
    private int _unicodeCategory;

    public MainWindow(ApiClient api, UserProfile user)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowResizeHelper.Attach(this, () => !_maximized);
        _api = api;
        _user = user;
        ChatList.ItemsSource = _chatItems;
        UnicodeCategoryList.ItemsSource = UnicodeEmojiCatalog.Categories;

        _realtime = new RealtimeClient(_api, OnRealtimeEventAsync, OnRealtimeStatus);
        _calls = new CallService(
            _api,
            json => _realtime.SendAsync(json),
            callId => _realtime.DropPendingSignals(callId));
        _calls.Incoming += OnCallIncoming;
        _calls.StateChanged += OnCallStateChanged;
        _calls.SpeakingChanged += OnCallSpeakingChanged;
        _calls.RemoteVideoFrameReady += OnRemoteVideoFrame;
        _calls.RemoteVideoCleared += OnRemoteVideoCleared;
        _calls.StatsUpdated += OnCallStatsUpdated;
        _calls.ScreenShareStateChanged += OnCallScreenShareStateChanged;
        _calls.PeerMediaStateChanged += OnCallPeerMediaStateChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _callHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _callHideTimer.Tick += (_, _) =>
        {
            _callHideTimer.Stop();
            SetSpeakingRings(false, false);
            
            CallOverlay.Visibility = Visibility.Collapsed;
        };
        _callDurationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _callDurationTimer.Tick += (_, _) => UpdateCallDurationText();
        _userSearchTimer.Tick += async (_, _) =>
        {
            _userSearchTimer.Stop();
            await RunUserSearchAsync();
        };
        StateChanged += (_, _) => OnWindowStateChanged();

        Loaded += async (_, _) =>
        {
            RenderUser();
            UpdateThemeIcon();
            ApplySettingsToUi();
            _ = InitializeCallEngineAsync();
            try
            {
                await LoadClientConfigAsync();
            }
            catch
            {
                _allowDeleteOthers = true;
            }
            try
            {
                await RefreshChatsAsync(selectFirst: true);
            }
            catch
            {
                // Таймер ниже повторит загрузку — перезапуск приложения не нужен.
                SetChatStatus("Could not load chats. Retrying…", true);
            }
            FadeIn(ChatPanel);
            _refreshTimer.Start();
            _realtime.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _callHideTimer.Stop();
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

    private async Task InitializeCallEngineAsync()
    {
        try
        {
            await _calls.InitializeAsync(CallWebView);
            _callEngineReady = true;
            UpdateCallButton();
        }
        catch
        {
            // WebView2 unavailable — calls stay disabled, no system dialogs.
            _callEngineReady = false;
            CallButton.IsEnabled = false;
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

    private void OnWindowStateChanged()
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_calls.Current is null)
            {
                _pausedByMinimize = true;
                _realtime.Pause();
            }
            return;
        }

        if (_pausedByMinimize)
        {
            _pausedByMinimize = false;
            _realtime.Resume();
        }
    }

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
            ThemeOverlay.CornerRadius = new CornerRadius(10);
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
        ThemeOverlay.CornerRadius = new CornerRadius(0);
        MaximizeButton.Content = "\uE923";
        MaximizeButton.ToolTip = "Restore";
        _maximized = true;
    }

    // ---------- ТЕМА ----------

    private bool _themeTransition;

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_themeTransition) return;
        _themeTransition = true;

        bool previousDark = _dark;
        bool nextDark = !previousDark;

        try
        {
            _dark = nextDark;
            UpdateThemeIcon();
            ApplyTheme(_dark);
            UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(this);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.PixelsPerInchX / 96.0));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.PixelsPerInchY / 96.0));

            var snapshot = new RenderTargetBitmap(
                pixelWidth, pixelHeight,
                dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            snapshot.Render(this);
            ThemeSnapshotBrush.ImageSource = snapshot;
        }
        finally
        {
            _dark = previousDark;
            UpdateThemeIcon();
            ApplyTheme(_dark);
        }

        ThemeToggleButton.IsEnabled = false;

        ThemeFillStart.Offset = 0;
        ThemeFillEdge.Offset = 0;
        ThemeFillFade.Offset = 0.03;
        ThemeFillEnd.Offset = 1;
        ThemeOverlay.Visibility = Visibility.Visible;

        var edge = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(340)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        var fade = new DoubleAnimation(0.03, 1, new Duration(TimeSpan.FromMilliseconds(340)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        edge.Completed += (_, _) =>
        {
            _dark = nextDark;
            UpdateThemeIcon();
            ApplyTheme(_dark);

            ThemeToggleButton.IsEnabled = true;
            _themeTransition = false;

            ThemeFillEdge.BeginAnimation(GradientStop.OffsetProperty, null);
            ThemeFillFade.BeginAnimation(GradientStop.OffsetProperty, null);
            ThemeOverlay.Visibility = Visibility.Collapsed;
        };

        ThemeFillEdge.BeginAnimation(GradientStop.OffsetProperty, edge);
        ThemeFillFade.BeginAnimation(GradientStop.OffsetProperty, fade);
    }

    private void UpdateThemeIcon()
    {
        ThemeIcon.Text = _dark ? "\uE706" : "\uE708";
        ThemeToggleButton.ToolTip = _dark ? "Switch to light theme" : "Switch to dark theme";
    }

    private void ApplyTheme(bool dark)
    {
        Color P(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        void Set(string key, string hex)
        {
            Color color = P(hex);
            if (Application.Current.Resources[key] is not SolidColorBrush brush)
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
                return;
            }
            if (brush.IsFrozen)
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
                return;
            }
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
        }

        Set("BgBrush",            dark ? "#0C0C0E" : "#F7F7F8");
        Set("SurfaceBrush",       dark ? "#141417" : "#FFFFFF");
        Set("SurfaceHoverBrush",  dark ? "#1C1C20" : "#F0F0F2");
        Set("HoverBrush",         dark ? "#232329" : "#EAEAEF");
        Set("SelectedBrush",      dark ? "#2B2B33" : "#DEDEE5");
        // В светлой теме поле поиска заметно серым, как выбранный чат.
        Set("SearchFieldBrush",   dark ? "#141417" : "#DEDEE5");
        Set("SplitterBrush", dark ? "#3E3E48" : "#B9B9C4");
        Set("SplitterHoverBrush", dark ? "#6E6E7C" : "#777785");
        Set("ScrollThumbBrush",       dark ? "#38FFFFFF" : "#3D000000");
        Set("ScrollThumbHoverBrush",  dark ? "#55FFFFFF" : "#5A000000");
        Set("ScrollThumbPressedBrush",dark ? "#70FFFFFF" : "#75000000");
        Set("BorderBrush",        dark ? "#26262B" : "#E6E6EA");
        Set("BorderStrongBrush",  dark ? "#3A3A40" : "#D2D2D8");
        Set("TextBrush",          dark ? "#F4F4F5" : "#1A1A1E");
        Set("MutedBrush",         dark ? "#9B9BA3" : "#6B6B74");
        Set("FaintBrush",         dark ? "#6B6B74" : "#9B9BA3");
        Set("AccentBrush",        dark ? "#5B9A74" : "#3F7A5C");
        Set("AccentSoftBrush",    dark ? "#151C18" : "#EEF2EF");
        Set("OnlineBrush",        dark ? "#5B9A74" : "#3F7A5C");
        Set("DangerBrush",        dark ? "#E87979" : "#C43C3C");
        Set("DangerSoftBrush",    dark ? "#2A1414" : "#F8EEEE");
        Set("PrimaryBrush",           dark ? "#EDEDEF" : "#1A1A1E");
        Set("PrimaryHoverBrush",      dark ? "#D8D8DC" : "#2C2C32");
        Set("PrimaryPressBrush",      dark ? "#C8C8CE" : "#0E0E10");
        Set("PrimaryForegroundBrush", dark ? "#141417" : "#FFFFFF");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- ПРОФИЛЬ ----------

    private void RenderUser()
    {
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
        int version = ++_avatarLoadVersion;
        ellipse.Tag = version;

        if (string.IsNullOrWhiteSpace(url)) return;

        _ = LoadAvatarAsync(ellipse, initial, url, version);
    }

    private async Task LoadAvatarAsync(Ellipse ellipse, TextBlock initial, string url, int version)
    {
        try
        {
            byte[] bytes = await _api.DownloadAvatarAsync(url);
            if (!Equals(ellipse.Tag, version)) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            ellipse.Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            initial.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Если аватарку загрузить не удалось, остаётся буквенная заглушка.
        }
    }

    private async void ChatAvatarImage_Loaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        if (image.DataContext is not ChatSummary chat) return;

        string? url = chat.AvatarUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        TextBlock? initial = (image.Parent as Grid)?.Children.OfType<TextBlock>().FirstOrDefault();
        if (Equals(image.Tag, url)) return;

        image.Tag = url;
        image.Source = null;
        if (initial is not null) initial.Visibility = Visibility.Visible;

        try
        {
            byte[] bytes = await _api.DownloadAvatarAsync(url);
            if (!Equals(image.Tag, url)) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            image.Source = bitmap;
            if (initial is not null) initial.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Оставляем буквенную заглушку, если аватар недоступен.
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
        _favoritesChat = items.FirstOrDefault(c => c.IsFavorites);
        string q = NormalizeSearchQuery(_chatSearchQuery);
        bool hasQuery = q.Length > 0;
        bool favoritesMatch = !hasQuery || "Favorites".Contains(q, StringComparison.OrdinalIgnoreCase);
        FavoritesButton.Visibility = _favoritesChat is null || _globalSearch || !favoritesMatch
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_favoritesChat is not null)
        {
            ApplyChatPreview(FavoritesPreview, _favoritesChat.PreviewLine);
        }

        List<ChatSummary> regular = items.Where(c => !c.IsFavorites).ToList();
        if (hasQuery)
        {
            regular = regular.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (c.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.LastMessageText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
        for (int i = 0; i < regular.Count; i++)
        {
            if (i < _chatItems.Count)
            {
                if (_chatItems[i] != regular[i]) _chatItems[i] = regular[i];
            }
            else
            {
                _chatItems.Add(regular[i]);
            }
        }
        while (_chatItems.Count > regular.Count)
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
            ChatList.SelectedItem = selected is not null && !selected.IsFavorites ? selected : null;
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
        bool favorites = chat.IsFavorites;
        // Статус в сети приходит вместе со списком чатов — храним свежий ответ сервера.
        _selectedChat = chat;
        if (!unchanged)
        {
            ThreadTitle.Text = chat.Title;
            ThreadAvatarInitial.Visibility = favorites ? Visibility.Collapsed : Visibility.Visible;
            // У избранного свой синий круг с закладкой — асинхронная загрузка
            // аватарки не должна его перезаписать.
            if (!favorites)
            {
                ApplyAvatar(ThreadAvatar, ThreadAvatarInitial, chat.AvatarUrl, chat.Title);
            }
        }
        ThreadAvatar.Visibility = Visibility.Visible;
        ThreadFavoritesIcon.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
        if (favorites)
        {
            ThreadAvatar.SetResourceReference(Shape.FillProperty, "FavoritesBrush");
        }
        FavoritesButton.Tag = _favoritesChat is not null && chat.Id == _favoritesChat.Id
            ? "Selected"
            : null;
        UpdatePresenceStatus();
        UpdateCallButton();
        NoChatSelected.Visibility = Visibility.Collapsed;
        ThreadPanel.Visibility = Visibility.Visible;
    }

    private void UpdateCallButton()
    {
        ChatSummary? chat = _selectedChat;
        bool canCall = chat is not null
            && !chat.IsFavorites
            && !string.IsNullOrEmpty(chat.PeerUserId);
        CallButton.Visibility = canCall ? Visibility.Visible : Visibility.Collapsed;
        CallButton.IsEnabled = canCall && _callEngineReady && _calls.Current is null;
        CallButton.ToolTip = _callEngineReady ? "Voice call" : "Call engine unavailable";
    }

    private Task<bool> RequestPermissionAsync(string title, string body)
    {
        _permissionTcs?.TrySetResult(false);
        _permissionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PermissionTitle.Text = title;
        PermissionBody.Text = body;
        PermissionOverlay.Visibility = Visibility.Visible;
        return _permissionTcs.Task;
    }

    private async Task<bool> EnsureMicrophonePermissionAsync(string reason)
    {
        if (_appSettings.MicrophonePermissionGranted) return true;
        bool allowed = await RequestPermissionAsync("Microphone access", reason);
        if (allowed)
        {
            _appSettings.MicrophonePermissionGranted = true;
            AppSettingsStore.Save(_appSettings);
        }
        return allowed;
    }

    private void PermissionAllowButton_Click(object sender, RoutedEventArgs e)
    {
        PermissionOverlay.Visibility = Visibility.Collapsed;
        _permissionTcs?.TrySetResult(true);
        _permissionTcs = null;
    }

    private void PermissionDenyButton_Click(object sender, RoutedEventArgs e)
    {
        PermissionOverlay.Visibility = Visibility.Collapsed;
        _permissionTcs?.TrySetResult(false);
        _permissionTcs = null;
    }

    private ListBoxItem CreateScreenPickerItem(ScreenShareSource src)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var previewBorder = new Border
        {
            Width = 160,
            Height = 90,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 12, 0)
        };
        previewBorder.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        var img = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        var preview = CapturePreview(src, 160, 90);
        if (preview is not null) img.Source = preview;
        previewBorder.Child = img;
        Grid.SetColumn(previewBorder, 0);
        panel.Children.Add(previewBorder);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        var name = new TextBlock
        {
            Text = src.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var meta = new TextBlock
        {
            Text = src.Kind == ScreenShareKind.Monitor ? "Screen" : "Window",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(name);
        text.Children.Add(meta);
        panel.Children.Add(text);

        return new ListBoxItem { Content = panel, Tag = src, Padding = new Thickness(6) };
    }

    private static BitmapSource? CapturePreview(ScreenShareSource src, int targetW, int targetH)
    {
        try
        {
            int w = Math.Max(1, src.Width);
            int h = Math.Max(1, src.Height);
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                if (src.Kind == ScreenShareKind.Monitor)
                {
                    g.CopyFromScreen(src.Left, src.Top, 0, 0, new System.Drawing.Size(w, h));
                }
                else
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        bool ok = PrintWindow(src.Handle, hdc, 2);
                        if (!ok)
                        {
                            g.ReleaseHdc(hdc);
                            g.CopyFromScreen(src.Left, src.Top, 0, 0, new System.Drawing.Size(w, h));
                        }
                    }
                    finally
                    {
                        try { g.ReleaseHdc(hdc); } catch { }
                    }
                }
            }

            using var thumb = new System.Drawing.Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.Clear(System.Drawing.Color.FromArgb(32, 32, 32));
                double scale = Math.Min((double)targetW / w, (double)targetH / h);
                int dw = Math.Max(1, (int)(w * scale));
                int dh = Math.Max(1, (int)(h * scale));
                int dx = (targetW - dw) / 2;
                int dy = (targetH - dh) / 2;
                g.DrawImage(bmp, dx, dy, dw, dh);
            }

            IntPtr hBmp = thumb.GetHbitmap();
            try
            {
                BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
        catch { return null; }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private Task<ScreenShareSource?> ShowScreenPickerAsync()
    {
        _screenPickerTcs?.TrySetResult(null);
        _screenPickerTcs = new TaskCompletionSource<ScreenShareSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ScreenPickerMonitors.Items.Clear();
        ScreenPickerWindows.Items.Clear();
        ScreenPickerShareButton.IsEnabled = false;

        foreach (var m in EnumerateMonitors())
            ScreenPickerMonitors.Items.Add(CreateScreenPickerItem(m));
        foreach (var w in EnumerateWindows())
            ScreenPickerWindows.Items.Add(CreateScreenPickerItem(w));

        ScreenPickerOverlay.Visibility = Visibility.Visible;
        return _screenPickerTcs.Task;
    }

    private void ScreenPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == ScreenPickerMonitors && ScreenPickerMonitors.SelectedItem is not null)
            ScreenPickerWindows.SelectedItem = null;
        if (sender == ScreenPickerWindows && ScreenPickerWindows.SelectedItem is not null)
            ScreenPickerMonitors.SelectedItem = null;
        ScreenPickerShareButton.IsEnabled =
            ScreenPickerMonitors.SelectedItem is not null || ScreenPickerWindows.SelectedItem is not null;
    }

    private void ScreenPickerCancel_Click(object sender, RoutedEventArgs e)
    {
        ScreenPickerOverlay.Visibility = Visibility.Collapsed;
        _screenPickerTcs?.TrySetResult(null);
        _screenPickerTcs = null;
    }

    private void ScreenPickerShare_Click(object sender, RoutedEventArgs e)
    {
        ScreenShareSource? src = (ScreenPickerMonitors.SelectedItem as ListBoxItem)?.Tag as ScreenShareSource
            ?? (ScreenPickerWindows.SelectedItem as ListBoxItem)?.Tag as ScreenShareSource;
        ScreenPickerOverlay.Visibility = Visibility.Collapsed;
        _screenPickerTcs?.TrySetResult(src);
        _screenPickerTcs = null;
    }

    private static IReadOnlyList<ScreenShareSource> EnumerateMonitors()
    {
        var list = new List<ScreenShareSource>();
        int i = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            i++;
            var r = lprcMonitor;
            int w = Math.Max(1, r.Right - r.Left);
            int h = Math.Max(1, r.Bottom - r.Top);
            list.Add(new ScreenShareSource(ScreenShareKind.Monitor, $"Screen {i} ({w}x{h})", hMonitor, r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private IReadOnlyList<ScreenShareSource> EnumerateWindows()
    {
        var list = new List<ScreenShareSource>();
        IntPtr self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        EnumWindows((hwnd, lParam) =>
        {
            if (hwnd == self) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (!GetWindowRect(hwnd, out RECT r)) return true;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w < 120 || h < 80) return true;
            list.Add(new ScreenShareSource(ScreenShareKind.Window, title, hwnd, r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
        return list.Take(30).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    private const uint GW_OWNER = 4;

    private async void CallButton_Click(object sender, RoutedEventArgs e)
    {
        ChatSummary? chat = _selectedChat;
        if (chat is null || string.IsNullOrEmpty(chat.PeerUserId) || !_callEngineReady) return;
        if (_calls.Current is not null) return;

        bool allowed = await EnsureMicrophonePermissionAsync(
            "GoPing needs access to your microphone to start a voice call. Your audio is sent peer-to-peer.");
        if (!allowed) return;

        CallButton.IsEnabled = false;
        CallPeerName.Text = chat.Title;
        CallStatusText.Text = "Starting call…";
        if (CallPeerStateText is not null) { CallPeerStateText.Text = ""; CallPeerStateText.Visibility = Visibility.Collapsed; }
        ShowCallActions(active: true);
        CallMuteButton.Content = "\uE720";
        CallMuteButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#2B2D31")!;
        CallMuteButton.ToolTip = "Mute";
        if (CallDeafenButton is not null)
        {
            CallDeafenButton.Content = "\uE7F6";
            CallDeafenButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#2B2D31")!;
            CallDeafenButton.ToolTip = "Deafen";
        }
        CallEndButton.IsEnabled = true;
        UpdateScreenShareUi();
        
        CallOverlay.Visibility = Visibility.Visible;
        FadeIn(CallOverlay);

        try
        {
            await _calls.StartOutgoingAsync(chat.Id, chat.PeerUserId, chat.Title);
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
            ShowCallActions(active: false);
            CallAcceptButton.IsEnabled = false;
            CallRejectButton.IsEnabled = false;
            CallButton.IsEnabled = true;
            _callHideTimer.Stop();
            _callHideTimer.Start();
        }
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
        // Профиль собеседника — узкая панель справа. Переписка остаётся видимой.
        ProfilePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        _viewingPeer = true;

        PeerPanelNickname.Text = peer.Nickname;
        PeerPanelUsername.Text = "@" + peer.Username;
        PeerPanelBio.Text = string.IsNullOrWhiteSpace(peer.Bio) ? "No bio yet." : peer.Bio;
        ApplyAvatar(PeerPanelAvatar, PeerPanelAvatarInitial, peer.AvatarUrl, peer.Nickname);

        _chatListWidthBeforePeer = ChatListColumn.Width;
        ChatListColumn.Width = new GridLength(190);
        PeerPanelColumn.Width = new GridLength(300);
        PeerPanel.Visibility = Visibility.Visible;
        FadeIn(PeerPanel);
    }

    private void ClosePeerPanel()
    {
        if (PeerPanel.Visibility != Visibility.Visible) return;
        _viewingPeer = false;
        PeerPanel.Visibility = Visibility.Collapsed;
        PeerPanelColumn.Width = new GridLength(0);
        if (_chatListWidthBeforePeer.Value > 0)
        {
            ChatListColumn.Width = _chatListWidthBeforePeer;
        }
    }

    private void ShowOwnProfile()
    {
        ClosePeerPanel();
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
        SettingsPanel.Visibility = Visibility.Collapsed;
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
        UpdateCallButton();
    }

    private void ShowEmptyState()
    {
        ThreadPanel.Visibility = Visibility.Collapsed;
        NoChatSelected.Visibility = Visibility.Visible;
        bool empty = _chats.Count == 0;
        EmptyStateTitle.Text = empty ? "No chats yet" : "Select a chat";
        EmptyStateText.Text = empty
            ? "Search for a friend above to start a conversation."
            : "Pick a chat on the left to read the messages.";
    }

    private async void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChatSelection || ChatList.SelectedItem is not ChatSummary chat)
        {
            return;
        }
        _selectedChatId = chat.Id;
        ClosePeerPanel();
        ResetConversationState();
        ShowThread(chat);
        await LoadMessagesAsync();
    }

    private async void FavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_favoritesChat is null) return;

        ClosePeerPanel();
        _suppressChatSelection = true;
        ChatList.SelectedItem = null;
        _suppressChatSelection = false;

        _selectedChatId = _favoritesChat.Id;
        ResetConversationState();
        ShowThread(_favoritesChat);
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
            if (message.IsCall)
                return BuildCallLogRow(message);

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
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };
            text.SetResourceReference(
                TextBlock.ForegroundProperty, own ? "PrimaryForegroundBrush" : "TextBrush");
            if (message.IsCall)
            {
                text.Inlines.Add(new System.Windows.Documents.Run("📞 ") { FontSize = 14 });
                text.Inlines.Add(new System.Windows.Documents.Run(FormatCallMessage(message)));
            }
            else
            {
                foreach (var inline in EmojiCatalog.BuildInlines(message.Text))
                {
                    text.Inlines.Add(inline);
                }
            }
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

    private sealed record CallLogInfo(string Status, long DurationSec, string Who);

    private static CallLogInfo ParseCallLog(ChatMessage message)
    {
        string status = "ended";
        long sec = 0;
        string payload = message.Text ?? "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String)
                status = s.GetString() ?? status;
            if (doc.RootElement.TryGetProperty("durationSec", out JsonElement d) && d.TryGetInt64(out long v))
                sec = Math.Max(0, v);
        }
        catch { }

        string? who = message.Sender?.Nickname;
        if (string.IsNullOrWhiteSpace(who)) who = message.Sender?.Username;
        if (string.IsNullOrWhiteSpace(who)) who = "Someone";
        return new CallLogInfo(status, Math.Max(0, sec), who);
    }

    private static string FormatCallMessage(ChatMessage message)
    {
        CallLogInfo info = ParseCallLog(message);
        string dur = info.DurationSec > 0 ? " · " + FormatDuration(info.DurationSec) : "";
        return info.Status switch
        {
            "missed" or "cancelled" => $"Missed call from {info.Who}",
            "declined" => $"Call declined by {info.Who}",
            _ => $"Call ended{dur}"
        };
    }

    private UIElement BuildCallLogRow(ChatMessage message)
    {
        CallLogInfo info = ParseCallLog(message);
        bool missed = info.Status is "missed" or "cancelled" or "declined";
        string title = info.Status switch
        {
            "missed" or "cancelled" => "Missed call",
            "declined" => "Call declined",
            _ => "Call ended"
        };
        string subtitle = info.Status switch
        {
            "missed" or "cancelled" => $"From {info.Who}",
            "declined" => $"By {info.Who}",
            _ => info.DurationSec > 0 ? $"Duration {FormatDuration(info.DurationSec)}" : $"With {info.Who}"
        };

        var outer = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 14),
            ToolTip = "Right-click for actions"
        };
        outer.MouseRightButtonUp += (_, e) => ShowMessageActions(e, message);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 16, 10),
            BorderThickness = new Thickness(1),
            MinWidth = 220,
            MaxWidth = 360
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconWrap = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconWrap.Background = new SolidColorBrush(missed
            ? Color.FromArgb(40, 218, 55, 60)
            : Color.FromArgb(40, 35, 165, 90));
        var icon = new TextBlock
        {
            Text = missed ? "\uE717" : "\uE717",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = missed ? new RotateTransform(135) : new RotateTransform(0)
        };
        icon.Foreground = new SolidColorBrush(missed
            ? Color.FromRgb(218, 55, 60)
            : Color.FromRgb(35, 165, 90));
        iconWrap.Child = icon;
        Grid.SetColumn(iconWrap, 0);
        grid.Children.Add(iconWrap);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(stack, 1);
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var subBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0)
        };
        subBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var timeBlock = new TextBlock
        {
            Text = message.CreatedAt.ToLocalTime().ToString("HH:mm"),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0)
        };
        timeBlock.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
        stack.Children.Add(titleBlock);
        stack.Children.Add(subBlock);
        stack.Children.Add(timeBlock);
        grid.Children.Add(stack);

        card.Child = grid;
        outer.Children.Add(card);
        return outer;
    }

    private static string FormatDuration(long sec)
    {
        long h = sec / 3600;
        long m = (sec % 3600) / 60;
        long s = sec % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
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

        bool canPin = !message.Id.StartsWith("local:", StringComparison.Ordinal);
        MessagePinButton.Visibility = canPin ? Visibility.Visible : Visibility.Collapsed;

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
        MessageActionsPopup.Height = 48 + (42 * actions);
        PositionMessageActions();
    }

    private void ShowDeleteConfirmView()
    {
        MessageActionsMainPanel.Visibility = Visibility.Collapsed;
        MessageActionsDeletePanel.Visibility = Visibility.Visible;
        MessageActionsPinPanel.Visibility = Visibility.Collapsed;
        MessageActionsHeading.Text = "DELETE MESSAGE";
        MessageActionsPopup.Height = 174;
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
        MessageUnpinButton.Content = scope == "chat" ? "Remove shared pin" : "Remove my pin";

        int actions = 1; // Back
        if (MessagePinForMeButton.Visibility == Visibility.Visible) actions++;
        if (MessagePinForChatButton.Visibility == Visibility.Visible) actions++;
        if (MessageUnpinButton.Visibility == Visibility.Visible) actions++;
        MessageActionsPopup.Height = 48 + (42 * actions);
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
    // ---------- ЛОКАЛЬНЫЙ НАБОР ЭМОДЗИ ----------

    private static string FormatCallPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("status", out JsonElement s) || s.ValueKind != JsonValueKind.String)
                return text;
            string status = s.GetString() ?? "ended";
            long sec = 0;
            if (doc.RootElement.TryGetProperty("durationSec", out JsonElement d) && d.TryGetInt64(out long v))
                sec = Math.Max(0, v);
            string dur = sec > 0 ? " · " + FormatDuration(sec) : "";
            return status switch
            {
                "missed" or "cancelled" => "📞 Missed call",
                "declined" => "📞 Call declined",
                _ => "📞 Call ended" + dur
            };
        }
        catch
        {
            return text;
        }
    }

    private static void ApplyChatPreview(TextBlock block, string preview)
    {
        block.Text = string.Empty;
        string formatted = FormatCallPreview(preview);
        foreach (var inline in EmojiCatalog.BuildInlines(formatted))
        {
            block.Inlines.Add(inline);
        }
    }

    private void ChatPreview_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyChatPreview(block: (TextBlock)sender);
    }

    private void ChatPreview_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ApplyChatPreview(block: (TextBlock)sender);
    }

    private static void ApplyChatPreview(TextBlock block)
    {
        if (block.DataContext is ChatSummary chat)
        {
            ApplyChatPreview(block, chat.PreviewLine);
        }
    }

    private void EmojiButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyEmojiTab();
        EmojiPickerPopup.IsOpen = true;
        EmojiSearchBox.Focus();
    }

    private void ApplyEmojiTab()
    {
        bool unicode = _unicodeEmojiTab;
        bool showPack = !unicode && EmojiCatalog.IsInstalled;

        PackTabButton.Tag = unicode ? null : "Selected";
        UnicodeTabButton.Tag = unicode ? "Selected" : null;
        EmojiMissingText.Visibility = !unicode && !EmojiCatalog.IsInstalled
            ? Visibility.Visible : Visibility.Collapsed;
        EmojiScroller.Visibility = showPack ? Visibility.Visible : Visibility.Collapsed;
        PackPager.Visibility = showPack ? Visibility.Visible : Visibility.Collapsed;
        UnicodeEmojiPanel.Visibility = unicode ? Visibility.Visible : Visibility.Collapsed;
        UnicodeCategoryBar.Visibility = unicode ? Visibility.Visible : Visibility.Collapsed;

        if (unicode) RefreshUnicodeEmojiGrid();
        else RefreshEmojiGrid();
    }

    private void EmojiTab_Click(object sender, RoutedEventArgs e)
    {
        _unicodeEmojiTab = ReferenceEquals(sender, UnicodeTabButton);
        EmojiSearchBox.Text = string.Empty;
        _emojiPage = 0;
        ApplyEmojiTab();
    }

    private IReadOnlyList<EmojiEntry> FilteredEmoji()
    {
        string query = EmojiSearchBox.Text.Trim();
        IReadOnlyList<EmojiEntry> all = EmojiCatalog.Entries;
        if (query.Length == 0) return all;
        return all.Where(e => e.Number.ToString(CultureInfo.InvariantCulture).Contains(query)
                              || e.Token.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshEmojiGrid()
    {
        IReadOnlyList<EmojiEntry> filtered = FilteredEmoji();
        int pages = Math.Max(1, (filtered.Count + EmojiPageSize - 1) / EmojiPageSize);
        _emojiPage = Math.Clamp(_emojiPage, 0, pages - 1);
        EmojiGrid.ItemsSource = filtered.Skip(_emojiPage * EmojiPageSize).Take(EmojiPageSize).ToList();
        EmojiPageText.Text = filtered.Count == 0 ? "0" : $"{_emojiPage + 1} / {pages}";
        EmojiPrevButton.IsEnabled = _emojiPage > 0;
        EmojiNextButton.IsEnabled = _emojiPage < pages - 1;
        EmojiScroller.ScrollToTop();
    }

    private void RefreshUnicodeEmojiGrid()
    {
        string query = EmojiSearchBox.Text.Trim();
        IEnumerable<UnicodeEmoji> items;
        if (query.Length == 0)
        {
            items = UnicodeEmojiCatalog.Categories[_unicodeCategory].Items;
        }
        else
        {
            items = UnicodeEmojiCatalog.Categories.SelectMany(c => c.Items)
                .Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || e.Char.Contains(query, StringComparison.Ordinal));
        }
        UnicodeEmojiGrid.ItemsSource = items.ToList();
    }

    private void EmojiSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _emojiPage = 0;
        if (!EmojiPickerPopup.IsOpen) return;
        if (_unicodeEmojiTab) RefreshUnicodeEmojiGrid();
        else RefreshEmojiGrid();
    }

    private void UnicodeCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not UnicodeEmojiCategory category) return;

        int index = 0;
        foreach (UnicodeEmojiCategory item in UnicodeEmojiCatalog.Categories)
        {
            if (ReferenceEquals(item, category)) break;
            index++;
        }
        _unicodeCategory = Math.Clamp(index, 0, UnicodeEmojiCatalog.Categories.Count - 1);

        foreach (UnicodeEmojiCategory item in UnicodeEmojiCatalog.Categories)
        {
            if (UnicodeCategoryList.ItemContainerGenerator.ContainerFromItem(item) is Button button)
            {
                button.Tag = ReferenceEquals(item, category) ? "Selected" : null;
            }
        }

        EmojiSearchBox.Text = string.Empty;
        RefreshUnicodeEmojiGrid();
    }

    private void EmojiPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_emojiPage <= 0) return;
        _emojiPage--;
        RefreshEmojiGrid();
    }

    private void EmojiNextPage_Click(object sender, RoutedEventArgs e)
    {
        _emojiPage++;
        RefreshEmojiGrid();
    }

    private void EmojiImage_Loaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        if (image.DataContext is EmojiEntry entry)
        {
            image.Source = EmojiCatalog.GetImage(entry.Number);
        }
    }

    private void EmojiItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not int number) return;

        // Если для картинки есть Unicode-соответствие — отправляем символ, а не код.
        string value = EmojiCatalog.GetUnicode(number) ?? $":emoji_{number}:";
        InsertIntoMessageBox(value);
    }

    private void UnicodeEmojiItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is UnicodeEmoji emoji)
        {
            InsertIntoMessageBox(emoji.Char);
        }
    }

    private void InsertIntoMessageBox(string value)
    {
        int caret = MessageBox.SelectionStart;
        MessageBox.Text = MessageBox.Text.Insert(caret, value);
        MessageBox.SelectionStart = caret + value.Length;
        MessageBox.Focus();
        EmojiPickerPopup.IsOpen = false;
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
            UpdatePresenceStatus();
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

    private void OnRealtimeStatus(RealtimeStatus status, int attempt)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (status)
            {
                case RealtimeStatus.Connected:
                    ChatsTitle.Text = "Chats · connected";
                    ChatsTitle.ToolTip = null;
                    _connectionFailureDialogShown = false;
                    ConnectionFailureOverlay.Visibility = Visibility.Collapsed;
                    if (_realtimeWasDisconnected)
                    {
                        _realtimeWasDisconnected = false;
                        _ = OnRealtimeReconnectedAsync();
                    }
                    break;
                case RealtimeStatus.Reconnecting:
                    ChatsTitle.Text = "Chats · reconnecting";
                    ChatsTitle.ToolTip = $"Attempt {attempt} of 5";
                    _realtimeWasDisconnected = true;
                    break;
                case RealtimeStatus.Failed:
                    ChatsTitle.Text = "Chats · error";
                    ChatsTitle.ToolTip = "Connection unavailable";
                    if (!_connectionFailureDialogShown)
                    {
                        _connectionFailureDialogShown = true;
                        ConnectionFailureOverlay.Visibility = Visibility.Visible;
                        FadeIn(ConnectionFailureOverlay);
                    }
                    break;
            }
        }));
    }

    private async Task OnRealtimeReconnectedAsync()
    {
        try
        {
            await _calls.OnRealtimeReconnectedAsync();
            await _realtime.FlushPendingAsync();
        }
        catch
        {
            // Тихий rejoin не должен показывать ошибки пользователю.
        }
    }

    private void ConnectionFailureCloseButton_Click(object sender, RoutedEventArgs e)
        => ConnectionFailureOverlay.Visibility = Visibility.Collapsed;

    private void ConnectionFailureRetryButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionFailureOverlay.Visibility = Visibility.Collapsed;
        ChatsTitle.Text = "Chats · reconnecting";
        _connectionFailureDialogShown = false;
        _realtime.Reconnect();
    }

    private void ConnectionFailureResizeGrip_DragDelta(
        object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        ConnectionFailureCard.Width = Math.Min(ConnectionFailureCard.MaxWidth,
            Math.Max(ConnectionFailureCard.MinWidth, ConnectionFailureCard.Width + e.HorizontalChange));
        ConnectionFailureCard.Height = Math.Min(ConnectionFailureCard.MaxHeight,
            Math.Max(ConnectionFailureCard.MinHeight, ConnectionFailureCard.Height + e.VerticalChange));
    }

    private void ShowCallActions(bool active)
    {
        CallIncomingActions.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        CallActiveActions.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCallIncoming(CallInfo call)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _callHideTimer.Stop();
            CallPeerName.Text = call.PeerName;
            CallStatusText.Text = "Incoming voice call";
            if (CallPeerStateText is not null) { CallPeerStateText.Text = ""; CallPeerStateText.Visibility = Visibility.Collapsed; }
            ShowCallActions(active: false);
            CallAcceptButton.IsEnabled = true;
            CallRejectButton.IsEnabled = true;
            UpdateScreenShareUi();

            if (_appSettings.RaiseWindowOnIncomingCall)
                BringWindowToFront();

            CallOverlay.Visibility = Visibility.Visible;
            FadeIn(CallOverlay);
            UpdateCallButton();
        }));
    }

    private void OnCallStateChanged(CallStatus status, string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (status == CallStatus.Ended)
            {
                SetSpeakingRings(false, false);
                _callDurationTimer.Stop();
                _callConnectedAt = null;
                _remoteScreenSharing = false;
                _remoteVideoBitmap = null;
                if (CallRemoteVideo is not null)
                {
                    CallRemoteVideo.Source = null;
                    CallRemoteVideo.Visibility = Visibility.Collapsed;
                }
                if (CallWebView is not null) CallWebView.Visibility = Visibility.Collapsed;
                if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Visible;
                if (CallStatsPanel is not null)
                {
                    CallStatsPanel.Visibility = Visibility.Collapsed;
                    CallPingText.Text = "Ping: —";
                    CallJitterText.Text = "Jitter: —";
                    CallPacketLossText.Text = "Packets lost: —";
                    CallJitterHistoryText.Text = "—";
                }
                if (CallPeerStateText is not null)
                {
                    CallPeerStateText.Text = "";
                    CallPeerStateText.Visibility = Visibility.Collapsed;
                }
                CallStatusText.Text = text;
                ShowCallActions(active: false);
                CallAcceptButton.IsEnabled = false;
                CallRejectButton.IsEnabled = false;
                UpdateScreenShareUi();

                UpdateCallButton();
                _callHideTimer.Stop();
                _callHideTimer.Start();
                return;
            }

            _callHideTimer.Stop();
            CallPeerName.Text = _calls.Current?.PeerName ?? CallPeerName.Text;
            bool screenSharingStatus = string.Equals(text, "Screen sharing", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(text, "Connected", StringComparison.OrdinalIgnoreCase) || screenSharingStatus)
            {
                if (_callConnectedAt is null)
                {
                    _callConnectedAt = DateTime.UtcNow;
                    _callDurationTimer.Start();
                }
                if (screenSharingStatus)
                {
                    _remoteScreenSharing = true;
                    CallStatusText.Text = text;
                    if (CallWebView is not null) CallWebView.Visibility = Visibility.Visible;
                    if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _remoteScreenSharing = false;
                    if (CallWebView is not null) CallWebView.Visibility = Visibility.Collapsed;
                    if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Visible;
                    UpdateCallDurationText();
                }
            }
            else
            {
                if (!string.Equals(text, "Reconnecting…", StringComparison.Ordinal))
                {
                    // keep timer running through transient statuses only when already connected
                }
                CallStatusText.Text = text;
            }
            bool active = status is CallStatus.Active or CallStatus.Outgoing;
            ShowCallActions(active);
            CallAcceptButton.IsEnabled = true;
            CallRejectButton.IsEnabled = true;
            CallEndButton.IsEnabled = true;
            bool muted = _calls.IsMuted;
            CallMuteButton.Content = "\uE720";
            CallMuteButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(muted ? "#DA373C" : "#2B2D31")!;
            CallMuteButton.ToolTip = muted ? "Unmute" : "Mute";
            bool deafened = _calls.IsDeafened;
            if (CallDeafenButton is not null)
            {
                CallDeafenButton.Content = "\uE7F6";
                CallDeafenButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(deafened ? "#DA373C" : "#2B2D31")!;
                CallDeafenButton.ToolTip = deafened ? "Undeafen" : "Deafen";
            }
            UpdateScreenShareUi();
            CallOverlay.Visibility = Visibility.Visible;
            
            UpdateCallButton();
        }));
    }

    private void OnCallSpeakingChanged(bool localSpeaking, bool remoteSpeaking)
    {
        Dispatcher.BeginInvoke(new Action(() => SetSpeakingRings(localSpeaking, remoteSpeaking)));
    }

    private void OnCallStatsUpdated(CallStatsSnapshot stats)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CallStatsPanel is null) return;
            CallStatsPanel.Visibility = Visibility.Visible;
            CallPingText.Text = stats.RttMs.HasValue ? $"Ping: {stats.RttMs.Value} ms" : "Ping: —";
            CallJitterText.Text = stats.JitterMs.HasValue ? $"Jitter: {stats.JitterMs.Value:0.0} ms" : "Jitter: —";
            string loss = stats.PacketsLost.HasValue ? stats.PacketsLost.Value.ToString() : "—";
            string recv = stats.PacketsReceived.HasValue ? stats.PacketsReceived.Value.ToString() : "—";
            CallPacketLossText.Text = $"Packets lost: {loss} / {recv}";

            if (stats.History.Count == 0)
            {
                CallJitterHistoryText.Text = "—";
            }
            else
            {
                var lines = stats.History
                    .TakeLast(15)
                    .Select(p =>
                    {
                        string rtt = p.Rtt.HasValue ? $"{p.Rtt.Value,4}ms" : "   — ";
                        return $"j {p.Jitter,6:0.0}ms | rtt {rtt}";
                    });
                CallJitterHistoryText.Text = string.Join(Environment.NewLine, lines);
            }
        }));
    }

    private void OnCallScreenShareStateChanged(bool sharing, string? error)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                CallStatusText.Text = error!;
                if (!sharing && !_remoteScreenSharing && CallWebView is not null)
                {
                    CallWebView.Visibility = Visibility.Collapsed;
                    if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Visible;
                }
            }

            if (!sharing && !_remoteScreenSharing && string.IsNullOrWhiteSpace(error) && CallWebView is not null)
            {
                CallWebView.Visibility = Visibility.Collapsed;
                if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Visible;
            }

            if (sharing)
            {
                if (CallWebView is not null) CallWebView.Visibility = Visibility.Visible;
                if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Collapsed;
            }

            UpdateScreenShareUi();
        }));
    }

    private void OnCallPeerMediaStateChanged(bool muted, bool deafened)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CallPeerStateText is null) return;
            var parts = new List<string>();
            if (muted) parts.Add("muted mic");
            if (deafened) parts.Add("deafened");
            if (parts.Count == 0)
            {
                CallPeerStateText.Visibility = Visibility.Collapsed;
                CallPeerStateText.Text = "";
            }
            else
            {
                CallPeerStateText.Text = "Peer " + string.Join(" & ", parts);
                CallPeerStateText.Visibility = Visibility.Visible;
            }
        }));
    }

    private void OnRemoteVideoFrame(byte[] bgr24, int width, int height)
    {
        if (bgr24 is null || width <= 0 || height <= 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_remoteVideoBitmap is null || _remoteVideoBitmap.PixelWidth != width || _remoteVideoBitmap.PixelHeight != height)
                {
                    _remoteVideoBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    CallRemoteVideo.Source = _remoteVideoBitmap;
                }
                int stride = width * 3;
                _remoteVideoBitmap.WritePixels(new Int32Rect(0, 0, width, height), bgr24, stride, 0);
                if (CallRemoteVideo.Visibility != Visibility.Visible)
                {
                    CallRemoteVideo.Visibility = Visibility.Visible;
                    CallPeerCenter.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }));
    }

    private void OnRemoteVideoCleared()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CallRemoteVideo.Source = null;
            CallRemoteVideo.Visibility = Visibility.Collapsed;
            CallPeerCenter.Visibility = Visibility.Visible;
            _remoteVideoBitmap = null;
        }));
    }

    private void SetSpeakingRings(bool localSpeaking, bool remoteSpeaking)
    {
        if (PeerSpeakingRing is not null) PeerSpeakingRing.Opacity = remoteSpeaking ? 1 : 0;
        if (LocalSpeakingRing is not null) LocalSpeakingRing.Opacity = localSpeaking ? 1 : 0;
    }

    private void UpdateCallDurationText()
    {
        if (_callConnectedAt is null) return;
        if (string.Equals(CallStatusText.Text, "Reconnecting…", StringComparison.Ordinal)) return;
        if (_remoteScreenSharing) return;
        TimeSpan elapsed = DateTime.UtcNow - _callConnectedAt.Value;
        CallStatusText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
    }

    private async void CallAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        CallAcceptButton.IsEnabled = false;
        bool allowed = await EnsureMicrophonePermissionAsync(
            "GoPing needs access to your microphone to accept this call. Your audio is sent peer-to-peer.");
        if (!allowed)
        {
            CallAcceptButton.IsEnabled = true;
            return;
        }
        try
        {
            await _calls.AcceptIncomingAsync();
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
            CallAcceptButton.IsEnabled = true;
        }
    }

    private async void CallRejectButton_Click(object sender, RoutedEventArgs e)
    {
        CallRejectButton.IsEnabled = false;
        try
        {
            await _calls.RejectIncomingAsync();
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
            CallRejectButton.IsEnabled = true;
        }
    }

    private async void CallEndButton_Click(object sender, RoutedEventArgs e)
    {
        CallEndButton.IsEnabled = false;
        try
        {
            await _calls.EndAsync();
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
            CallEndButton.IsEnabled = true;
        }
    }

    private async void CallMuteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool muted = !_calls.IsMuted;
            await _calls.SetMutedAsync(muted);
            CallMuteButton.Content = "\uE720";
            CallMuteButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(muted ? "#DA373C" : "#2B2D31")!;
            CallMuteButton.ToolTip = muted ? "Unmute" : "Mute";
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
        }
    }

    private async void CallDeafenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool deafened = !_calls.IsDeafened;
            await _calls.SetDeafenedAsync(deafened);
            CallDeafenButton.Content = "\uE7F6";
            CallDeafenButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(deafened ? "#DA373C" : "#2B2D31")!;
            CallDeafenButton.ToolTip = deafened ? "Undeafen" : "Deafen";
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
        }
    }

    private async void CallShareButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_calls.IsScreenSharing)
            {
                await _calls.StopScreenShareAsync();
                UpdateScreenShareUi();
                return;
            }

            if (CallWebView is not null) CallWebView.Visibility = Visibility.Visible;
            if (CallPeerCenter is not null) CallPeerCenter.Visibility = Visibility.Collapsed;
            ScreenShareSource source = new(ScreenShareKind.Monitor, "Screen", IntPtr.Zero, 0, 0, 0, 0);
            await _calls.StartScreenShareAsync(source);
            UpdateScreenShareUi();
        }
        catch (Exception ex)
        {
            CallStatusText.Text = ex.Message;
            UpdateScreenShareUi();
        }
    }

    private void UpdateScreenShareUi()
    {
        if (CallShareButton is null) return;
        bool sharing = _calls.IsScreenSharing;
        CallShareButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(sharing ? "#23A55A" : "#2B2D31")!;
        CallShareButton.ToolTip = sharing ? "Stop sharing" : "Share screen";
    }

    private Task OnRealtimeEventAsync(string payload)
        => Dispatcher.InvokeAsync(() => HandleRealtimeEventAsync(payload)).Task.Unwrap();

    private async Task HandleRealtimeEventAsync(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string type = root.TryGetProperty("type", out JsonElement typeNode)
            ? typeNode.GetString() ?? string.Empty
            : string.Empty;

        if (type.StartsWith("call.", StringComparison.Ordinal))
        {
            await HandleCallEventAsync(root, type);
            return;
        }

        if (_handlingRealtime) return;
        _handlingRealtime = true;

        try
        {
            if (type is "typing" or "pong") return;

            if (type == "presence")
            {
                string? presenceUserId = ReadString(root, "userId");
                bool online = root.TryGetProperty("online", out JsonElement onlineNode)
                    && onlineNode.ValueKind == JsonValueKind.True;
                DateTimeOffset? lastSeenAt = null;
                if (root.TryGetProperty("lastSeenAt", out JsonElement lsNode)
                    && lsNode.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(lsNode.GetString(), out DateTimeOffset ls))
                {
                    lastSeenAt = ls;
                }
                if (!string.IsNullOrWhiteSpace(presenceUserId))
                    ApplyPresenceUpdate(presenceUserId, online, lastSeenAt);
                return;
            }

            string? chatId = ReadString(root, "chatId");

            switch (type)
            {
                case "message.created":
                    ChatMessage? created = null;
                    if (root.TryGetProperty("message", out JsonElement messageNode))
                    {
                        created = JsonSerializer.Deserialize<ChatMessage>(
                            messageNode.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (created is not null) PrunePendingDuplicates(new[] { created });
                    }
                    await RefreshChatsAsync(selectFirst: false);
                    if (chatId == _selectedChatId)
                    {
                        _messageSignature = string.Empty;
                        await LoadMessagesAsync();
                    }
                    if (created is not null
                        && !string.Equals(created.Sender.Id, _user.Id, StringComparison.Ordinal)
                        && !created.IsCall
                        && _appSettings.ShowMessageNotifications
                        && (!IsActive || !string.Equals(chatId, _selectedChatId, StringComparison.Ordinal)))
                    {
                        string sender = !string.IsNullOrWhiteSpace(created.Sender.Nickname)
                            ? created.Sender.Nickname
                            : created.Sender.Username;
                        string body = created.Text ?? string.Empty;
                        if (body.Length > 140) body = body[..140] + "…";
                        ShowInAppNotification(sender, body, created.Sender.AvatarUrl, chatId);
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

    private async Task HandleCallEventAsync(JsonElement root, string type)
    {
        string? callId = ReadString(root, "callId");
        if (string.IsNullOrEmpty(callId)) return;

        string? chatId = ReadString(root, "chatId");
        string? fromUserId = ReadString(root, "fromUserId");

        switch (type)
        {
            case "call.invite":
                string peerName = chatId == _selectedChatId && _selectedChat is not null
                    ? _selectedChat.Title
                    : _chats.FirstOrDefault(c => c.Id == chatId)?.Title ?? "GoPing user";
                await _calls.HandleInviteAsync(callId, chatId ?? string.Empty, fromUserId ?? string.Empty, peerName);
                break;

            case "call.accepted":
                await _calls.HandleAcceptedAsync(callId);
                break;

            case "call.rejected":
                await _calls.HandleRejectedAsync(callId);
                break;

            case "call.ended":
                await _calls.HandleEndedAsync(callId);
                break;

            case "call.signal":
                string kind = ReadString(root, "kind") ?? string.Empty;
                JsonElement data = root.TryGetProperty("data", out JsonElement dataNode)
                    ? dataNode
                    : default;
                await _calls.HandleSignalAsync(callId, kind, data);
                break;

            case "call.peer-rejoined":
                await _calls.HandlePeerRejoinedAsync();
                break;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    private void SetChatStatus(string message, bool isError)
    {
        ChatStatusText.Text = message;
        ChatStatusText.SetResourceReference(
            TextBlock.ForegroundProperty, isError ? "DangerBrush" : "MutedBrush");
    }

    // Вместо «Encrypted» показываем последний статус в сети, который знает сервер.
    private void UpdatePresenceStatus()
    {
        ChatSummary? chat = _selectedChat;
        if (chat is null || chat.IsFavorites)
        {
            SetChatStatus(string.Empty, false);
            return;
        }

        if (chat.PeerOnline)
        {
            SetChatStatus("Online", false);
            return;
        }

        if (chat.PeerLastSeenAt is DateTimeOffset lastSeen)
        {
            SetChatStatus("Last seen " + FormatLastSeen(lastSeen), false);
            return;
        }

        SetChatStatus("Offline", false);
    }

    private static string FormatLastSeen(DateTimeOffset lastSeen)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset local = lastSeen.ToLocalTime();
        TimeSpan delta = now - local;
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        if (delta < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)delta.TotalHours)} h ago";
        if (local.Date == now.Date.AddDays(-1)) return $"yesterday at {local:HH:mm}";
        if (delta < TimeSpan.FromDays(7)) return $"{(int)delta.TotalDays} d ago";
        return local.ToString("dd.MM.yyyy HH:mm");
    }

    private void ApplyPresenceUpdate(string userId, bool online, DateTimeOffset? lastSeenAt)
    {
        void UpdateCollection(ObservableCollection<ChatSummary> collection)
        {
            for (int i = 0; i < collection.Count; i++)
            {
                ChatSummary c = collection[i];
                if (!string.Equals(c.PeerUserId, userId, StringComparison.Ordinal)) continue;
                collection[i] = c with
                {
                    PeerOnline = online,
                    PeerLastSeenAt = lastSeenAt ?? c.PeerLastSeenAt
                };
            }
        }

        UpdateCollection(_chatItems);

        if (_chats.Count > 0)
        {
            var chats = _chats.ToList();
            bool changed = false;
            for (int i = 0; i < chats.Count; i++)
            {
                ChatSummary c = chats[i];
                if (!string.Equals(c.PeerUserId, userId, StringComparison.Ordinal)) continue;
                chats[i] = c with
                {
                    PeerOnline = online,
                    PeerLastSeenAt = lastSeenAt ?? c.PeerLastSeenAt
                };
                changed = true;
            }
            if (changed) _chats = chats;
        }

        if (_selectedChat is not null && string.Equals(_selectedChat.PeerUserId, userId, StringComparison.Ordinal))
        {
            _selectedChat = _selectedChat with
            {
                PeerOnline = online,
                PeerLastSeenAt = lastSeenAt ?? _selectedChat.PeerLastSeenAt
            };
        }

        UpdatePresenceStatus();
    }

    // ---------- ПОИСК В СПИСКЕ ЧАТОВ ----------

    private static string NormalizeSearchQuery(string value)
        => value.Trim().TrimStart('@').Trim();

    private void UpdateSearchFiltersVisibility()
    {
        bool active = ChatSearchBox.IsFocused
            || MyChatsFilterButton.IsFocused
            || GlobalFilterButton.IsFocused
            || NormalizeSearchQuery(ChatSearchBox.Text).Length > 0;
        SearchFilters.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChatSearchBox_GotFocus(object sender, RoutedEventArgs e)
        => UpdateSearchFiltersVisibility();

    private async void ChatSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Небольшая задержка: клик по фильтру сначала забирает фокус у поля.
        await Task.Delay(120);
        UpdateSearchFiltersVisibility();
    }

    private void MyChatsFilterButton_Click(object sender, RoutedEventArgs e)
        => SetSearchFilter(global: false);

    private void GlobalFilterButton_Click(object sender, RoutedEventArgs e)
        => SetSearchFilter(global: true);

    private void SetSearchFilter(bool global)
    {
        if (_globalSearch == global) return;
        _globalSearch = global;
        MyChatsFilterButton.Tag = global ? null : "Selected";
        GlobalFilterButton.Tag = global ? "Selected" : null;
        UpdateSearchFiltersVisibility();

        _userSearchTimer.Stop();
        _userSearchVersion++;
        _selectedSearchUser = null;
        GlobalSearchResults.ItemsSource = null;
        GlobalSearchResults.Visibility = Visibility.Collapsed;

        if (global)
        {
            FavoritesButton.Visibility = Visibility.Collapsed;
            ChatList.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChatList.Visibility = Visibility.Visible;
            FavoritesButton.Visibility = _favoritesChat is null
                ? Visibility.Collapsed : Visibility.Visible;
            SyncChatList(_chats);
        }

        if (global && NormalizeSearchQuery(ChatSearchBox.Text).Length > 0)
        {
            _userSearchTimer.Start();
        }
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _selectedSearchUser = null;
        _userSearchTimer.Stop();
        _userSearchVersion++;
        _chatSearchQuery = NormalizeSearchQuery(ChatSearchBox.Text);
        UpdateSearchFiltersVisibility();

        if (_globalSearch)
        {
            GlobalSearchResults.ItemsSource = null;
            GlobalSearchResults.Visibility = Visibility.Collapsed;
            if (_chatSearchQuery.Length > 0)
            {
                _userSearchTimer.Start();
            }
        }
        else
        {
            SyncChatList(_chats);
        }
    }

    private void ChatSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ChatSearchBox.Text = string.Empty;
            return;
        }
        if (e.Key == Key.Enter && _globalSearch)
        {
            e.Handled = true;
            if (GlobalSearchResults.ItemsSource is IReadOnlyList<UserProfile> found && found.Count > 0)
            {
                _ = CreateChatAsync(found[0].Username);
            }
        }
    }

    private async Task RunUserSearchAsync()
    {
        if (!_globalSearch) return;
        string query = NormalizeSearchQuery(ChatSearchBox.Text);
        if (query.Length == 0) return;

        int version = ++_userSearchVersion;
        try
        {
            IReadOnlyList<UserProfile> found = await _api.SearchUsersAsync(query);
            if (version != _userSearchVersion || !_globalSearch) return;
            GlobalSearchResults.ItemsSource = found;
            GlobalSearchResults.Visibility = found.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch
        {
            if (version != _userSearchVersion) return;
            GlobalSearchResults.ItemsSource = null;
            GlobalSearchResults.Visibility = Visibility.Collapsed;
        }
    }

    private async void GlobalSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GlobalSearchResults.SelectedItem is not UserProfile user || _creatingChat) return;
        GlobalSearchResults.SelectedItem = null;
        _selectedSearchUser = user;
        await CreateChatAsync(user.Username);
    }

    private async Task CreateChatAsync(string? username = null)
    {
        if (_creatingChat) return;

        username ??= _selectedSearchUser?.Username ?? NormalizeSearchQuery(ChatSearchBox.Text);
        if (string.IsNullOrWhiteSpace(username)) return;

        _creatingChat = true;
        try
        {
            ChatSummary chat = await _api.CreateChatAsync(username);
            _userSearchTimer.Stop();
            _userSearchVersion++;
            ChatSearchBox.Text = string.Empty;
            SetSearchFilter(global: false);
            ClosePeerPanel();
            await RefreshChatsAsync(selectFirst: false, selectChatId: chat.Id);
        }
        catch (ApiException ex)
        {
            SetChatStatus(ex.Message, true);
        }
        finally
        {
            _creatingChat = false;
        }
    }

    private void ShowInAppNotification(string title, string body, string? avatarUrl, string? chatId = null, bool isCall = false)
    {
        if (NotificationHost is null) return;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            Cursor = chatId is not null ? Cursors.Hand : Cursors.Arrow,
            SnapsToDevicePixels = true
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 2,
            Opacity = 0.28,
            Color = Colors.Black
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatarBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var avatarImage = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        var avatarInitial = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "?" : title.Trim()[0].ToString().ToUpperInvariant(),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarInitial.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var avatarGrid = new Grid();
        avatarGrid.Children.Add(avatarInitial);
        avatarGrid.Children.Add(avatarImage);
        avatarBorder.Child = avatarGrid;
        Grid.SetColumn(avatarBorder, 0);
        grid.Children.Add(avatarBorder);

        if (!string.IsNullOrWhiteSpace(avatarUrl))
        {
            _ = LoadNotificationAvatarAsync(avatarImage, avatarInitial, avatarUrl);
        }

        var textStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(textStack, 1);
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var bodyBlock = new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        bodyBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        textStack.Children.Add(titleBlock);
        textStack.Children.Add(bodyBlock);
        grid.Children.Add(textStack);

        var closeBtn = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, -2, -4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        closeBtn.SetResourceReference(Button.ForegroundProperty, "FaintBrush");
        closeBtn.Click += (_, args) =>
        {
            args.Handled = true;
            NotificationHost.Children.Remove(card);
        };
        Grid.SetColumn(closeBtn, 2);
        grid.Children.Add(closeBtn);

        card.Child = grid;

        if (chatId is not null)
        {
            card.MouseLeftButtonUp += async (_, _) =>
            {
                NotificationHost.Children.Remove(card);
                await OpenChatFromNotificationAsync(chatId);
            };
        }

        NotificationHost.Children.Insert(0, card);
        while (NotificationHost.Children.Count > 4)
            NotificationHost.Children.RemoveAt(NotificationHost.Children.Count - 1);

        _ = DismissNotificationAfterAsync(card, isCall ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(4));
    }

    private async Task LoadNotificationAvatarAsync(System.Windows.Controls.Image image, TextBlock initial, string url)
    {
        try
        {
            byte[] bytes = await _api.DownloadAvatarAsync(url);
            await Dispatcher.InvokeAsync(() =>
            {
                var bitmap = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                image.Source = bitmap;
                initial.Visibility = Visibility.Collapsed;
            });
        }
        catch { }
    }

    private async Task DismissNotificationAfterAsync(Border card, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await Dispatcher.InvokeAsync(() =>
            {
                if (NotificationHost?.Children.Contains(card) == true)
                    NotificationHost.Children.Remove(card);
            });
        }
        catch { }
    }

    private async Task OpenChatFromNotificationAsync(string chatId)
    {
        try
        {
            BringWindowToFront();
            ChatSummary? chat = _chats.FirstOrDefault(c => c.Id == chatId) ?? _favoritesChat;
            if (chat is null || !string.Equals(chat.Id, chatId, StringComparison.Ordinal))
            {
                await RefreshChatsAsync(selectFirst: false);
                chat = _chats.FirstOrDefault(c => c.Id == chatId) ?? (_favoritesChat?.Id == chatId ? _favoritesChat : null);
            }
            if (chat is null) return;

            _suppressChatSelection = true;
            ChatList.SelectedItem = _chats.FirstOrDefault(c => c.Id == chat.Id);
            _suppressChatSelection = false;
            _selectedChatId = chat.Id;
            ClosePeerPanel();
            ResetConversationState();
            ShowThread(chat);
            await LoadMessagesAsync();
        }
        catch { }
    }

    private void BringWindowToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowOwnProfile();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMessageActions();
        ClosePeerPanel();
        ProfilePanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Collapsed;
        ApplySettingsToUi();
        SettingsPanel.Visibility = Visibility.Visible;
        FadeIn(SettingsPanel);
    }

    private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChatPanel();
    }

    private void ApplySettingsToUi()
    {
        _suppressSettingsSave = true;
        try
        {
            if (MessageNotificationsCheckBox is not null)
                MessageNotificationsCheckBox.IsChecked = _appSettings.ShowMessageNotifications;
            if (RaiseWindowOnCallCheckBox is not null)
                RaiseWindowOnCallCheckBox.IsChecked = _appSettings.RaiseWindowOnIncomingCall;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        _appSettings.ShowMessageNotifications = MessageNotificationsCheckBox.IsChecked == true;
        _appSettings.RaiseWindowOnIncomingCall = RaiseWindowOnCallCheckBox.IsChecked == true;
        AppSettingsStore.Save(_appSettings);
    }

    private void ShowChatPanel()
    {
        ClosePeerPanel();
        SettingsPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        FadeIn(ChatPanel);
    }

    private void PeerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _viewingPeer = false;
        ShowChatPanel();
    }

    private void PeerPanelCloseButton_Click(object sender, RoutedEventArgs e)
        => ClosePeerPanel();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingPeer)
        {
            _viewingPeer = false;
            ShowChatPanel();
            return;
        }
        RenderUser();
        ProfileStatusText.Text = string.Empty;
        ShowChatPanel();
    }

    private void FadeIn(FrameworkElement panel)
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
            ShowChatPanel();
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

