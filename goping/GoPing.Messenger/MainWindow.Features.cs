using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoPing.Models;
using GoPing.Services;

namespace GoPing;

public partial class MainWindow
{
    private readonly Dictionary<string, int> _unreadCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _listenedVoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _voiceSpeeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private string? _typingChatId;
    private MediaPayload? _activeMedia;
    private string? _activeMediaPath;
    private string _separatorKey = string.Empty;
    private string? _activeVoiceMessageId;
    private readonly Dictionary<string, TextBlock> _voiceListenedGlyphs = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeFeatures()
    {
        CacheService.InMemoryCleanup = () =>
        {
            EmojiCatalog.ClearCache();
            _messageRows.Clear();
            _unreadCounts.Clear();
            AppLog.Info("In-memory caches cleared.");
        };

        if (MessageBox is not null)
            MessageBox.TextChanged += (_, _) => NotifyTyping();

        if (TypingExperimentalCheckBox is not null)
            TypingExperimentalCheckBox.Checked += FeatureSettingsToggle_Changed;
        if (TypingExperimentalCheckBox is not null)
            TypingExperimentalCheckBox.Unchecked += FeatureSettingsToggle_Changed;
        if (NativeNotificationsCheckBox is not null)
            NativeNotificationsCheckBox.Checked += FeatureSettingsToggle_Changed;
        if (NativeNotificationsCheckBox is not null)
            NativeNotificationsCheckBox.Unchecked += FeatureSettingsToggle_Changed;
        if (PollCreateButton is not null) PollCreateButton.Click += PollCreateButton_Click;
        if (PollCancelButton is not null) PollCancelButton.Click += PollCancelButton_Click;
        if (AddServerButton is not null) AddServerButton.Click += AddServerButton_Click;
        if (RemoveServerButton is not null) RemoveServerButton.Click += RemoveServerButton_Click;
        if (ResetServersButton is not null) ResetServersButton.Click += ResetServersButton_Click;
        if (ServersListBox is not null) ServersListBox.SelectionChanged += ServersListBox_SelectionChanged;
        if (SaveLimitsButton is not null) SaveLimitsButton.Click += SaveLimitsButton_Click;
        if (ClearCacheButton is not null) ClearCacheButton.Click += ClearCacheButton_Click;
        if (OpenAdminPanelButton is not null) OpenAdminPanelButton.Click += OpenAdminPanelButton_Click;
        if (AdminCloseButton is not null) AdminCloseButton.Click += AdminCloseButton_Click;
        if (AdminLoadLogsButton is not null) AdminLoadLogsButton.Click += AdminLoadLogsButton_Click;
        if (AdminIssueTokenButton is not null) AdminIssueTokenButton.Click += AdminIssueTokenButton_Click;
        if (MediaSaveButton is not null) MediaSaveButton.Click += MediaSaveButton_Click;
        if (MediaCloseButton is not null) MediaCloseButton.Click += MediaCloseButton_Click;

        _typingTimer.Tick += (_, _) =>
        {
            _typingTimer.Stop();
            _typingChatId = null;
        };

        ApplyFeatureSettingsToUi();
        ApplyActiveServer();
        UpdateCacheSizeText();
        _ = LoadLimitsFromServerAsync();
    }

    internal static bool IsMediaKind(string? kind)
        => string.Equals(kind, "photo", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase);

