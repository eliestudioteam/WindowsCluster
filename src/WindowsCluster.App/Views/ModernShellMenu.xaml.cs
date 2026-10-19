using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsCluster.App.Input;
using WindowsCluster.App.Shell;
using WindowsCluster.App.Theming;
using static WindowsCluster.App.Interop.NativeMethods;

namespace WindowsCluster.App.Views;

/// <summary>
/// Context menu for files inside a Cluster, laid out like Windows 11's: Cut/Copy/Rename/
/// Share/Delete on top, the shell's commands below, and "Show more options" opening the
/// classic menu. Windows offers no public API for its own modern menu, so this window
/// presents the commands of the shell's IContextMenu and runs them unchanged.
/// </summary>
public partial class ModernShellMenu : Window
{
    // Canonical verbs shown as icons in the top row, with Segoe Fluent Icons glyphs.
    private static readonly (string Verb, string Glyph)[] TopRowVerbs =
    [
        ("cut", ""),
        ("copy", ""),
        ("rename", ""),
        ("Windows.ModernShare", ""),
        ("delete", ""),
    ];

    // Classic-only commands Windows 11 leaves to "Show more options".
    private static readonly HashSet<string> MoreOptionsOnly = new(StringComparer.OrdinalIgnoreCase) { "link", "PreviousVersions" };

    private readonly ShellContextMenu.Session _session;
    private readonly IReadOnlyList<string> _parsingNames;
    private readonly IntPtr _owner;
    private readonly POINT _anchor;
    private readonly MouseHook _hook;
    private readonly Action _onRename;
    private readonly IntPtr _hwnd;
    private readonly List<Popup> _openPopups = new();
    private bool _closing;

