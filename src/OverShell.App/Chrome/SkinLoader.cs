using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using OverShell.Core;

namespace OverShell.App.Chrome;

/// <summary>
/// XAML skins (DESIGN.md §12.5): a <c>ResourceDictionary</c> under
/// <c>%APPDATA%\OverShell\skins\&lt;name&gt;.xaml</c> whose keys override the theme's.
/// Applied before the first window exists, so every <c>StaticResource</c> — fonts,
/// metrics, brushes — resolves to the skin's value; re-applied on save, when only brush
/// <em>colours</em> can still change: a font or a height already copied into a control
/// stays until restart, but every theme brush is shared, so recolouring it repaints
/// everything that uses it.
/// <para>
/// Recolouring needs an unfrozen brush, and an application-owned resource dictionary
/// freezes every freezable put into it (<c>SealIfSealable</c>). A brush whose
/// <c>Color</c> is data-bound cannot be frozen, so each theme brush is replaced at start
/// by one bound to a <see cref="ThemeColor"/>; a skin then writes the colour into the
/// model and the binding repaints. Skins are the user's own files and are trusted like
/// configuration (documented).
/// </para>
/// </summary>
internal static class SkinLoader
{
    private static readonly Dictionary<string, ThemeColor> Colors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Color> Skinned = new(StringComparer.Ordinal);
    private static ResourceDictionary? _current;

    public static string? CurrentName { get; private set; }

    /// <summary>Replaces every theme brush with a bound, unfreezable one. Call once, before the first window.</summary>
    public static int PrepareThemeForLiveRecolour()
    {
        var app = Application.Current;
        if (app is null)
        {
            return 0;
        }

        var swapped = 0;
        foreach (var dictionary in app.Resources.MergedDictionaries)
        {
            foreach (var key in dictionary.Keys.Cast<object>().ToArray())
            {
                if (key is string name && dictionary[key] is SolidColorBrush brush && !Colors.ContainsKey(name))
                {
                    var model = new ThemeColor(brush.Color);
                    var bound = new SolidColorBrush();
                    BindingOperations.SetBinding(bound, SolidColorBrush.ColorProperty, new Binding(nameof(ThemeColor.Value)) { Source = model, Mode = BindingMode.OneWay });
                    dictionary[key] = bound;
                    Colors[name] = model;
                    swapped++;
                }
            }
        }

        return swapped;
    }

    /// <summary>Applies (or, with null, removes) a skin. Returns a problem description, or null when all went well.</summary>
    public static string? Apply(string? skinName)
    {
        var app = Application.Current;
        if (app is null)
        {
            return "no application";
        }

        Revert(app);
        CurrentName = null;

        if (string.IsNullOrWhiteSpace(skinName))
        {
            return null;
        }

        var path = Path.Combine(AppPaths.SkinsDir, skinName + ".xaml");
        if (!File.Exists(path))
        {
            return $"skin '{skinName}' not found at {path}";
        }

        ResourceDictionary skin;
        try
        {
            using var stream = File.OpenRead(path);
            skin = XamlReader.Load(stream) as ResourceDictionary ?? throw new InvalidDataException("the root element is not a ResourceDictionary");
        }
        catch (Exception e) when (e is XamlParseException or IOException or InvalidDataException or InvalidCastException)
        {
            return $"skin '{skinName}': {e.Message}";
        }

        var extra = new ResourceDictionary();
        foreach (var key in skin.Keys)
        {
            var value = skin[key];

            // A colour for a theme brush goes into the model: the bound brush repaints live.
            if (key is string name && Colors.TryGetValue(name, out var model))
            {
                Color? color = value switch
                {
                    SolidColorBrush brush => brush.Color,
                    Color c => c,
                    _ => null,
                };

                if (color is { } c2)
                {
                    Skinned[name] = model.Value;
                    model.Value = c2;
                    continue;
                }
            }

            extra[key] = value;
        }

        if (extra.Count > 0)
        {
            app.Resources.MergedDictionaries.Add(extra);
            _current = extra;
        }

        CurrentName = skinName;
        return null;
    }

    private static void Revert(Application app)
    {
        foreach (var (name, original) in Skinned)
        {
            if (Colors.TryGetValue(name, out var model))
            {
                model.Value = original;
            }
        }

        Skinned.Clear();

        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
            _current = null;
        }
    }

    /// <summary>The colour behind one theme brush; the brush's Color is bound to <see cref="Value"/>.</summary>
    private sealed class ThemeColor(Color initial) : INotifyPropertyChanged
    {
        private Color _value = initial;

        public event PropertyChangedEventHandler? PropertyChanged;

        public Color Value
        {
            get => _value;
            set
            {
                if (_value == value)
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }
}
