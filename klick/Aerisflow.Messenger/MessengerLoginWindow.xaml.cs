using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Aerisflow.Messenger;

public partial class MessengerLoginWindow : Window
{
    private static readonly Brush ErrorBrush = CreateFrozenBrush(0xE5, 0x48, 0x4D);
    private static readonly Brush InfoBrush = CreateFrozenBrush(0x6F, 0x6D, 0x7A);
    private bool _isSwitching;

    public MessengerLoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboard.Focus(LoginEmailBox);
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && RegisterPanel.Visibility == Visibility.Visible && !_isSwitching)
        {
            e.Handled = true;
            SwitchToLogin();
        }
    }

    private void LoginInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SignInButton_Click(sender, e);
        }
    }

    private void RegisterInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RegisterSubmitButton_Click(sender, e);
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        FieldState.SetHasText(box, box.Password.Length > 0);
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        string email = LoginEmailBox.Text.Trim();
        string password = LoginPasswordBox.Password;

        if (!email.Contains('@'))
        {
            SetStatus(LoginStatusText, "Please enter a valid email address.", true);
            Keyboard.Focus(LoginEmailBox);
            return;
        }
        if (password.Length < 6)
        {
            SetStatus(LoginStatusText, "Password must be at least 6 characters.", true);
            Keyboard.Focus(LoginPasswordBox);
            return;
        }

        SetStatus(LoginStatusText, string.Empty, false);
    }

    private void CreateAccountButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchToRegister();
    }

    private void BackToSignInButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchToLogin();
    }

    private void RegisterSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        string username = RegUsernameBox.Text.Trim();
        string email = RegEmailBox.Text.Trim();
        string password = RegPasswordBox.Password;
        string confirmPassword = RegConfirmPasswordBox.Password;

        if (username.Length < 3)
        {
            SetStatus(RegisterStatusText, "Username must be at least 3 characters.", true);
            Keyboard.Focus(RegUsernameBox);
            return;
        }
        if (!email.Contains('@'))
        {
            SetStatus(RegisterStatusText, "Please enter a valid email address.", true);
            Keyboard.Focus(RegEmailBox);
            return;
        }
        if (password.Length < 6)
        {
            SetStatus(RegisterStatusText, "Password must be at least 6 characters.", true);
            Keyboard.Focus(RegPasswordBox);
            return;
        }
        if (password != confirmPassword)
        {
            SetStatus(RegisterStatusText, "Passwords do not match.", true);
            Keyboard.Focus(RegConfirmPasswordBox);
            return;
        }

        LoginEmailBox.Text = email;
        RegUsernameBox.Clear();
        RegEmailBox.Clear();
        RegPasswordBox.Clear();
        RegConfirmPasswordBox.Clear();
        SetStatus(RegisterStatusText, string.Empty, false);
        SwitchToLogin();
        SetStatus(LoginStatusText, "Account created. You can sign in now.", false);
    }

    private void SwitchToRegister()
    {
        if (_isSwitching || LoginPanel.Visibility != Visibility.Visible)
            return;
        TitleLabel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        SwitchPanel(LoginPanel, RegisterPanel, 1);
    }

    private void SwitchToLogin()
    {
        if (_isSwitching || RegisterPanel.Visibility != Visibility.Visible)
            return;
        TitleLabel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        SwitchPanel(RegisterPanel, LoginPanel, -1);
    }

    private void SwitchPanel(FrameworkElement from, FrameworkElement to, double direction)
    {
        _isSwitching = true;

        var fromShift = (TranslateTransform)from.RenderTransform;
        var toShift = (TranslateTransform)to.RenderTransform;

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.12));
        var slideOut = new DoubleAnimation(-28 * direction, TimeSpan.FromSeconds(0.12))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            from.Visibility = Visibility.Collapsed;
            from.BeginAnimation(OpacityProperty, null);
            fromShift.BeginAnimation(TranslateTransform.XProperty, null);

            to.Opacity = 0;
            toShift.X = 28 * direction;
            to.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(1, TimeSpan.FromSeconds(0.18));
            var slideIn = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            slideIn.Completed += (_, _) =>
            {
                to.BeginAnimation(OpacityProperty, null);
                toShift.BeginAnimation(TranslateTransform.XProperty, null);
                to.Opacity = 1;
                toShift.X = 0;
                _isSwitching = false;
            };
            to.BeginAnimation(OpacityProperty, fadeIn);
            toShift.BeginAnimation(TranslateTransform.XProperty, slideIn);

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (ReferenceEquals(to, LoginPanel))
                {
                    if (LoginEmailBox.Text.Length > 0)
                        Keyboard.Focus(LoginPasswordBox);
                    else
                        Keyboard.Focus(LoginEmailBox);
                }
                else
                {
                    Keyboard.Focus(RegUsernameBox);
                }
            }));
        };

        from.BeginAnimation(OpacityProperty, fadeOut);
        fromShift.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    private static void SetStatus(TextBlock target, string message, bool isError)
    {
        target.Text = message;
        target.Foreground = isError ? ErrorBrush : InfoBrush;
    }
}

public static class FieldState
{
    public static readonly DependencyProperty HasTextProperty = DependencyProperty.RegisterAttached(
        "HasText", typeof(bool), typeof(FieldState), new PropertyMetadata(false));

    public static bool GetHasText(DependencyObject obj) => (bool)obj.GetValue(HasTextProperty);

    public static void SetHasText(DependencyObject obj, bool value) => obj.SetValue(HasTextProperty, value);
}