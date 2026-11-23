using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GoPing.Services;

public sealed record NotificationAction(string Text, Action OnClick);

/// <summary>
/// Native-style notifications rendered as standalone rounded cards in the
/// bottom-right corner of the Windows work area. They do not steal focus.
/// </summary>
public static class NotificationService
{
    private static readonly List<NotificationWindow> Active = new();

    public static void ShowMessage(string title, string body, NotificationAction? action = null)
        => Show(title, body, "\uE8BD", action);

    public static void ShowCall(string title, string body, NotificationAction? action = null)
        => Show(title, body, "\uE717", action);

    public static void ShowUpdateReady(UpdateInfo info, NotificationAction action)
        => Show("Update ready", $"GoPing {info.Version} is downloaded and waiting for your approval.", "\uE896", action);

    public static void ShowUpdateApplied(UpdateCompletionNotice notice)
        => Show("Update installed", $"GoPing was successfully updated to {notice.Version}.", "\uE8E0", null);

    public static void Show(string title, string body, string glyph, NotificationAction? action = null,
        TimeSpan? lifetime = null)
    {
        void Run()
        {
            var window = new NotificationWindow(title, body, glyph, action, lifetime);
            window.Closed += (_, _) => Active.Remove(window);
            Active.Add(window);
            window.Loaded += (_, _) => Reposition();
            Reposition();
            window.Show();
            window.BeginFadeIn();
        }

        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) Run();
        else app.Dispatcher.BeginInvoke(Run);
    }

    public static void Clear()
    {
        void Run()
        {
            foreach (var window in Active.ToList()) window.Close();
            Active.Clear();
        }
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) Run();
        else app.Dispatcher.BeginInvoke(Run);
    }

    private static void Reposition()
    {
        double bottom = SystemParameters.WorkArea.Bottom - 16;
        double right = SystemParameters.WorkArea.Right - 16;
        foreach (var window in Active.AsEnumerable().Reverse())
        {
            window.UpdateLayout();
            double height = window.ActualHeight > 0 ? window.ActualHeight : 104;
            bottom -= height;
            window.Left = right - window.Width;
            window.Top = Math.Max(SystemParameters.WorkArea.Top + 8, bottom);
            bottom -= 10;
        }
    }

    private sealed class NotificationWindow : Window
    {
        private readonly Border _card;
        private readonly NotificationAction? _action;
        private readonly DispatcherTimer _timer;
        private bool _closing;

        public NotificationWindow(string title, string body, string glyph, NotificationAction? action,
            TimeSpan? lifetime = null)
        {
            _action = action;
            Width = 348;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SnapsToDevicePixels = true;

            var root = new Grid { Margin = new Thickness(8) };
            root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                CornerRadius = new CornerRadius(18),
                Margin = new Thickness(3, 5, 3, 1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = 0.28,
                    Color = Colors.Black
                }
            });

            _card = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 13, 12, 13),
                Background = CreateBrush("SurfaceBrush", Color.FromRgb(250, 250, 250)),
                BorderBrush = CreateBrush("BorderBrush", Color.FromRgb(228, 228, 231)),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(11),
                Background = CreateBrush("AccentSoftBrush", Color.FromRgb(238, 242, 239)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            icon.Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = CreateBrush("AccentBrush", Color.FromRgb(63, 122, 92)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title) ? "GoPing" : title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("TextBrush", Color.FromRgb(24, 24, 27)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var bodyBlock = new TextBlock
            {
                Text = body,
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                MaxHeight = 54,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = CreateBrush("MutedBrush", Color.FromRgb(113, 113, 122))
            };
            text.Children.Add(bodyBlock);
            Grid.SetColumn(text, 1);
            Grid.SetColumnSpan(text, 2);
            grid.Children.Add(text);

            var close = new Button
            {
                Content = "\uE711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = CreateBrush("FaintBrush", Color.FromRgb(161, 161, 170)),
                Cursor = Cursors.Hand
            };
            close.Click += (_, _) => Dismiss();
            Grid.SetColumn(close, 3);
            grid.Children.Add(close);

            if (action is not null)
            {
                var actionButton = new Button
                {
                    Content = action.Text,
                    Height = 26,
                    Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(0, 9, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    FontSize = 11,
                    Foreground = Brushes.White,
                    Background = CreateBrush("AccentBrush", Color.FromRgb(63, 122, 92)),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                if (Application.Current?.TryFindResource("PrimaryButton") is Style buttonStyle)
                    actionButton.Style = buttonStyle;
                actionButton.Click += (_, _) =>
                {
                    try { action.OnClick(); } catch { /* notification action must not crash the app */ }
                    Dismiss();
                };
                Grid.SetColumn(actionButton, 1);
                Grid.SetColumnSpan(actionButton, 2);
                var row = new Grid();
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(grid, 0);
                Grid.SetRow(actionButton, 1);
                row.Children.Add(grid);
                row.Children.Add(actionButton);
                _card.Child = row;
            }
            else
            {
                _card.Child = grid;
            }

            root.Children.Add(_card);
            Content = root;

            _card.MouseLeftButtonUp += (_, _) =>
            {
                if (action is null) return;
                try { action.OnClick(); } catch { /* ignore */ }
                Dismiss();
            };
            _card.Cursor = action is null ? Cursors.Arrow : Cursors.Hand;

            _timer = new DispatcherTimer { Interval = lifetime ?? TimeSpan.FromSeconds(action is null ? 5 : 12) };
            _timer.Tick += (_, _) => Dismiss();
        }

        public void BeginFadeIn()
        {
            Opacity = 0;
            var transform = new TranslateTransform(0, 18);
            RenderTransform = transform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _timer.Start();
        }

        private void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            animation.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, animation);
        }

        private static Brush CreateBrush(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
            return new SolidColorBrush(fallback);
        }
    }
}