    private ModernShellMenu(ShellContextMenu.Session session, IReadOnlyList<string> parsingNames, IntPtr owner,
        POINT anchor, MouseHook hook, Action onRename)
    {
        InitializeComponent();
        _session = session;
        _parsingNames = parsingNames;
        _owner = owner;
        _anchor = anchor;
        _hook = hook;
        _onRename = onRename;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW));
        ThemeService.Current?.Track(this);

        Build();

        _hook.MouseEvent += OnGlobalMouse;
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _hook.MouseEvent -= OnGlobalMouse;
            foreach (var popup in _openPopups)
                popup.IsOpen = false;
            _session.Dispose();
        };
    }

    /// <summary>Opens the menu for the given items; falls back to nothing if the shell has no menu.</summary>
    public static void Show(IntPtr owner, IReadOnlyList<string> parsingNames, POINT anchor, MouseHook hook, Action onRename)
    {
        var session = ShellContextMenu.Session.Create(owner, parsingNames);
        if (session is null)
            return;
        var menu = new ModernShellMenu(session, parsingNames, owner, anchor, hook, onRename);
        menu.Show();
        menu.UpdateLayout();
        menu.PlaceWithinWorkArea();
        menu.Activate();
    }

    // ---------------------------------------------------------------- building

    private void Build()
    {
        var entries = _session.Entries;
        var topVerbs = TopRowVerbs.Select(t => t.Verb).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var topRow = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var (verb, glyph) in TopRowVerbs)
        {
            var entry = entries.FirstOrDefault(e => string.Equals(e.Verb, verb, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
                topRow.Children.Add(CreateTopButton(entry, glyph));
        }
        if (topRow.Children.Count > 0)
        {
            Root.Children.Add(topRow);
            Root.Children.Add(CreateSeparator());
        }

        var list = entries
            .Where(e => e.Verb is null || (!topVerbs.Contains(e.Verb) && !MoreOptionsOnly.Contains(e.Verb)))
            .ToList();
        AddRows(Root, TrimSeparators(list), isRoot: true);

        Root.Children.Add(CreateSeparator());
        Root.Children.Add(CreateRow("", null, "Mostrar más opciones", "Mayús+F10", hasChildren: false,
            enabled: true, onClick: ShowMoreOptions));
    }

    private void AddRows(Panel panel, IEnumerable<ShellContextMenu.Entry> entries, bool isRoot)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                panel.Children.Add(CreateSeparator());
                continue;
            }
            var hasChildren = entry.Children.Count > 0;
            var row = CreateRow(null, entry.Icon, entry.Text, entry.Shortcut, hasChildren, entry.IsEnabled,
                hasChildren ? null : () => Run(entry));
            if (hasChildren)
                AttachSubmenu(row, entry, isRoot);
            panel.Children.Add(row);
        }
    }

    private static IEnumerable<ShellContextMenu.Entry> TrimSeparators(List<ShellContextMenu.Entry> entries)
    {
        ShellContextMenu.Entry? previous = null;
        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (previous is null || previous.IsSeparator))
                continue;
            previous = entry;
            yield return entry;
        }
    }

    private UIElement CreateTopButton(ShellContextMenu.Entry entry, string glyph)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("Font.Icons"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = entry.Text,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var button = CreateHoverBorder(content, new Thickness(6, 7, 6, 6));
        button.ToolTip = entry.Text;
        button.MouseLeftButtonUp += (_, _) => Run(entry);
        return button;
    }

    private Border CreateRow(string? glyph, ImageSource? icon, string text, string? shortcut, bool hasChildren,
        bool enabled, Action? onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        UIElement? leading = icon is not null
            ? new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left }
            : glyph is not null
                ? new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center }
                : null;
        if (leading is not null)
            grid.Children.Add(leading);

        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(shortcut))
        {
            var keys = new TextBlock
            {
                Text = shortcut,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Brush.TextSecondary"),
            };
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }
        if (hasChildren)
        {
            var chevron = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("Font.Icons"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(chevron);
        }

        var row = CreateHoverBorder(grid, new Thickness(10, 7, 6, 7));
        row.IsEnabled = enabled;
        row.Opacity = enabled ? 1 : 0.4;
        if (onClick is not null)
            row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    private Border CreateHoverBorder(UIElement content, Thickness padding)
    {
        var border = new Border
        {
            Child = content,
            Padding = padding,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
        };
        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("Brush.HoverFill");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    private Border CreateSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(-4, 4, -4, 4),
        Background = (Brush)FindResource("Brush.Separator"),
    };

    /// <summary>Submenus open to the side on hover, like Windows 11's.</summary>
    private void AttachSubmenu(Border row, ShellContextMenu.Entry entry, bool isRoot)
    {
        var panel = new StackPanel();
        AddRows(panel, TrimSeparators(entry.Children), isRoot: false);
        var card = new Border
        {
            Child = panel,
            MinWidth = 220,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Brush.Surface"),
            BorderBrush = (Brush)FindResource("Brush.SurfaceBorder"),
            BorderThickness = new Thickness(1),
        };
        var popup = new Popup
        {
            Child = card,
            PlacementTarget = row,
            Placement = PlacementMode.Right,
            AllowsTransparency = true,
            StaysOpen = true,
            HorizontalOffset = 2,
        };
        row.MouseEnter += (_, _) =>
        {
            if (isRoot)
            {
                foreach (var open in _openPopups.Where(p => p != popup))
                    open.IsOpen = false;
            }
            popup.IsOpen = true;
            if (!_openPopups.Contains(popup))
                _openPopups.Add(popup);
        };
    }

    // ---------------------------------------------------------------- actions

    private void Run(ShellContextMenu.Entry entry)
    {
        if (!entry.IsEnabled)
            return;
        var isRename = string.Equals(entry.Verb, "rename", StringComparison.OrdinalIgnoreCase);
        // Invoke before closing: closing disposes the shell menu the command belongs to.
        // _closing stops the deactivation caused by Hide() from closing it early.
        _closing = true;
        Hide();
        foreach (var popup in _openPopups)
            popup.IsOpen = false;
        if (isRename)
            _onRename();
        else
            _session.Invoke(entry, _anchor);
        Close();
    }

    private void ShowMoreOptions()
    {
        Dismiss();
        Dispatcher.BeginInvoke(() => ShellContextMenu.Show(_owner, _parsingNames, _anchor));
    }

    private void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    private void OnGlobalMouse(object? sender, LowLevelMouseEventArgs e)
    {
        if (e.Message is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN))
            return;
        if (Contains(_hwnd, e.Point))
            return;
        foreach (var popup in _openPopups.Where(p => p.IsOpen))
        {
            if (popup.Child is not null && PresentationSource.FromVisual(popup.Child) is HwndSource source &&
                Contains(source.Handle, e.Point))
                return;
        }
        Dispatcher.BeginInvoke(Dismiss);
    }

    private static bool Contains(IntPtr hwnd, POINT p)
    {
        GetWindowRect(hwnd, out var r);
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    private void PlaceWithinWorkArea()
    {
        GetWindowRect(_hwnd, out var size);
        var inset = (int)Math.Round(12 * GetScaleAt(_anchor));
        var x = _anchor.X - inset;
        var y = _anchor.Y - inset;
        var work = GetWorkAreaAt(_anchor);
        if (x + size.Width > work.Right) x = _anchor.X - size.Width + inset;
        if (y + size.Height > work.Bottom) y = Math.Max(work.Top - inset, work.Bottom - size.Height + inset);
        x = Math.Max(work.Left - inset, x);
        y = Math.Max(work.Top - inset, y);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);
    }
}
