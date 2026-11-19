using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace ChengyuDock;

internal sealed class TodoEditorWindow : Window
{
    readonly TextBox _text = new() { MaxLength = 300, MinHeight = 47, VerticalContentAlignment = VerticalAlignment.Center };
    readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 152, MinHeight = 31 };
    readonly TextBlock _feedback = Ui.Text("", 12, "#F3AE9C");

    public TodoEditorWindow()
    {
        Title = "添加待办";
        Width = 426;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#141E29");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CornerRadius = new CornerRadius(17), CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });

        AutomationProperties.SetName(_text, "待办内容");
        AutomationProperties.SetName(_date, "计划日期");
        var heading = Ui.Stack(Ui.Text("添加待办", 23), Ui.Text("写下接下来要做的事", 12, "#A3B4C7"));
        heading.Margin = new Thickness(0, 0, 0, 21);
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var fieldLabel = Ui.Text("内容", 12, "#B5C5D6");
        fieldLabel.Margin = new Thickness(0, 0, 0, 7);
        var cancel = Ui.Button("取消", Close);
        var add = Ui.Button("添加待办", Save);
        add.IsDefault = true;
        add.Background = Ui.Brush("#205771");
        add.BorderBrush = Ui.Brush("#498BA4");
        var buttons = Ui.Actions(cancel, add);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        _feedback.Margin = new Thickness(0, 0, 0, 8);
        _feedback.Visibility = Visibility.Collapsed;
        _date.DateValidationError += (_, e) => { e.ThrowException = false; ShowFeedback("请输入有效日期"); };
        var panel = Ui.Stack(heading, fieldLabel, _text, Ui.Row("计划日期", _date), _feedback, buttons);
        panel.Margin = new Thickness(23, 19, 23, 20);
        Content = new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(24, 39, 54), Color.FromRgb(16, 23, 33), 65),
            BorderBrush = Ui.Brush("#435568"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(17), Child = panel
        };
        Loaded += (_, _) =>
        {
            _date.ApplyTemplate();
            _date.BorderBrush = Ui.Brush("#3A4E60");
            if (_date.Template.FindName("PART_TextBox", _date) is TextBox dateText)
            {
                dateText.Background = Ui.Brush("#1C2B38");
                dateText.Foreground = Ui.Brush("#EDF5FC");
                dateText.CaretBrush = Brushes.White;
                dateText.BorderBrush = Ui.Brush("#3A4E60");
                dateText.VerticalContentAlignment = VerticalAlignment.Center;
            }
            if (_date.Template.FindName("PART_Button", _date) is Button dateButton)
            {
                dateButton.Style = (Style)FindResource(typeof(Button));
                dateButton.Content = ConsoleIcons.Draw("calendar", 15);
                dateButton.Width = 27;
                dateButton.Padding = new Thickness(0);
            }
            _text.Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_date.IsDropDownOpen) { e.Handled = true; Close(); } };
    }

    void ShowFeedback(string message)
    {
        _feedback.Text = message;
        _feedback.Visibility = Visibility.Visible;
    }

    void Save()
    {
        string text = _text.Text.Trim();
        if (text.Length == 0) { ShowFeedback("请输入待办内容"); _text.Focus(); return; }
        if (!DateTime.TryParse(_date.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime date))
        { ShowFeedback("请输入有效日期"); _date.Focus(); return; }
        if (App.Current.Prefs.Todos.Count >= 500) { ShowFeedback("待办已满，请先移除旧项目"); return; }

        string day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (App.Current.Apply(p => p.Todos.Add(new TodoItem { Text = text, Date = day }))) Close();
        else ShowFeedback("保存失败，请稍后重试");
    }
}
