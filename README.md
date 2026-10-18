# WindowsCluster

Organizador de escritorio para Windows (C# / WPF, .NET 8) bajo la marca **Cluster**.

## Estructura

```
WindowsCluster.sln
src/WindowsCluster.App/
  Interop/NativeMethods.cs     P/Invoke de User32 (Progman, WorkerW, SetParent, Z-order)
  Desktop/DesktopHost.cs       Localiza las ventanas del escritorio y engancha los Clusters
  Desktop/DesktopTopology.cs   Instantánea de Progman / SHELLDLL_DefView / WorkerW
  Desktop/EmbedMode.cs         Capa de escritorio (interactiva) o capa de fondo (WorkerW)
  Clusters/ClusterWindow.xaml  Ventana sin bordes, transparente, redimensionable
  Clusters/ClusterService.cs   Crea/recrea Clusters (también tras reiniciar Explorer)
  MainWindow.xaml              Banco de pruebas temporal del módulo DesktopHost
rea/                           REA (submódulo) para el análisis clean-room
```

## Compilar y ejecutar

```bash
dotnet build WindowsCluster.sln
dotnet run --project src/WindowsCluster.App
```

Clonar con el submódulo de REA:

```bash
git clone --recurse-submodules https://github.com/eliestudioteam/WindowsCluster.git
```
