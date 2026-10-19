using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Shows Explorer's drag image (the icons being dragged) over a WPF drop target.
/// WPF does not do this by itself, so without it the dragged files seem to vanish
/// and only the cursor is visible while they are over a Cluster.
/// </summary>
public sealed class DropTargetHelper
{
    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwndTarget, ComIDataObject pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref POINT ppt, int dwEffect);
        [PreserveSig] int Drop(ComIDataObject pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
    }

    private static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    private readonly IDropTargetHelper? _helper;

    public DropTargetHelper()
    {
        try
        {
            _helper = (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DragDropHelper)!)!;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"[DropTargetHelper] {ex.Message}");
        }
    }

    public void DragEnter(IntPtr hwnd, DragEventArgs e, POINT screen)
    {
        if (_helper is not null && e.Data is ComIDataObject data)
            _helper.DragEnter(hwnd, data, ref screen, (int)e.Effects);
    }

    public void DragOver(DragEventArgs e, POINT screen) => _helper?.DragOver(ref screen, (int)e.Effects);

    public void DragLeave() => _helper?.DragLeave();

    public void Drop(DragEventArgs e, POINT screen)
    {
        if (_helper is not null && e.Data is ComIDataObject data)
            _helper.Drop(data, ref screen, (int)e.Effects);
    }
}
