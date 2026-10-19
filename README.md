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
  Layout/ClusterGeometry.cs          Geometría pura: imán, empuje, límites y celdas libres (con tests)
  Layout/ClusterLayoutManager.cs     Aplica la geometría a las ventanas (WM_WINDOWPOSCHANGING + animación)
  Desktop/DesktopIconAvoider.cs      Aparta los íconos del escritorio que quedan bajo un Cluster
  Explorer/                          Prototipo: Cluster con la vista nativa del Explorador (IExplorerBrowser)
  MainWindow.xaml                    Banco de pruebas temporal (el Manager llegará después)
tests/WindowsCluster.Tests/        Pruebas unitarias de la geometría (xUnit)
rea/                           REA (submódulo) para el análisis clean-room
```

## Reglas de UI/UX

- Íconos siempre a 256×256 vía `IShellItemImageFactory`; nunca `SHGetFileInfo`/32×32.
- Sin menús Win32 (`TrackPopupMenu`): todo menú es WPF, con tema claro/oscuro dinámico.
- Los Clusters referencian los archivos reales; no se copian, mueven ni ocultan íconos de Explorer.

## Colocación de los Clusters

- **Imán:** los bordes se alinean con otros Clusters (con 8 px de hueco limpio) y con el borde de la pantalla.
- **Empuje:** al arrastrar o redimensionar sobre otro Cluster, este se aparta con una animación y empuja a su vez a los suyos; si no hay sitio, cede el que arrastras. Nunca quedan encimados.
- **Íconos del escritorio:** al terminar de mover o redimensionar, los íconos sueltos que queden bajo el Cluster pasan a la celda libre más cercana (no con «Organizar íconos automáticamente»).

## Compilar y ejecutar

```bash
dotnet build WindowsCluster.sln
dotnet run --project src/WindowsCluster.App
dotnet test tests/WindowsCluster.Tests
```

Clonar con el submódulo de REA:

```bash
git clone --recurse-submodules https://github.com/eliestudioteam/WindowsCluster.git
```
