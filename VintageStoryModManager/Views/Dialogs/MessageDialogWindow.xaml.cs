using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VintageStoryModManager.Views.Dialogs;

public partial class MessageDialogWindow : Window
{
    private MessageBoxButton _buttons;
    private bool _resultSet;
    private Action? _extraButtonCallback;

    public MessageDialogWindow()
    {
        InitializeComponent();
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public void Initialize(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageDialogExtraButton? extraButton = null)
    {
        _buttons = buttons;
        Title = caption;
        MessageTextBlock.Text = message;

        ConfigureButtons(buttons);
        ConfigureExtraButton(extraButton);
        ConfigureIcon(icon);
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        ButtonOne.Visibility = Visibility.Collapsed;
        ButtonTwo.Visibility = Visibility.Collapsed;
        ButtonThree.Visibility = Visibility.Collapsed;

        ButtonOne.IsDefault = false;
        ButtonTwo.IsDefault = false;
        ButtonThree.IsDefault = false;
        ButtonExtra.IsDefault = false;

        ButtonOne.IsCancel = false;
        ButtonTwo.IsCancel = false;
        ButtonThree.IsCancel = false;
        ButtonExtra.IsCancel = false;

        switch (buttons)
        {
            case MessageBoxButton.OK:
                ConfigureButton(ButtonOne, "OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                ConfigureButton(ButtonOne, "Cancel", MessageBoxResult.Cancel, isCancel: true);
                ConfigureButton(ButtonTwo, "OK", MessageBoxResult.OK, isDefault: true);
                break;
            case MessageBoxButton.YesNo:
                ConfigureButton(ButtonOne, "Yes", MessageBoxResult.Yes, isDefault: true);
                ConfigureButton(ButtonTwo, "No", MessageBoxResult.No);
                break;
            case MessageBoxButton.YesNoCancel:
                ConfigureButton(ButtonOne, "Cancel", MessageBoxResult.Cancel, isCancel: true);
                ConfigureButton(ButtonTwo, "Yes", MessageBoxResult.Yes, isDefault: true);
                ConfigureButton(ButtonThree, "No", MessageBoxResult.No);
                break;
            default:
                ConfigureButton(ButtonOne, "OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
        }
    }

    private void ConfigureExtraButton(MessageDialogExtraButton? extraButton)
    {
        if (extraButton is null)
        {
            ButtonExtra.Visibility = Visibility.Collapsed;
            ButtonExtra.Tag = null;
            _extraButtonCallback = null;
            return;
        }

        ButtonExtra.Content = extraButton.Content;
        ButtonExtra.Tag = extraButton.Result;
        ButtonExtra.Visibility = Visibility.Visible;
        ButtonExtra.IsDefault = extraButton.IsDefault;
        ButtonExtra.IsCancel = extraButton.IsCancel;
        _extraButtonCallback = extraButton.OnClick;
    }

    private static void ConfigureButton(System.Windows.Controls.Button button, string content, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
    {
        button.Content = content;
        button.Tag = result;
        button.Visibility = Visibility.Visible;
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        ImageSource? source = icon switch
        {
            MessageBoxImage.Error => ConvertIcon(SystemIcons.Error),
            MessageBoxImage.Warning => ConvertIcon(SystemIcons.Warning),
            MessageBoxImage.Information => ConvertIcon(SystemIcons.Information),
            MessageBoxImage.Question => ConvertIcon(SystemIcons.Question),
            _ => null
        };

        if (source == null)
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconImage.Source = source;
            IconImage.Visibility = Visibility.Visible;
        }
    }

    private static ImageSource? ConvertIcon(Icon icon)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(icon.Width, icon.Height));
        source.Freeze();
        return source;
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is MessageBoxResult result)
        {
            if (ReferenceEquals(button, ButtonExtra))
            {
                _extraButtonCallback?.Invoke();
            }

            Result = result;
            _resultSet = true;
            DialogResult = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_resultSet)
        {
            return;
        }

        Result = _buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.None,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };

        _resultSet = true;
    }
}
