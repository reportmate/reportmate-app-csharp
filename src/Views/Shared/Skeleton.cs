using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ReportMate.App.Views.Shared;

/// <summary>
/// Skeleton loading: grey blocks laid out like the page that is about to arrive,
/// pulsing gently, in place of a spinner. The web's <c>animate-pulse</c> blocks are
/// the reference -- <c>bg-gray-200</c> / <c>dark:bg-gray-700</c> is
/// <c>SkeletonBrush</c> in either palette -- so a page holds its shape while it
/// loads and nothing jumps when the data lands.
/// </summary>
public static class Skeleton
{
    // ── Primitives ───────────────────────────────────────────────────────

    /// <summary>A rounded block. Without a width it stretches to its container.</summary>
    public static Border Block(double? width, double height, double radius = 6) => new()
    {
        Width = width ?? double.NaN,
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = Ui.Brush("SkeletonBrush"),
        HorizontalAlignment = width is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A line of text that has not arrived yet.</summary>
    public static Border Line(double? width, double height = 10) => Block(width, height, height / 2);

    public static Ellipse Circle(double size) => new()
    {
        Width = size, Height = size, Fill = Ui.Brush("SkeletonBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A donut's ring, the shape the insight cards' charts take.</summary>
    public static Ellipse Ring(double size, double thickness) => new()
    {
        Width = size, Height = size, Stroke = Ui.Brush("SkeletonBrush"), StrokeThickness = thickness,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Pulse a skeleton: one opacity animation on its root rather than one per block,
    /// so the whole page breathes together, as the web's does.
    /// </summary>
    public static T Pulse<T>(T root) where T : UIElement
    {
        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(1000))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // Runs only while on screen: an animation left running on a skeleton the
        // data has replaced would keep ticking, and the element alive, for nothing.
        if (root is FrameworkElement fe)
        {
            fe.Loaded += (_, _) => fe.BeginAnimation(UIElement.OpacityProperty, pulse);
            fe.Unloaded += (_, _) => fe.BeginAnimation(UIElement.OpacityProperty, null);
            if (fe.IsLoaded) fe.BeginAnimation(UIElement.OpacityProperty, pulse);
        }
        System.Windows.Automation.AutomationProperties.SetName(root, "Loading");
        return root;
    }

    // ── Parts ────────────────────────────────────────────────────────────

    /// <summary>A card with a header -- icon tile, title, subtitle -- over a body.</summary>
    public static Border Card(UIElement body, double titleWidth = 140)
    {
        var card = new Border { Style = (Style)Ui.Res("CardStyle") };
        var root = new StackPanel();
        var header = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = Block(40, 40, 8);
        tile.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Line(titleWidth, 13));
        var sub = Line(titleWidth * 1.4, 9);
        sub.Margin = new Thickness(0, 8, 0, 0);
        titles.Children.Add(sub);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
        root.Children.Add(new Border { Padding = new Thickness(20, 16, 20, 18), Child = body });
        card.Child = root;
        return card;
    }

    /// <summary>A page's title block: the 48 px tile, the title and the subtitle.</summary>
    public static Grid PageHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = Block(48, 48, 10);
        tile.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Line(220, 18));
        var sub = Line(320, 11);
        sub.Margin = new Thickness(0, 10, 0, 0);
        titles.Children.Add(sub);
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        return grid;
    }

