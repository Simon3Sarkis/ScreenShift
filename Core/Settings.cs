using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKey = System.Windows.Input.Key;
using System.Windows.Input;
using ScreenShift.Native;

namespace ScreenShift.Core;

public sealed class Hotkey
{
    public uint Modifiers { get; set; }   // MOD_* flags
    public uint Key { get; set; }         // virtual-key code

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & Win32.MOD_WIN) != 0) sb.Append("Win + ");
        if ((Modifiers & Win32.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
        if ((Modifiers & Win32.MOD_ALT) != 0) sb.Append("Alt + ");
        if ((Modifiers & Win32.MOD_SHIFT) != 0) sb.Append("Shift + ");
        var k = KeyInterop.KeyFromVirtualKey((int)Key);
        sb.Append(k switch
        {
            >= WKey.D0 and <= WKey.D9 => ((char)('0' + (k - WKey.D0))).ToString(),
            >= WKey.NumPad0 and <= WKey.NumPad9 => "Num " + (k - WKey.NumPad0),
            WKey.OemPlus => "=", WKey.OemMinus => "-", WKey.OemComma => ",", WKey.OemPeriod => ".",
            WKey.Oem3 => "`", WKey.OemOpenBrackets => "[", WKey.Oem6 => "]", WKey.Oem5 => "\\",
            WKey.Oem1 => ";", WKey.OemQuotes => "'", WKey.OemQuestion => "/",
            WKey.Prior => "PageUp", WKey.Next => "PageDown",
            _ => k.ToString()
        });
        return sb.ToString();
    }

    public bool SameAs(Hotkey? o) => o != null && o.Modifiers == Modifiers && o.Key == Key;
}

public sealed class AdapterRef
{
    public ulong Luid { get; set; }
    public string DevicePath { get; set; } = "";
}

public sealed class MonitorRef
{
    public ulong AdapterLuid { get; set; }
    public uint TargetId { get; set; }
    public string Name { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double RefreshHz { get; set; }
    public bool HdrSupported { get; set; }
    public bool HdrEnabled { get; set; }
    public bool Primary => X == 0 && Y == 0;
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profile";
    public Hotkey? Hotkey { get; set; }
    public bool RestoreHdr { get; set; } = true;
    public string Paths { get; set; } = "";   // base64 DISPLAYCONFIG_PATH_INFO[]
    public string Modes { get; set; } = "";   // base64 DISPLAYCONFIG_MODE_INFO[]
    public List<AdapterRef> Adapters { get; set; } = [];
    public List<MonitorRef> Monitors { get; set; } = [];
    public DateTime Saved { get; set; } = DateTime.Now;
}

public enum BridgeMode { Proportional = 0, ScaledTop = 1, ScaledCenter = 2, ScaledBottom = 3 }

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<Profile> Profiles { get; set; } = [];

    public bool CursorBridge { get; set; } = true;
    public BridgeMode BridgeMode { get; set; } = BridgeMode.Proportional;
    public Hotkey? BridgeToggleHotkey { get; set; }
    public Hotkey? CycleHotkey { get; set; }
    public Hotkey? EmergencyHotkey { get; set; } = new() { Modifiers = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT, Key = 0x24 /* Home */ };

    public bool ConfirmAfterSwitch { get; set; } = true;
    public int ConfirmSeconds { get; set; } = 15;
    public bool ShowOsd { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string? LastProfileId { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;

public static class SettingsStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenShift");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly string BackupPath = Path.Combine(Dir, "settings.bak.json");
    static readonly object Gate = new();

    public static AppSettings Load()
    {
        foreach (var p in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var bytes = File.ReadAllBytes(p);
                var json = bytes.AsSpan();
                if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];   // tolerate Notepad's UTF-8 BOM
                var s = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings);
                if (s != null) { s.Profiles ??= []; return s; }
            }
            catch (Exception ex) { Log.Write($"Settings load failed ({Path.GetFileName(p)}): {ex.Message}"); }
        }
        return new AppSettings();
    }

    /// <summary>Atomic save: write temp file, keep a backup, then replace. A crash mid-write can never corrupt profiles.</summary>
    public static void Save(AppSettings s)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(s, SettingsJson.Default.AppSettings));
                if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
        }
    }
}

public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(SettingsStore.Dir, "log.txt");

    public static void Write(string msg)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 256 * 1024) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
