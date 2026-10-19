using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WindowsCluster.App.Desktop;
using WindowsCluster.App.Shell;
using WindowsCluster.App.Theming;

namespace WindowsCluster.App.Clusters;

/// <summary>
/// State of one Cluster. Outlives its window so the window can be recreated
/// (for example after Explorer restarts and destroys it).
/// </summary>
public sealed class ClusterViewModel : INotifyPropertyChanged
{
    private string _title = "Nuevo Cluster";
    private Rect _bounds = new(120, 120, 360, 240);
    private EmbedMode _mode = EmbedMode.DesktopLayer;
    private double _transparency = 15;
    private double _brightness;
    private double _saturation;
    private double _hue = 210;
    private double _contrast;

    public ClusterViewModel(Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        if (ThemeService.Current is { } theme)
            theme.ThemeChanged += (_, _) => UpdateAppearance();
        UpdateAppearance();
    }

    public Guid Id { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Cluster" : value.Trim());
    }

    /// <summary>Window bounds in WPF device-independent units.</summary>
    public Rect Bounds
    {
        get => _bounds;
        set => Set(ref _bounds, value);
    }

    public EmbedMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    /// <summary>Items shown in the Cluster: references to real shell items.</summary>
    public ObservableCollection<ClusterItemViewModel> Items { get; } = new();

    /// <summary>When set, <see cref="Items"/> mirrors this real folder.</summary>
    public FolderPortal? Portal { get; set; }

    /// <summary>How the portal's own folder icon was hidden from the desktop.</summary>
    public HiddenState PortalFolderHidden { get; set; } = HiddenState.NotHidden;

    // ---------------------------------------------------------------- appearance

    /// <summary>Background transparency, 0–90 %.</summary>
    public double Transparency
    {
        get => _transparency;
        set { if (Set(ref _transparency, Math.Clamp(value, 0, 90))) UpdateAppearance(); }
    }

    /// <summary>Lightness offset from the theme's base color, −50…+50.</summary>
    public double Brightness
    {
        get => _brightness;
        set { if (Set(ref _brightness, Math.Clamp(value, -50, 50))) UpdateAppearance(); }
    }

    /// <summary>Color saturation, 0–100 %. At 0 the Cluster is neutral gray.</summary>
    public double Saturation
    {
        get => _saturation;
        set { if (Set(ref _saturation, Math.Clamp(value, 0, 100))) UpdateAppearance(); }
    }

    /// <summary>Hue in degrees, 0–360. Only visible when saturation is above 0.</summary>
    public double Hue
    {
        get => _hue;
        set { if (Set(ref _hue, Math.Clamp(value, 0, 360))) UpdateAppearance(); }
    }

    /// <summary>Contrast between body, header and border, −50…+50.</summary>
    public double Contrast
    {
        get => _contrast;
        set { if (Set(ref _contrast, Math.Clamp(value, -50, 50))) UpdateAppearance(); }
    }

    public double BackgroundOpacity => 1 - Transparency / 100;
    public Brush BackgroundBrush { get; private set; } = Brushes.Transparent;
    public Brush HeaderBrush { get; private set; } = Brushes.Transparent;
    public Brush BorderBrush { get; private set; } = Brushes.Transparent;
    public Brush TextBrush { get; private set; } = Brushes.White;
    public Brush HoverBrush { get; private set; } = Brushes.Transparent;

    /// <summary>
    /// Explorer draws desktop labels as light text with a soft dark shadow; Clusters do the
    /// same whenever their text is light, and drop the shadow on light backgrounds.
    /// </summary>
    public Effect? LabelEffect { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Derives the Cluster palette from the sliders. Brightness is relative to the active
    /// Windows theme, so a default Cluster looks right in both light and dark mode.
    /// </summary>
    private void UpdateAppearance()
    {
        var dark = ThemeService.Current?.IsDark ?? true;
        var baseLightness = dark ? 0.12 : 0.94;
        var lightness = Math.Clamp(baseLightness + Brightness / 100, 0.02, 0.98);
        var saturation = Saturation / 100;
        var body = ColorMath.FromHsl(Hue, saturation, lightness);

        var amount = Math.Max(0, 0.07 + Contrast / 100 * 0.14);
        var headerLightness = lightness >= 0.5 ? lightness - amount : lightness + amount;
        var header = ColorMath.FromHsl(Hue, saturation, Math.Clamp(headerLightness, 0, 1));

        var lightText = ColorMath.Luminance(body) < 0.45;
        var text = lightText ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE4, 0, 0, 0);
        var borderAlpha = (byte)Math.Clamp(0x30 + Contrast * 2, 0x08, 0xA0);

        BackgroundBrush = Frozen(body);
        HeaderBrush = Frozen(header);
        BorderBrush = Frozen(Color.FromArgb(borderAlpha, text.R, text.G, text.B));
        TextBrush = Frozen(text);
        HoverBrush = Frozen(Color.FromArgb(0x24, text.R, text.G, text.B));
        LabelEffect = lightText ? LabelShadow : null;

        foreach (var name in new[] { nameof(BackgroundOpacity), nameof(BackgroundBrush), nameof(HeaderBrush),
                     nameof(BorderBrush), nameof(TextBrush), nameof(HoverBrush), nameof(LabelEffect) })
            OnPropertyChanged(name);
    }

    private static readonly DropShadowEffect LabelShadow = CreateLabelShadow();

    private static DropShadowEffect CreateLabelShadow()
    {
        var shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 1,
            BlurRadius = 3,
            Opacity = 0.9,
            Direction = 315,
        };
        shadow.Freeze();
        return shadow;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
