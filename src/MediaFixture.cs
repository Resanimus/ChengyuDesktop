using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.Media;
using Windows.Media.Core;

namespace ChengyuDock;
internal static class MediaFixture
{
    internal static void Run(string path)
    {
        var window = new Window { Title = "澄屿 · 系统媒体验证", Width = 380, Height = 180, ShowActivated = false, Background = Ui.Brush("#172335"), Content = new TextBlock { Text = "系统媒体测试\n低音量播放 · 检测 · 暂停", FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var player = new Windows.Media.Playback.MediaPlayer { Volume = .015, IsLoopingEnabled = true };
        player.CommandManager.IsEnabled = true;
        var controls = player.SystemMediaTransportControls; controls.IsEnabled = true; controls.IsPlayEnabled = controls.IsPauseEnabled = true;
        void Metadata() { var updater = controls.DisplayUpdater; updater.Type = MediaPlaybackType.Music; updater.MusicProperties.Title = "澄屿 系统媒体验证"; updater.MusicProperties.Artist = "本地测试信号"; updater.Update(); }
        player.MediaOpened += (_, _) => App.Current.Dispatcher.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(100); Metadata(); });
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        window.Loaded += (_, _) => { player.Play(); Metadata(); };
        window.Closed += (_, _) => { player.Pause(); player.Dispose(); App.Current.Shutdown(); };
        App.Current.MainWindow = window; window.Show();
    }
}
