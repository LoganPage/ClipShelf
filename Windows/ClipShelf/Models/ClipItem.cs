using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace ClipShelf;

public enum ClipKind { Text, File, Image }

public sealed class ClipItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Text { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public bool IsDirectory { get; set; }
    public string? ImagePath { get; set; }
    public string? SourcePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    private bool isPinned;
    public bool IsPinned
    {
        get => isPinned;
        set { if (isPinned == value) return; isPinned = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonIgnore] public string KindLabel => Kind switch
    {
        ClipKind.File => FilePaths.Count > 1 ? $"{FilePaths.Count} 个文件" : RecordTypeIcon.Label(this),
        ClipKind.Image => string.IsNullOrWhiteSpace(SourcePath) ? "图片" : "截图",
        _ => "文字"
    };

    [JsonIgnore] public string DisplayTitle
    {
        get
        {
            var value = !string.IsNullOrWhiteSpace(Title) ? Title : Kind switch
            {
                ClipKind.Text => Text ?? "文字",
                ClipKind.File => FilePaths.Count == 1 ? Path.GetFileName(FilePaths[0]) : $"{FilePaths.Count} 个文件",
                _ => "剪贴板图片"
            };
            // The full original text remains available for copying and preview.
            return value.Length > 500 ? value[..500] + "…" : value;
        }
    }

    [JsonIgnore] public string Subtitle => $"{(IsPinned ? "已置顶  ·  " : "")}{KindLabel}  ·  {TimeLabel}";
    [JsonIgnore] public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    [JsonIgnore] public string PreviewText => Kind switch
    {
        ClipKind.Text => Text ?? Title,
        ClipKind.File => string.Join(Environment.NewLine, FilePaths),
        _ => SourcePath ?? Title
    };

    public bool Search(string query) => SearchMatcher.Matches(this, query);
}
