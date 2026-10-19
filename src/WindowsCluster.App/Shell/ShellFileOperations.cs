using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WindowsCluster.App.Interop;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Moves or copies files with IFileOperation, Explorer's own engine: native progress and
/// conflict dialogs, Recycle Bin–safe, and undoable with Ctrl+Z in Explorer.
/// </summary>
public static class ShellFileOperations
{
    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void _Advise();
        void _Unadvise();
        void SetOperationFlags(uint dwOperationFlags);
        void _SetProgressMessage();
        void _SetProgressDialog();
        void _SetProperties();
        void SetOwnerWindow(IntPtr hwndOwner);
        void _ApplyPropertiesToItem();
        void _ApplyPropertiesToItems();
        void _RenameItem();
        void _RenameItems();
        void MoveItem([MarshalAs(UnmanagedType.IUnknown)] object psiItem,
            [MarshalAs(UnmanagedType.IUnknown)] object psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
        void _MoveItems();
        void CopyItem([MarshalAs(UnmanagedType.IUnknown)] object psiItem,
            [MarshalAs(UnmanagedType.IUnknown)] object psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
        void _CopyItems();
        void DeleteItem([MarshalAs(UnmanagedType.IUnknown)] object psiItem, IntPtr pfopsItem);
        void _DeleteItems();
        void _NewItem();
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    /// <summary>
    /// Moves (or copies) <paramref name="sources"/> into <paramref name="destinationFolder"/>
    /// and returns the paths that exist there afterwards.
    /// </summary>
    public static IReadOnlyList<string> Transfer(IEnumerable<string> sources, string destinationFolder, bool copy, IntPtr owner)
    {
        var list = sources.ToList();
        if (list.Count == 0)
            return [];

        object? operation = null;
        var items = new List<object>();
        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation)!)!;
            var op = (IFileOperation)operation;
            op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOFX_ADDUNDORECORD);
            op.SetOwnerWindow(owner);

            var destination = CreateItem(destinationFolder);
            items.Add(destination);
            foreach (var source in list)
            {
                var item = CreateItem(source);
                items.Add(item);
                if (copy)
                    op.CopyItem(item, destination, null, IntPtr.Zero);
                else
                    op.MoveItem(item, destination, null, IntPtr.Zero);
            }
            op.PerformOperations();
        }
        catch (COMException ex)
        {
            // Cancelling the native dialog also lands here (ERROR_CANCELLED).
            Debug.WriteLine($"[ShellFileOperations] {ex.Message}");
        }
        finally
        {
            foreach (var item in items)
                Marshal.ReleaseComObject(item);
            if (operation is not null)
                Marshal.ReleaseComObject(operation);
        }

        return list
            .Select(s => Path.Combine(destinationFolder, Path.GetFileName(s.TrimEnd('\\'))))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
    }

    /// <summary>
    /// Sends items to the Recycle Bin, exactly like pressing Delete on the desktop
    /// (with Explorer's confirmation and undo). Never deletes permanently.
    /// </summary>
    public static void Recycle(IEnumerable<string> paths, IntPtr owner)
    {
        object? operation = null;
        var items = new List<object>();
        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation)!)!;
            var op = (IFileOperation)operation;
            op.SetOperationFlags(FOF_ALLOWUNDO | FOFX_ADDUNDORECORD | FOFX_RECYCLEONDELETE);
            op.SetOwnerWindow(owner);
            foreach (var path in paths)
            {
                var item = CreateItem(path);
                items.Add(item);
                op.DeleteItem(item, IntPtr.Zero);
            }
            if (items.Count > 0)
                op.PerformOperations();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[ShellFileOperations] Recycle: {ex.Message}");
        }
        finally
        {
            foreach (var item in items)
                Marshal.ReleaseComObject(item);
            if (operation is not null)
                Marshal.ReleaseComObject(operation);
        }
    }

    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    private static object CreateItem(string path)
    {
        var iid = IID_IShellItem;
        ShellInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return item;
    }
}
