using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClipShelf;

public sealed class AppSettings
{
    public bool HistoryEnabled { get; set; } = true;
    public bool WatchScreenshots { get; set; } = true;
    public string ScreenshotFolder { get; set; } = DefaultScreenshotFolder();
    public string GlobalHotKey { get; set; } = "Ctrl+Shift+V";
    public string ClearSelectionHotKey { get; set; } = "Ctrl+Shift+A";
    public string PinHotKey { get; set; } = "Ctrl+Shift+P";
    public string Theme { get; set; } = "System";
    public string SelectionPreset { get; set; } = "coolGrayBlue";
    public string SelectionColor { get; set; } = "#2870DB";
    public int AppIcon { get; set; } = 2;
    public string ClickBehavior { get; set; } = "KeepSelection";
    public bool DeselectOnRepeatedClick { get; set; }
    public bool SwitchToClickedRecord { get; set; }
    public string MultiSelectedClick { get; set; } = "Collapse";
    public string MultiUnselectedClick { get; set; } = "Collapse";
    public int ClickRecoveryMilliseconds { get; set; } = 600;
    public int MaxItems { get; set; } = 100;
    public bool LaunchAtLogin { get; set; }
    public bool CloseToTray { get; set; } = true;
    public double WindowWidth { get; set; } = 720;
    public double WindowHeight { get; set; } = 540;

    private static string DefaultScreenshotFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            // Resolves OneDrive or other user redirection without creating a folder.
            var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
            var address = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out address) == 0 && address != IntPtr.Zero)
                {
                    var value = Marshal.PtrToStringUni(address);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally { if (address != IntPtr.Zero) Marshal.FreeCoTaskMem(address); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