    internal static string FormatDaySeparator(DateTime day)
    {
        int days = (DateTime.Today - day.Date).Days;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days < 7) return day.ToString("dddd");
        if (days < 14) return "A week ago";
        if (days < 21) return "Two weeks ago";
        return day.ToString("d MMMM yyyy");
    }

    private void ShowCompletionNotice()
    {
        UpdateCompletionNotice? notice = UpdateService.ConsumeCompletionNotice();
        if (notice is null) return;
        AppLog.Info("Update " + notice.Version + " applied successfully.");
        NotificationService.ShowUpdateApplied(notice);
        SettingsButton_Click(this, new RoutedEventArgs());
        UpdateStatusText.Text = $"GoPing was successfully updated to {notice.Version}. Release notes are shown below.";
        UpdateNotesHeader.Visibility = Visibility.Visible;
        UpdateNotesBox.Visibility = Visibility.Visible;
        UpdateNotesBox.Text = string.IsNullOrWhiteSpace(notice.Notes)
            ? $"GoPing {notice.Version} installed."
            : notice.Notes;
    }

    private void ApplyFeatureSettingsToUi()
    {
        _suppressSettingsSave = true;
        try
        {
            if (TypingExperimentalCheckBox is not null)
                TypingExperimentalCheckBox.IsChecked = _appSettings.ExperimentalTyping;
            if (NativeNotificationsCheckBox is not null)
                NativeNotificationsCheckBox.IsChecked = _appSettings.NativeNotifications;
            if (MaxTextLengthBox is not null) MaxTextLengthBox.Text = _appSettings.MaxTextLength.ToString();
            if (MaxVoiceSecondsBox is not null) MaxVoiceSecondsBox.Text = _appSettings.MaxVoiceSeconds.ToString();
            if (MaxCallMinutesBox is not null) MaxCallMinutesBox.Text = _appSettings.MaxCallMinutes.ToString();
            if (MaxFileBytesBox is not null) MaxFileBytesBox.Text = _appSettings.MaxFileBytes.ToString();
            RefreshServersUi();
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void FeatureSettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsSave) return;
        if (TypingExperimentalCheckBox is not null)
            _appSettings.ExperimentalTyping = TypingExperimentalCheckBox.IsChecked == true;
        if (NativeNotificationsCheckBox is not null)
            _appSettings.NativeNotifications = NativeNotificationsCheckBox.IsChecked == true;
        AppSettingsStore.Save(_appSettings);
    }

    private void RefreshServersUi()
    {
        if (ServersListBox is null) return;
        ServersListBox.ItemsSource = null;
        ServersListBox.ItemsSource = _appSettings.Servers.Select(s => $"{s.Name}  {s.Url}").ToList();
        if (!string.IsNullOrWhiteSpace(_appSettings.ActiveServerId))
        {
            var active = _appSettings.Servers.FirstOrDefault(s => s.Id == _appSettings.ActiveServerId);
            if (ActiveServerText is not null)
                ActiveServerText.Text = active is null ? "Active server: default" : $"Active server: {active.Name}";
        }
        else if (ActiveServerText is not null)
        {
            ActiveServerText.Text = "Active server: default. Add a friend's server to switch.";
        }
    }

    private void ApplyActiveServer()
    {
        var active = _appSettings.Servers.FirstOrDefault(s => s.Id == _appSettings.ActiveServerId);
        if (active is null || string.IsNullOrWhiteSpace(active.Url)) return;
        try
        {
            _api.SetBaseUrl(active.Url.Trim());
            AppLog.Info("Using server " + active.Name + " at " + active.Url);
            _realtime.Reconnect();
            _ = RefreshChatsAsync(selectFirst: false);
            _ = LoadLimitsFromServerAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Server address was rejected: " + ex.Message);
        }
    }

    private void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NewServerNameBox?.Text.Trim() ?? "";
        string url = NewServerUrlBox?.Text.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            if (LimitsHintText is not null) LimitsHintText.Text = "Enter a valid http or https server URL.";
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) name = uri.Host;
        string id = Guid.NewGuid().ToString("N");
        var entry = new ServerEntry(id, name, url.TrimEnd('/'));
        _appSettings.Servers.Add(entry);
        _appSettings.ActiveServerId = id;
        AppSettingsStore.Save(_appSettings);
        if (NewServerNameBox is not null) NewServerNameBox.Text = "";
        if (NewServerUrlBox is not null) NewServerUrlBox.Text = "";
        RefreshServersUi();
        ApplyActiveServer();
        AppLog.Info("Server added: " + entry.Name);
    }

    private void RemoveServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServersListBox?.SelectedIndex is not int index || index < 0 || index >= _appSettings.Servers.Count) return;
        string id = _appSettings.Servers[index].Id;
        _appSettings.Servers.RemoveAt(index);
        if (_appSettings.ActiveServerId == id) _appSettings.ActiveServerId = "";
        AppSettingsStore.Save(_appSettings);
        RefreshServersUi();
        ApplyActiveServer();
    }

    private void ResetServersButton_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.Servers.Clear();
        _appSettings.ActiveServerId = "";
        AppSettingsStore.Save(_appSettings);
        RefreshServersUi();
        ApplyActiveServer();
        AppLog.Info("Server list reset.");
    }

    private void ServersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsSave || ServersListBox?.SelectedIndex is not int index) return;
        if (index < 0 || index >= _appSettings.Servers.Count) return;
        _appSettings.ActiveServerId = _appSettings.Servers[index].Id;
        AppSettingsStore.Save(_appSettings);
        RefreshServersUi();
        ApplyActiveServer();
    }

    private async Task LoadLimitsFromServerAsync()
    {
        try
        {
            LimitsConfig limits = await _api.GetLimitsAsync();
            _appSettings.MaxTextLength = limits.MaxTextLength;
            _appSettings.MaxVoiceSeconds = limits.MaxVoiceSeconds;
            _appSettings.MaxCallMinutes = limits.MaxCallMinutes;
            _appSettings.MaxFileBytes = limits.MaxFileBytes;
            AppSettingsStore.Save(_appSettings);
            _suppressSettingsSave = true;
            if (MaxTextLengthBox is not null) MaxTextLengthBox.Text = limits.MaxTextLength.ToString();
            if (MaxVoiceSecondsBox is not null) MaxVoiceSecondsBox.Text = limits.MaxVoiceSeconds.ToString();
            if (MaxCallMinutesBox is not null) MaxCallMinutesBox.Text = limits.MaxCallMinutes.ToString();
            if (MaxFileBytesBox is not null) MaxFileBytesBox.Text = limits.MaxFileBytes.ToString();
            _suppressSettingsSave = false;
            if (LimitsHintText is not null) LimitsHintText.Text = "Limits are synced from the server.";
        }
        catch
        {
            if (LimitsHintText is not null) LimitsHintText.Text = "Server limits are unavailable. Local values are used.";
        }
    }

    private void SaveLimitsButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MaxTextLengthBox?.Text, out int text)) _appSettings.MaxTextLength = Math.Clamp(text, 100, 100000);
        if (int.TryParse(MaxVoiceSecondsBox?.Text, out int voice)) _appSettings.MaxVoiceSeconds = Math.Clamp(voice, 1, 3600);
        if (int.TryParse(MaxCallMinutesBox?.Text, out int call)) _appSettings.MaxCallMinutes = Math.Max(0, call);
        if (long.TryParse(MaxFileBytesBox?.Text, out long file)) _appSettings.MaxFileBytes = Math.Clamp(file, 1024, 1024L * 1024 * 1024);
        AppSettingsStore.Save(_appSettings);
        ApplyFeatureSettingsToUi();
        if (LimitsHintText is not null) LimitsHintText.Text = "Limits saved locally. Server values may override them on reconnect.";
    }

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        long bytes = CacheService.EstimateSize();
        long removed = CacheService.Clear();
        if (CacheSizeText is not null)
            CacheSizeText.Text = removed > 0 || bytes > 0
                ? $"About {bytes / (1024.0 * 1024.0):F1} MB will be removed on restart."
                : "Cache is already empty.";
        AppLog.Info("Cache cleanup scheduled.");
    }

    private void UpdateCacheSizeText()
    {
        if (CacheSizeText is null) return;
        long bytes = CacheService.EstimateSize();
        CacheSizeText.Text = bytes > 0 ? $"WebView2 cache: {bytes / (1024.0 * 1024.0):F1} MB" : "WebView2 cache: empty";
    }

    private void OpenAdminPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminPanel is null) return;
        AdminPanel.Visibility = Visibility.Visible;
        FadeIn(AdminPanel);
    }

    private void AdminCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminPanel is not null) AdminPanel.Visibility = Visibility.Collapsed;
    }

    private async void AdminLoadLogsButton_Click(object sender, RoutedEventArgs e)
    {
        string secret = AdminSecretBox?.Password ?? "";
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (AdminStatusText is not null) AdminStatusText.Text = "Enter the admin secret first.";
            return;
        }
        try
        {
            IReadOnlyList<string> lines = await _api.GetAdminLogsAsync(secret, 200);
            if (AdminLogsList is not null) AdminLogsList.ItemsSource = lines;
            if (AdminStatusText is not null) AdminStatusText.Text = $"Loaded {lines.Count} log lines.";
        }
        catch (Exception ex)
        {
            if (AdminStatusText is not null) AdminStatusText.Text = "Could not load logs: " + ex.Message;
        }
    }

    private async void AdminIssueTokenButton_Click(object sender, RoutedEventArgs e)
    {
        string secret = AdminSecretBox?.Password ?? "";
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (AdminStatusText is not null) AdminStatusText.Text = "Enter the admin secret first.";
            return;
        }
        try
        {
            string token = await _api.IssueAdminTokenAsync(secret, "Issued from GoPing client");
            if (AdminTokenText is not null) AdminTokenText.Text = token;
            if (AdminStatusText is not null) AdminStatusText.Text = "Token issued. Copy it into the sign-in window.";
        }
        catch (Exception ex)
        {
            if (AdminStatusText is not null) AdminStatusText.Text = "Could not issue token: " + ex.Message;
        }
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach a photo, video or file",
            Filter = "Images and videos|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.mp4;*.webm;*.mov|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        string ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        string kind = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }.Contains(ext) ? "photo"
            : new[] { ".mp4", ".webm", ".mov" }.Contains(ext) ? "video" : "file";
        if (string.IsNullOrWhiteSpace(_selectedChatId))
        {
            SetChatStatus("Select a chat first.", true);
            return;
        }
        _ = UploadSelectedFileAsync(dialog.FileName, kind);
    }

    private async Task UploadSelectedFileAsync(string path, string kind)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > _appSettings.MaxFileBytes)
            {
                SetChatStatus($"File is too large. Limit is {_appSettings.MaxFileBytes / (1024 * 1024)} MB.", true);
                return;
            }
            SetChatStatus("Uploading " + Path.GetFileName(path) + "…", false);
            var upload = new MediaUploadService(_api);
            var progress = new Progress<double>(p => SetChatStatus($"Uploading {Path.GetFileName(path)}… {p:F0}%", false));
            await upload.UploadAsync(_selectedChatId!, path, kind, _appSettings.MaxFileBytes, progress);
            SetChatStatus("File sent.", false);
            await LoadMessagesAsync();
        }
        catch (Exception ex)
        {
            SetChatStatus("Upload failed: " + ex.Message, true);
            AppLog.Error("Media upload failed: " + ex.Message);
        }
    }

    private void PollButton_Click(object sender, RoutedEventArgs e)
    {
        if (PollComposerPanel is null) return;
        PollComposerPanel.Visibility = PollComposerPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (PollComposerPanel.Visibility == Visibility.Visible)
            PollQuestionBox?.Focus();
    }

    private void PollCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (PollComposerPanel is not null) PollComposerPanel.Visibility = Visibility.Collapsed;
        if (PollQuestionBox is not null) PollQuestionBox.Text = "";
        if (PollOptionsBox is not null) PollOptionsBox.Text = "";
    }

    private async void PollCreateButton_Click(object sender, RoutedEventArgs e)
    {
        string question = PollQuestionBox?.Text.Trim() ?? "";
        var options = (PollOptionsBox?.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Trim()).Where(o => o.Length > 0).Take(10).ToList();
        if (string.IsNullOrWhiteSpace(question) || options.Count < 2)
        {
            SetChatStatus("A poll needs a question and at least two options.", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_selectedChatId)) return;
        try
        {
            await _api.CreatePollAsync(_selectedChatId, question, options);
            PollCancelButton_Click(sender, e);
            await LoadMessagesAsync();
        }
        catch (Exception ex)
        {
            SetChatStatus("Poll was not created: " + ex.Message, true);
        }
    }

    internal UIElement BuildPollContent(ChatMessage message)
    {
        ParsedPoll? parsed = message.ParsePoll();
        var panel = new StackPanel { MinWidth = 220 };
        if (parsed is null)
        {
            panel.Children.Add(new TextBlock { Text = "Poll", FontSize = 13 });
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = parsed.Question,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        int total = parsed.PollOptions.Sum(o => o.Votes);
        foreach (PollOption option in parsed.PollOptions)
        {
            var optionButton = new Button
            {
                Content = total > 0
                    ? $"{option.Text}   {option.Votes} ({(option.Votes * 100.0 / total):F0}%)"
                    : option.Text,
                Margin = new Thickness(0, 0, 0, 6),
                Height = 30,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            if (Application.Current?.TryFindResource("SecondaryButton") is Style style)
                optionButton.Style = style;
            string pollId = parsed.PollId;
            string optionId = option.Id;
            optionButton.Click += (_, _) => _ = VotePollAsync(message.ChatId, pollId, optionId);
            panel.Children.Add(optionButton);
        }

        if (total > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{total} vote{(total == 1 ? "" : "s")}",
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brushes.Gray
            });
        }
        return panel;
    }

    private async Task VotePollAsync(string chatId, string pollId, string optionId)
    {
        try
        {
            await _api.VotePollAsync(chatId, pollId, optionId);
            await LoadMessagesAsync();
        }
        catch (Exception ex)
        {
            SetChatStatus("Vote failed: " + ex.Message, true);
        }
    }

    internal UIElement BuildMediaContent(ChatMessage message)
    {
        MediaPayload? media = message.ParseMedia();
        var panel = new StackPanel { MinWidth = 200 };
        if (media is null)
        {
            panel.Children.Add(new TextBlock { Text = "Attachment", FontSize = 13 });
            return panel;
        }

        string glyph = media.Kind == "video" ? "\uE714" : media.Kind == "photo" ? "\uE91B" : "\uE8E5";
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph + "   " + media.FileName,
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = media.Kind.ToUpperInvariant()
                            + (media.SizeBytes > 0 ? "  " + (media.SizeBytes / (1024.0 * 1024.0)).ToString("F1") + " MB" : ""),
                        FontSize = 10,
                        Margin = new Thickness(0, 4, 0, 0),
                        Foreground = Brushes.Gray
                    }
                }
            }
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.MouseLeftButtonUp += (_, _) => OpenMediaPreview(media.Url, media.FileName, media.Kind);
        panel.Children.Add(card);
        return panel;
    }

    internal void OpenMediaPreview(string url, string fileName, string kind)
    {
        if (MediaPreviewPopup is null) return;
        _activeMediaPath = url.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)
            ? _api.BaseUrl.TrimEnd('/') + url : url;
        _activeMedia = new MediaPayload(_activeMediaPath, fileName, 0, "", kind);
        if (MediaFileNameText is not null) MediaFileNameText.Text = fileName;
        if (MediaPreviewImage is not null && MediaPreviewVideo is not null)
        {
            bool isVideo = kind == "video";
            MediaPreviewImage.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
            MediaPreviewVideo.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                if (isVideo)
                {
                    MediaPreviewVideo.Source = new Uri(_activeMediaPath);
                    MediaPreviewVideo.Play();
                }
                else
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(_activeMediaPath);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    MediaPreviewImage.Source = image;
                }
            }
            catch (Exception ex)
            {
                SetChatStatus("Could not open media: " + ex.Message, true);
            }
        }
        MediaPreviewPopup.IsOpen = true;
    }

    private void MediaCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (MediaPreviewPopup is null) return;
        if (MediaPreviewVideo is not null)
        {
            MediaPreviewVideo.Stop();
            MediaPreviewVideo.Source = null;
        }
        MediaPreviewPopup.IsOpen = false;
        _activeMedia = null;
    }

    private async void MediaSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMedia is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save media",
            FileName = Path.GetFileName(_activeMedia.FileName),
            Filter = "All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var client = new HttpClient();
            byte[] bytes = await client.GetByteArrayAsync(_activeMedia.Url);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            if (MediaFileNameText is not null) MediaFileNameText.Text = "Saved to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            SetChatStatus("Save failed: " + ex.Message, true);
        }
    }

    internal void IncrementUnread(string chatId)
    {
        if (string.Equals(chatId, _selectedChatId, StringComparison.OrdinalIgnoreCase)) return;
        _unreadCounts[chatId] = _unreadCounts.TryGetValue(chatId, out int n) ? n + 1 : 1;
        ApplyUnreadBadges();
    }

    internal void ClearUnread(string chatId)
    {
        if (_unreadCounts.Remove(chatId)) ApplyUnreadBadges();
    }

    private void ApplyUnreadBadges()
    {
        for (int i = 0; i < _chatItems.Count; i++)
        {
            ChatSummary chat = _chatItems[i];
            int count = _unreadCounts.TryGetValue(chat.Id, out int n) ? n : 0;
            if (chat.UnreadCount != count) _chatItems[i] = chat with { UnreadCount = count };
        }
    }

    private void NotifyTyping()
    {
        if (!_appSettings.ExperimentalTyping || string.IsNullOrWhiteSpace(_selectedChatId)) return;
        _typingChatId = _selectedChatId;
        _typingTimer.Stop();
        _typingTimer.Start();
        _ = _api.SendTypingAsync(_selectedChatId);
    }

    internal void ShowPeerTyping(string chatId)
    {
        if (!_appSettings.ExperimentalTyping) return;
        if (!string.Equals(chatId, _selectedChatId, StringComparison.OrdinalIgnoreCase)) return;
        if (ChatStatusText is not null)
        {
            ChatStatusText.Text = "typing…";
            ChatStatusText.Opacity = 1;
        }
        _ = RestoreStatusAfterTypingAsync(chatId);
    }

    private async Task RestoreStatusAfterTypingAsync(string chatId)
    {
        await Task.Delay(2200);
        if (string.Equals(chatId, _selectedChatId, StringComparison.OrdinalIgnoreCase))
            UpdatePresenceStatus();
    }

    internal void ShowNativeMessageNotification(string title, string body, string? chatId)
    {
        if (!_appSettings.NativeNotifications) return;
        NotificationService.ShowMessage(title, body, chatId is null ? null
            : new NotificationAction("Open", () => Dispatcher.Invoke(() => _ = OpenChatFromNotificationAsync(chatId))));
    }

    internal bool IsVoiceListened(string messageId) => _listenedVoices.Contains(messageId);

    internal void MarkVoiceListened(string messageId)
    {
        if (_listenedVoices.Add(messageId))
            AppLog.Debug("Voice message listened: " + messageId);
    }

    internal double GetVoiceSpeed(string messageId)
        => _voiceSpeeds.TryGetValue(messageId, out double speed) ? speed : 1.0;

    internal double CycleVoiceSpeed(string messageId)
    {
        double current = GetVoiceSpeed(messageId);
        double next = current switch { 1.0 => 1.5, 1.5 => 2.0, 2.0 => 4.0, _ => 1.0 };
        _voiceSpeeds[messageId] = next;
        return next;
    }

    internal void ApplyLimitsFromRealtime(LimitsConfig limits)
    {
        _appSettings.MaxTextLength = limits.MaxTextLength;
        _appSettings.MaxVoiceSeconds = limits.MaxVoiceSeconds;
        _appSettings.MaxCallMinutes = limits.MaxCallMinutes;
        _appSettings.MaxFileBytes = limits.MaxFileBytes;
        AppSettingsStore.Save(_appSettings);
        ApplyFeatureSettingsToUi();
        AppLog.Info("Server limits applied in real time.");
    }
}
