# WindowsCluster

Organizador de escritorio para Windows (C# / WPF, .NET 8) bajo la marca **Cluster**.

## Estructura

```
WindowsCluster.sln
src/WindowsCluster.App/
  Interop/NativeMethods.cs           P/Invoke de User32/DWM/GDI (Progman, WorkerW, hook, SendInput)
  Interop/ShellInterop.cs            COM del Shell: IShellWindows, IFolderView, IShellItemImageFactory
  Desktop/DesktopHost.cs             Localiza las ventanas del escritorio y engancha los Clusters
  Desktop/DesktopIconService.cs      Lee y selecciona los íconos reales del escritorio (IFolderView)
  Desktop/DesktopMenuController.cs   Menú tras el arrastre: Cluster, Carpeta real, Cluster con íconos
  Input/MouseHook.cs                 Hook WH_MOUSE_LL
  Input/DesktopDragGesture.cs        Detecta clic derecho + arrastre sobre el escritorio libre
  Shell/ShellIconLoader.cs           Íconos/miniaturas a 256×256 con canal alfa (hilo STA)
  Theming/ThemeService.cs            Sigue el modo claro/oscuro y el color de acento de Windows
  Themes/*.xaml                      Paletas Light/Dark y estilos de controles
  Views/ClusterMenu.xaml             Menú contextual WPF estilo Windows 11
  Views/SelectionOverlay.xaml        Rectángulo de selección durante el arrastre
  Clusters/ClusterWindow.xaml        Ventana sin bordes, transparente, redimensionable
  Clusters/ClusterService.cs         Crea/recrea Clusters (también tras reiniciar Explorer)
  Clusters/FolderPortal.cs           Cluster sincronizado con una carpeta real
  MainWindow.xaml                    Banco de pruebas temporal (el Manager llegará después)
rea/                           REA (submódulo) para el análisis clean-room
```

## Reglas de UI/UX

- Íconos siempre a 256×256 vía `IShellItemImageFactory`; nunca `SHGetFileInfo`/32×32.
- Sin menús Win32 (`TrackPopupMenu`): todo menú es WPF, con tema claro/oscuro dinámico.
- Los Clusters referencian los archivos reales; no se copian, mueven ni ocultan íconos de Explorer.

## Compilar y ejecutar

```bash
dotnet build WindowsCluster.sln
dotnet run --project src/WindowsCluster.App
```

Clonar con el submódulo de REA:

```bash
git clone --recurse-submodules https://github.com/eliestudioteam/WindowsCluster.git
```