    /// <summary>Label, bar, number: the rows of every bar list.</summary>
    public static StackPanel Bars(int count, bool withTrack = true)
    {
        var panel = new StackPanel();
        var widths = new[] { 0.55, 0.8, 0.4, 0.65, 0.3, 0.5, 0.7, 0.45 };
        for (var i = 0; i < count; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var label = Line(null, 10);
            label.Margin = new Thickness(0, 0, 24 + 60 * (1 - widths[i % widths.Length]), 0);
            row.Children.Add(label);
            if (withTrack)
            {
                var track = new Grid();
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i % widths.Length], GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - widths[i % widths.Length], GridUnitType.Star) });
                track.Children.Add(Block(null, 8, 4));
                Grid.SetColumn(track, 1);
                row.Children.Add(track);
            }
            var n = Line(28, 10);
            n.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(n, 2);
            row.Children.Add(n);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A donut with its legend beside it.</summary>
    public static Grid Donut(int legendRows = 3, double size = 104)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Ring(size, 14));
        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        for (var i = 0; i < legendRows; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var dot = Circle(9);
            dot.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(dot);
            row.Children.Add(Line(70 + 25 * (i % 2), 10));
            legend.Children.Add(row);
        }
        Grid.SetColumn(legend, 1);
        grid.Children.Add(legend);
        return grid;
    }

    /// <summary>A row of chip-shaped pills.</summary>
    public static WrapPanel Chips(int count)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < count; i++)
        {
            var chip = Block(90 + 20 * (i % 3), 24, 12);
            chip.Margin = new Thickness(0, 0, 8, 8);
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    /// <summary>Large numbers over their captions, side by side.</summary>
    public static Grid Figures(int count)
    {
        var cells = Enumerable.Range(0, count).Select(_ =>
        {
            var cell = new StackPanel();
            cell.Children.Add(Line(48, 22));
            var caption = Line(90, 9);
            caption.Margin = new Thickness(0, 8, 0, 0);
            cell.Children.Add(caption);
            return (UIElement)cell;
        }).ToArray();
        return Ui.Columns(count, 10, cells);
    }

    /// <summary>A list of name / detail rows, like New Clients or Recent Events.</summary>
    public static StackPanel List(int count, bool leadingPill = false)
    {
        var panel = new StackPanel();
        for (var i = 0; i < count; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (leadingPill)
            {
                var pill = Block(52, 18, 9);
                pill.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(pill);
            }
            var text = Line(null, 10);
            text.Margin = new Thickness(0, 0, 40 + 30 * (i % 4), 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var meta = Line(64, 9);
            Grid.SetColumn(meta, 2);
            row.Children.Add(meta);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A table card: title row, column headers, then rows.</summary>
    public static Border Table(int rows = 12, int columns = 6)
    {
        var card = new Border { Style = (Style)Ui.Res("CardStyle") };
        var root = new StackPanel();

        var title = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(Line(160, 13));
        var search = Block(240, 30, 6);
        Grid.SetColumn(search, 1);
        title.Children.Add(search);
        root.Children.Add(title);
        root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });

        Grid Row(double height, double lineHeight, int index)
        {
            var grid = new Grid { Height = height, Margin = new Thickness(20, 0, 20, 0) };
            for (var c = 0; c < columns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? 2.2 : 1, GridUnitType.Star) });
                var line = Line(null, lineHeight);
                line.Margin = new Thickness(0, 0, 18 + 14 * ((c + index) % 4), 0);
                Grid.SetColumn(line, c);
                grid.Children.Add(line);
            }
            return grid;
        }

        root.Children.Add(Row(38, 9, 1));
        for (var r = 0; r < rows; r++)
        {
            root.Children.Add(new Border { Height = 1, Background = Ui.Brush("DividerBrush") });
            root.Children.Add(Row(44, 10, r));
        }
        card.Child = root;
        return card;
    }

    /// <summary>A grid of cards with bar lists, the shape of a report's distributions.</summary>
    public static UniformCardPanel CardGrid(int cards, double minWidth = 300, int maxColumns = 6, int bars = 5)
    {
        var grid = Ui.CardGrid(minWidth, 14);
        grid.MaxColumns = maxColumns;
        for (var i = 0; i < cards; i++) grid.Children.Add(Card(Bars(bars - i % 2)));
        return grid;
    }

    /// <summary>A row of filter pills over a list.</summary>
    public static WrapPanel FilterRow(int count = 5)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        for (var i = 0; i < count; i++)
        {
            var pill = Block(i == 0 ? 96 : 76 + 10 * (i % 3), 30, 15);
            pill.Margin = new Thickness(0, 0, 8, 0);
            wrap.Children.Add(pill);
        }
        return wrap;
    }

    // ── Pages ────────────────────────────────────────────────────────────

    /// <summary>A report page: the distribution cards, then the table.</summary>
    public static StackPanel ReportBody()
    {
        var page = new StackPanel();
        var grid = CardGrid(6);
        grid.Margin = new Thickness(0, 0, 0, 14);
        page.Children.Add(grid);
        page.Children.Add(Table());
        return page;
    }

    /// <summary>The Devices and Events lists: filter pills over a long table.</summary>
    public static StackPanel ListBody(int columns = 8)
    {
        var page = new StackPanel();
        page.Children.Add(FilterRow());
        page.Children.Add(Table(14, columns));
        return page;
    }

    /// <summary>Failed check-ins: the outcome chips, the reasons card, the table.</summary>
    public static StackPanel FailuresBody()
    {
        var page = new StackPanel();
        page.Children.Add(FilterRow(3));
        var reasons = Card(Bars(5));
        reasons.Margin = new Thickness(0, 4, 0, 18);
        page.Children.Add(reasons);
        page.Children.Add(Table(10, 7));
        return page;
    }

    /// <summary>
    /// A device tab: a row of data-point tiles, then a two-column grid of cards,
    /// the shape every tab on the device page takes.
    /// </summary>
    public static StackPanel DeviceTab()
    {
        var page = new StackPanel();
        page.Children.Add(PageHeader());
        var tiles = Ui.CardGrid(180, 12);
        tiles.MaxColumns = 4;
        for (var i = 0; i < 4; i++)
        {
            var tile = new StackPanel();
            tile.Children.Add(Line(80, 9));
            var value = Line(120, 18);
            value.Margin = new Thickness(0, 10, 0, 0);
            tile.Children.Add(value);
            tiles.Children.Add(Ui.Card(tile));
        }
        tiles.Margin = new Thickness(0, 0, 0, 16);
        page.Children.Add(tiles);
        var cards = Ui.CardGrid(380, 16);
        cards.MaxColumns = 2;
        cards.Children.Add(Card(Rows(6)));
        cards.Children.Add(Card(Rows(6)));
        cards.Children.Add(Card(Bars(4)));
        cards.Children.Add(Card(Rows(5)));
        page.Children.Add(cards);
        return page;
    }

    /// <summary>Label / value pairs, the body of most device cards.</summary>
    public static StackPanel Rows(int count)
    {
        var panel = new StackPanel();
        for (var i = 0; i < count; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Line(70 + 20 * (i % 3), 10));
            var value = Line(100 + 30 * ((i + 1) % 3), 10);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>The body of an insight card while its read is in flight.</summary>
    public static StackPanel InsightBody(InsightShape shape)
    {
        var body = new StackPanel();
        switch (shape)
        {
            case InsightShape.Donut:
                body.Children.Add(Donut());
                var chips = Chips(3);
                chips.Margin = new Thickness(0, 16, 0, 0);
                body.Children.Add(chips);
                break;
            case InsightShape.Figures:
                body.Children.Add(Figures(2));
                var bars = Bars(5);
                bars.Margin = new Thickness(0, 18, 0, 0);
                body.Children.Add(bars);
                break;
            default:
                body.Children.Add(Bars(6));
                body.Children.Add(Chips(2));
                break;
        }
        return body;
    }
}

/// <summary>Which shape an insight card's skeleton takes, matching what it will draw.</summary>
public enum InsightShape { Bars, Donut, Figures }
