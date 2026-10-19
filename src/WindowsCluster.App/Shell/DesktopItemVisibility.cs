using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsCluster.App.Shell;

public enum HideMethod
{
    /// <summary>The item could not be hidden (e.g. a Public Desktop file without admin rights).</summary>
    None,

    /// <summary>FILE_ATTRIBUTE_HIDDEN on the real file or folder; the file never moves.</summary>
    FileAttribute,

    /// <summary>HideDesktopIcons registry value for shell items such as the Recycle Bin.</summary>
    DesktopIconsPolicy,
}

/// <summary>How an item was hidden from Explorer's desktop grid, so it can be restored exactly.</summary>
/// <param name="WasAlreadyHidden">The user had hidden it before; restoring must leave it hidden.</param>
public sealed record HiddenState(HideMethod Method, bool WasAlreadyHidden)
{
    public static HiddenState NotHidden { get; } = new(HideMethod.None, false);
}

/// <summary>
/// Removes items from Explorer's desktop grid while they live in a Cluster, and puts them back.
/// Files are never moved, copied or deleted: only their Hidden attribute changes.
/// </summary>
public static class DesktopItemVisibility
{
    private const string HideIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const int SHCNE_ATTRIBUTES = 0x00000800;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;
    private const int SHCNE_UPDATEITEM = 0x00002000;
    // Deliver the notification synchronously: Explorer drops the icon from its grid before
    // the call returns, instead of after its usual file-system batching delay (~1 s).
    private const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string? dwItem1, IntPtr dwItem2);

    /// <summary>
    /// True when Explorer is set to "Show hidden items": hidden files then still appear
    /// (dimmed) on the desktop, so grouped icons would look duplicated.
    /// </summary>
    public static bool ExplorerShowsHiddenItems
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue("Hidden") is int value && value == 1;
        }
    }

    /// <summary>True for items that sit directly on the desktop (user or Public Desktop, or a shell CLSID).</summary>
    public static bool IsDesktopItem(string parsingName)
    {
        if (IsShellLocation(parsingName))
            return true;
        var parent = Path.GetDirectoryName(parsingName);
        return parent is not null && DesktopFolders.Any(d => string.Equals(d, parent, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> DesktopFolders { get; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    public static HiddenState Hide(string parsingName) =>
        HideMany([parsingName]).TryGetValue(parsingName, out var state) ? state : HiddenState.NotHidden;

    /// <summary>
    /// Hides several items. Public Desktop items (shared by all users) need administrator
    /// rights: they are hidden together through one elevated helper, so the user sees a single
    /// UAC prompt. Items that still cannot be hidden come back as <see cref="HiddenState.NotHidden"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, HiddenState> HideMany(IEnumerable<string> parsingNames)
    {
        var result = new Dictionary<string, HiddenState>(StringComparer.OrdinalIgnoreCase);
        var needsElevation = new List<string>();
        foreach (var name in parsingNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result[name] = IsShellLocation(name) ? HideShellLocation(name) : HideFile(name);
            }
            catch (UnauthorizedAccessException) when (IsPublicDesktopItem(name))
            {
                HiddenLedger.Remove(name);
                needsElevation.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                HiddenLedger.Remove(name);
                Debug.WriteLine($"[Visibility] Hide {name}: {ex.Message}");
                result[name] = HiddenState.NotHidden;
            }
        }

        if (needsElevation.Count > 0)
        {
            var state = new HiddenState(HideMethod.FileAttribute, WasAlreadyHidden: false);
            foreach (var name in needsElevation)
                HiddenLedger.Record(name, state);
            ElevatedAttributes.Run(hide: true, needsElevation);
            foreach (var name in needsElevation)
            {
                var hidden = File.Exists(name) && (File.GetAttributes(name) & FileAttributes.Hidden) != 0;
                result[name] = hidden ? state : HiddenState.NotHidden;
                if (hidden)
                    NotifyChanged(name);
                else
                    HiddenLedger.Remove(name);
            }
        }
        return result;
    }

    /// <summary>Restores several items, elevating once for Public Desktop items.</summary>
    public static void RestoreMany(IEnumerable<(string ParsingName, HiddenState State)> items)
    {
        var needsElevation = new List<string>();
        foreach (var (name, state) in items)
        {
            if (state.WasAlreadyHidden || state.Method == HideMethod.None)
                continue;
            if (!Restore(name, state) && IsPublicDesktopItem(name))
                needsElevation.Add(name);
        }
        if (needsElevation.Count == 0)
            return;
        ElevatedAttributes.Run(hide: false, needsElevation);
        foreach (var name in needsElevation)
        {
            NotifyChanged(name);
            if (!File.Exists(name) || (File.GetAttributes(name) & FileAttributes.Hidden) == 0)
                HiddenLedger.Remove(name);
        }
    }

    public static bool IsPublicDesktopItem(string path) =>
        string.Equals(Path.GetDirectoryName(path), DesktopFolders[1], StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the item to the desktop grid. Never deletes anything.</summary>
    /// <returns>False when the item could not be restored (e.g. missing rights).</returns>
    public static bool Restore(string parsingName, HiddenState state)
    {
        if (state.WasAlreadyHidden || state.Method == HideMethod.None)
            return true;
        try
        {
            switch (state.Method)
            {
                case HideMethod.FileAttribute when File.Exists(parsingName) || Directory.Exists(parsingName):
                    var attributes = File.GetAttributes(parsingName);
                    File.SetAttributes(parsingName, attributes & ~FileAttributes.Hidden);
                    NotifyChanged(parsingName);
                    break;

                case HideMethod.DesktopIconsPolicy:
                    using (var key = Registry.CurrentUser.OpenSubKey(HideIconsKey, writable: true))
                        key?.DeleteValue(ClsidOf(parsingName), throwOnMissingValue: false);
                    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, null, IntPtr.Zero);
                    break;
            }
            HiddenLedger.Remove(parsingName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"[Visibility] Restore {parsingName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns to the desktop every item this app hid that no Cluster references any more,
    /// e.g. after the app was killed between hiding an item and saving its Cluster.
    /// </summary>
    public static void ReleaseOrphans(IEnumerable<string> referenced)
    {
        var inUse = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
        var orphans = HiddenLedger.Entries().Where(e => !inUse.Contains(e.Path)).ToList();
        foreach (var (path, _) in orphans.Where(o => !File.Exists(o.Path) && !Directory.Exists(o.Path) && !IsShellLocation(o.Path)))
            HiddenLedger.Remove(path);
        RestoreMany(orphans.Where(o => File.Exists(o.Path) || Directory.Exists(o.Path) || IsShellLocation(o.Path)));
    }

    private static HiddenState HideFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return HiddenState.NotHidden;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Hidden) != 0)
            return new HiddenState(HideMethod.FileAttribute, WasAlreadyHidden: true);

        var state = new HiddenState(HideMethod.FileAttribute, WasAlreadyHidden: false);
        HiddenLedger.Record(path, state); // written before the change, so a crash cannot strand the item
        File.SetAttributes(path, attributes | FileAttributes.Hidden);
        NotifyChanged(path);
        return state;
    }

    private static HiddenState HideShellLocation(string parsingName)
    {
        var clsid = ClsidOf(parsingName);
        using var key = Registry.CurrentUser.CreateSubKey(HideIconsKey, writable: true);
        if (key.GetValue(clsid) is int current && current == 1)
            return new HiddenState(HideMethod.DesktopIconsPolicy, WasAlreadyHidden: true);

        var state = new HiddenState(HideMethod.DesktopIconsPolicy, WasAlreadyHidden: false);
        HiddenLedger.Record(parsingName, state);
        key.SetValue(clsid, 1, RegistryValueKind.DWord);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, null, IntPtr.Zero);
        return state;
    }

    private static void NotifyChanged(string path)
    {
        SHChangeNotify(SHCNE_ATTRIBUTES, SHCNF_PATHW | SHCNF_FLUSH, path, IntPtr.Zero);
        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, path, IntPtr.Zero);
    }

    private static bool IsShellLocation(string parsingName) =>
        parsingName.StartsWith("::{", StringComparison.Ordinal) && !parsingName.Contains('\\');

    private static string ClsidOf(string parsingName) => parsingName[2..];
}

/// <summary>
/// Write-ahead record of every item Windows Cluster has hidden
/// (%APPDATA%\WindowsCluster\hidden-items.json). It is the source of truth for releasing
/// items, independent of whether the Cluster that held them was saved.
/// </summary>
internal static class HiddenLedger
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsCluster", "hidden-items.json");

    public static void Record(string path, HiddenState state)
    {
        lock (Gate)
        {
            var entries = Load();
            entries[path] = state;
            Save(entries);
        }
    }

    public static void Remove(string path)
    {
        lock (Gate)
        {
            var entries = Load();
            if (entries.Remove(path))
                Save(entries);
        }
    }

    public static IReadOnlyList<(string Path, HiddenState State)> Entries()
    {
        lock (Gate)
            return Load().Select(e => (e.Key, e.Value)).ToList();
    }

    private static Dictionary<string, HiddenState> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, HiddenState>>(File.ReadAllText(FilePath));
                if (data is not null)
                    return new Dictionary<string, HiddenState>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[HiddenLedger] Load: {ex.Message}");
        }
        return new Dictionary<string, HiddenState>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(Dictionary<string, HiddenState> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(entries));
        File.Move(temp, FilePath, overwrite: true);
    }
}
