using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClipShelf;

internal sealed class PreviewMediaControl : UserControl, IDisposable
{
    private readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = System.Windows.Media.Stretch.Uniform, Volume = .5 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Slider seek = new() { Minimum = 0, IsEnabled = false, MinWidth = 100, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock status = new() { Text = "点击播放开始预览；不会自动播放", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly string path;
    private bool updating, disposed;
    internal bool IsPlaying { get; private set; }
    internal bool HasSource => media.Source is not null;
    public PreviewMediaControl(string path)
    {
        this.path = path;
        var root = new DockPanel();
        var controls = new DockPanel { Margin = new Thickness(4, 8, 4, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(string text, Action action) { var button = new Button { Content = text, Style = (Style)Application.Current.FindResource("SoftButton"), Margin = new Thickness(3) }; button.Click += (_, _) => action(); buttons.Children.Add(button); }
        Add("播放", Play); Add("暂停", Pause); Add("停止", () => { Pause(); media.Stop(); seek.Value = 0; });
        DockPanel.SetDock(buttons, Dock.Left); controls.Children.Add(buttons);
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = .5, Width = 80, ToolTip = "音量", Margin = new Thickness(8, 0, 4, 0) };
        System.Windows.Automation.AutomationProperties.SetName(volume, "音量");
        System.Windows.Automation.AutomationProperties.SetName(seek, "播放进度");
        volume.ValueChanged += (_, _) => media.Volume = volume.Value;
        DockPanel.SetDock(volume, Dock.Right); controls.Children.Add(volume); controls.Children.Add(seek);
        DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status); root.Children.Add(media); Content = root;
        media.MediaOpened += (_, _) => { if (disposed) return; seek.Maximum = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : 0; seek.IsEnabled = seek.Maximum > 0; };
        media.MediaEnded += (_, _) => { if (disposed) return; Pause(); status.Text = "播放结束"; };
        media.MediaFailed += (_, _) => { if (disposed) return; Pause(); media.Close(); seek.IsEnabled = false; status.Text = "系统无法解码此媒体，可用默认应用打开。"; };
        seek.ValueChanged += (_, _) => { if (!updating && seek.IsEnabled && !disposed) media.Position = TimeSpan.FromSeconds(seek.Value); };
        timer.Tick += (_, _) => { updating = true; seek.Value = media.Position.TotalSeconds; updating = false; status.Text = $"{media.Position:hh\\:mm\\:ss} / {TimeSpan.FromSeconds(seek.Maximum):hh\\:mm\\:ss}"; };
        Unloaded += (_, _) => Dispose();
    }
    internal void Play()
    {
        if (disposed) return;
        media.Source ??= new Uri(path); media.Play(); IsPlaying = true; timer.Start(); status.Text = "正在载入媒体…";
    }
    internal void Pause() { if (disposed) return; media.Pause(); IsPlaying = false; timer.Stop(); }
    public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); IsPlaying = false; media.Close(); media.Source = null; }
}
