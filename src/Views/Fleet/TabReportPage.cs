using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The title switch of a module report: the views the web lists for the module, at
/// title size, each opening its own view. The switch is the page's title, as on the web.
/// </summary>
public static class ModuleSwitch
{
    public static Grid Header(FrameworkElement from, string module, string current, UIElement? right = null)
    {
        var area = ReportArea.ById(module)!;
        var tabs = ModuleTabs.All[module];
        var views = Ui.Segmented(tabs.Select(t => (t.Key, t.Label)), current, key =>
        {
            if (key != current) DashboardView.Follow(from, ModuleTabs.Link(module, key));
        });
        var subtitle = tabs.FirstOrDefault(t => t.Key == current)?.Subtitle ?? area.Subtitle;
        return Ui.TabHeader(views, subtitle, area.Glyph, area.Accent, right);
    }
}

/// <summary>
/// One module tab report -- Services, Updates, Background Items, Extensions,
/// Configuration Profiles, Managed Preferences, Vulnerabilities, Threat Detections,
/// Sign-in &amp; Recovery Posture -- drawn from its <see cref="TabReportDef"/>. The
/// selections come from the report's <c>/filters</c> catalog, so a reader picks from
/// what devices actually reported; every choice lives in the link; the table is the
/// whole report in one scrolling grid; Export writes exactly the rows on screen in the
/// order they are sorted.
/// </summary>
public sealed class TabReportPage : FleetPage
{
    private readonly string _module;
    private readonly string _tab;
    private readonly DeepLink? _carried;
    private TabQuery _query = new();
    private bool _seeded;
    private bool _selectionsOpen;

    private string? _catalogKey;
    private JsonElement? _catalog;
    private string? _catalogError;
    private string? _dataKey;
    private TabLoad? _load;

    public TabReportPage(string module, string tab, DeepLink? carried = null)
    {
        _module = module;
        _tab = tab;
        _carried = carried;
    }

    private static string Platform => PlatformFilter.Current switch
    {
        PlatformScope.Windows => "Windows",
        PlatformScope.Mac => "macOS",
        _ => "all",
    };

    private ModuleTab Tab => ModuleTabs.All[_module].First(t => t.Key == _tab);

    protected override (string, string, Accent)? Heading => (Tab.Label, Tab.Subtitle, ReportArea.ById(_module)!.Accent);

    /// <summary>The link that reopens exactly this view, for the platform toggle to carry across.</summary>
    public DeepLink CurrentLink() => _query.Link(_module, _tab);

    // Forced, so a choice made while the report is still loading starts over with it
    // rather than being dropped; only the newest load draws the page.
    private void Apply(TabQuery next)
    {
        _query = next;
        _ = ReloadAsync(force: true);
    }

    private void Refresh()
    {
        _catalogKey = null;
        _dataKey = null;
        _ = ReloadAsync(force: true);
    }

    protected override async Task<UIElement> BuildAsync()
    {
        if (!_seeded)
        {
            _seeded = true;
            _query = TabQuery.From(Link ?? _carried);
        }
        var platform = Platform;
        var def = TabReports.Find(_module, _tab, _query)!;
        var hasQuery = def.HasQuery(_query);
        var page = new StackPanel();

        var copy = Ui.IconButton("", "Copy link", (_, _) =>
            ClipboardHelper.Copy(CurrentLink().ToHandoffUrl(ConfigManager.Instance.WebDashboardUrl)),
            "Copy a shareable link to this exact report");
        var refresh = Ui.IconButton("", "Refresh", (_, _) => Refresh());
        refresh.Margin = new Thickness(8, 0, 0, 0);
        page.Children.Add(ModuleSwitch.Header(this, _module, _tab, Ui.HStack(copy, refresh)));

        if (def.NotApplicableOn == platform)
        {
            page.Children.Add(Ui.Card(Ui.EmptyState(def.NotApplicableText)));
            return page;
        }

        // The selections catalog: the values devices actually reported, for the pickers.
        if (def.OptionsPath(_query, platform) is { } optionsPath && optionsPath != _catalogKey)
        {
            var options = await FleetApiClient.Instance.GetRawAsync(optionsPath);
            _catalogKey = optionsPath;
            if (options.Ok)
            {
                using var doc = options.Data!;
                _catalog = doc.RootElement.Clone();
                _catalogError = null;
            }
            else
            {
                _catalog = null;
                _catalogError = options.HttpStatus == 404
                    ? def.NotFoundText
                    : options.Detail ?? "Available report selections could not be loaded.";
            }
        }

        // The report itself, read again only when what it asks the API changes: a sort,
        // a column or a view-only filter redraws what is already loaded.
        if (hasQuery)
        {
            var key = def.Path(_query, platform);
            if (key != _dataKey || _load is null)
            {
                _load = await def.LoadAsync(_query, platform, FetchAsync, (done, total) => Progress(done, total));
                _dataKey = _load.Ok ? key : null;
            }
        }
        else
        {
            _load = null;
            _dataKey = null;
        }

        var data = hasQuery ? _load?.Data : null;
        if (hasQuery && _load is { Ok: false } failed && failed.NotFound && _catalog is null)
        {
            page.Children.Add(Ui.Card(Ui.EmptyState($"Not available yet: {failed.Error}")));
            return page;
        }

        var context = new TabContext(_query, platform, def.FiltersApi is null ? data?.Root : _catalog, data);
        page.Children.Add(Selections(def, context, hasQuery));
        if (Chips(def, context) is { } chips) page.Children.Add(chips);

        if (_catalogError is not null || _load is { Ok: false })
        {
            var error = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var text = Ui.Status(_load?.Error ?? _catalogError, Tone.Error);
            text.VerticalAlignment = VerticalAlignment.Center;
            error.Children.Add(text);
            var retry = Ui.IconButton("", "Retry", (_, _) => Refresh());
            retry.Margin = new Thickness(10, 0, 0, 0);
            error.Children.Add(retry);
            page.Children.Add(error);
        }

        if (!hasQuery)
        {
            var prompt = new StackPanel { Margin = new Thickness(0, 30, 0, 30), HorizontalAlignment = HorizontalAlignment.Center };
            var lead = Ui.Text(def.Prompt, "TitleTextStyle");
            lead.TextWrapping = TextWrapping.Wrap;
            lead.TextAlignment = TextAlignment.Center;
            prompt.Children.Add(lead);
            var hint = Ui.Caption("Selections list only values devices have reported. Results update as you select.");
            hint.TextAlignment = TextAlignment.Center;
            hint.Margin = new Thickness(0, 8, 0, 0);
            prompt.Children.Add(hint);
            page.Children.Add(Ui.Card(prompt));
            page.Children.Add(Footnote(def));
            return page;
        }
        if (data is null)
        {
            page.Children.Add(Footnote(def));
            return page;
        }

        var rows = def.ViewRows(context);
        page.Children.Add(ResultBar(def, context, rows.Count));
        page.Children.Add(Table(def, context, rows));
        page.Children.Add(Footnote(def));
        return page;
    }

    private static async Task<TabFetch> FetchAsync(string path)
    {
        var result = await FleetApiClient.Instance.GetRawAsync(path);
        if (!result.Ok) return new TabFetch(false, result.HttpStatus, result.Detail, null);
        using var doc = result.Data!;
        return new TabFetch(true, result.HttpStatus, null, doc.RootElement.Clone());
    }

    private static UIElement Footnote(TabReportDef def)
    {
        var note = Ui.Caption(def.Footnote);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 14, 0, 0);
        return note;
    }

    // ── Controls ─────────────────────────────────────────────────────────

    /// <summary>A choice pill: outlined when off, tinted when on, as the web's choice buttons are.</summary>
    private static Border Chip(string text, bool active, Action onClick, string? tip = null)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = active ? Ui.StatusBrush(Tone.Info) : Ui.Brush("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Background = active ? Ui.Tint(Tone.Info, 0.15) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = active ? Ui.Tint(Tone.Info, 0.45) : Ui.Brush("CardBorderBrush"),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = tip ?? text,
            Child = label,
        };
        AutomationPropertiesHelper.Name(chip, text, active);
        chip.MouseLeftButtonUp += (_, _) => onClick();
        chip.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return chip;
    }

    private static string WithCount(TabOption option) => option.Count is { } n ? $"{option.Label}  {n:N0}" : option.Label;

    private UIElement Group(string label, IEnumerable<UIElement> chips)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 28, 12) };
        var heading = Ui.Eyebrow(label.ToUpperInvariant());
        heading.Margin = new Thickness(0, 0, 0, 6);
        group.Children.Add(heading);
        var wrap = new WrapPanel();
        foreach (var chip in chips) wrap.Children.Add(chip);
        group.Children.Add(wrap);
        return group;
    }

    /// <summary>The pills of one choice group, with the API's count beside each when it gives one.</summary>
    private IEnumerable<UIElement> ChoicePills(TabReportDef def, TabChoice choice, TabContext context)
    {
        var options = choice.Resolve(context);
        string Counted(TabOption o)
        {
            if (choice.SummaryKey is null || context.Data is null) return o.Label;
            var key = choice.SummaryKey.Length == 0 ? o.Value : $"{choice.SummaryKey}:{o.Value}";
            return $"{o.Label}  {TabReports.Count(context.Data.Summary(key)):N0}";
        }
        TabQuery Reset(TabQuery q) => q.With(choice.Resets.Select(r => (r, (string?)null)).ToArray());

        switch (choice.Mode)
        {
            case ChoiceMode.Multi:
            {
                var selected = _query.List(choice.Param);
                yield return Chip(choice.AllLabel, selected.Count == 0, () => Apply(Reset(_query.With((choice.Param, null)))));
                foreach (var o in options)
                    yield return Chip(Counted(o), selected.Contains(o.Value), () => Apply(Reset(_query.Toggle(choice.Param, o.Value))), o.Value);
                break;
            }
            case ChoiceMode.Single:
            {
                var current = _query[choice.Param] ?? choice.Default ?? "";
                foreach (var o in options)
                    yield return Chip(Counted(o), current == o.Value,
                        () => Apply(Reset(_query.With((choice.Param, o.Value == (choice.Default ?? "") ? null : o.Value)))));
                break;
            }
            case ChoiceMode.Toggle:
                foreach (var o in options)
                    yield return Chip(o.Label, _query[choice.Param] == o.Value,
                        () => Apply(Reset(_query.With((choice.Param, _query[choice.Param] == o.Value ? null : o.Value)))));
                break;
        }
    }

    /// <summary>
    /// The Selections panel: the inventory-backed pickers, then every filter group. Open
    /// until the report has something to show, then folded so the table leads.
    /// </summary>
    private UIElement Selections(TabReportDef def, TabContext context, bool hasQuery)
    {
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var pickers = def.Selections.Where(s => s.Visible?.Invoke(context) ?? true).ToList();
        if (pickers.Count > 0)
        {
            var grid = Ui.CardGrid(380, 18);
            grid.MaxColumns = 2;
            foreach (var selection in pickers) grid.Children.Add(Picker(selection, context));
            body.Children.Add(grid);
        }
        var groups = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var choice in def.Choices.Where(c => !c.Prominent && (c.Visible?.Invoke(context) ?? true)))
        {
            var pills = ChoicePills(def, choice, context).ToList();
            if (choice.Mode == ChoiceMode.Multi && pills.Count <= 1) continue;
            groups.Children.Add(Group(choice.Label, pills));
        }
        body.Children.Add(groups);

        var active = def.Selections.Sum(s => s.Current(context).Count)
            + def.Choices.Count(c => !c.Prominent && _query[c.Param] is not null);
        var expander = new Expander
        {
            Header = active > 0 ? $"Selections ({active:N0} active)" : "Selections",
            IsExpanded = _selectionsOpen || !hasQuery,
            Content = body,
        };
        expander.Expanded += (_, _) => _selectionsOpen = true;
        expander.Collapsed += (_, _) => _selectionsOpen = false;
        var card = Ui.Card(expander, new Thickness(18, 12, 18, 12));
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    /// <summary>
    /// A searchable picker over the collected values. At most 150 choices are drawn at
    /// once, so a catalog of several thousand services stays responsive; the search
    /// narrows to the rest, and "Select N results" takes every match at once.
    /// </summary>
    private UIElement Picker(TabSelection selection, TabContext context)
    {
        const int Shown = 150;
        var options = selection.Options(context);
        var selected = selection.Current(context);
        var panel = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Ui.Label(selected.Count > 0 ? $"{selection.Label} ({selected.Count:N0} selected)" : selection.Label));
        panel.Children.Add(head);

        var wrap = new WrapPanel();
        var note = Ui.Caption("");
        note.Visibility = Visibility.Collapsed;
        var query = "";

        void Choose(IEnumerable<string> values)
        {
            var list = values.ToList();
            if (selection.Max is { } max) list = list.Take(max).ToList();
            Apply(_query.With(selection.Resets.Select(r => (r, (string?)null)).ToArray()).WithList(selection.Param, list, selection.Json));
        }

        void Draw()
        {
            wrap.Children.Clear();
            actions.Children.Clear();
            var visible = options.Where(o => query.Length == 0
                || $"{o.Label} {o.Value}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var o in visible.Take(Shown))
                wrap.Children.Add(Chip(WithCount(o), selected.Contains(o.Value),
                    () => Choose(selected.Contains(o.Value) ? selected.Where(v => v != o.Value) : selected.Append(o.Value)), o.Value));
            if (visible.Count == 0)
                wrap.Children.Add(Ui.Caption(query.Length > 0 ? "No matching choices." : _catalogError ?? "No choices reported in this scope."));
            note.Text = $"Showing {Shown} of {visible.Count:N0} choices. Search to narrow the list.";
            note.Visibility = visible.Count > Shown ? Visibility.Visible : Visibility.Collapsed;
            if (query.Length > 0 && visible.Count > 0)
                actions.Children.Add(Ui.IconButton("", $"Select {visible.Count:N0} results",
                    (_, _) => Choose(selected.Concat(visible.Select(v => v.Value)).Distinct())));
            if (selected.Count > 0)
            {
                var clear = Ui.IconButton("", "Clear", (_, _) => Choose([]));
                clear.Margin = new Thickness(6, 0, 0, 0);
                actions.Children.Add(clear);
            }
        }

        var search = new SearchBox($"Search {selection.Label.ToLowerInvariant()}...", q => { query = q.Trim(); Draw(); })
        {
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(search);
        panel.Children.Add(note);
        panel.Children.Add(new ScrollViewer
        {
            Content = wrap, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Draw();
        return panel;
    }

    /// <summary>The selected items as removable chips, and a way to clear every selection.</summary>
    private UIElement? Chips(TabReportDef def, TabContext context)
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var selection in def.Selections)
        {
            var current = selection.Current(context);
            var labels = selection.Options(context).ToDictionary(o => o.Value, o => o.Label);
            foreach (var value in current)
            {
                var label = labels.TryGetValue(value, out var l) ? l : value;
                chips.Children.Add(Chip(label + "  ×", true,
                    () => Apply(_query.With(selection.Resets.Select(r => (r, (string?)null)).ToArray())
                        .WithList(selection.Param, current.Where(v => v != value), selection.Json)), $"Remove {label}"));
            }
        }
        if (chips.Children.Count == 0) return null;
        chips.Children.Add(Chip("Clear selections", false, () => Apply(new TabQuery())));
        return chips;
    }

    /// <summary>The row count, the prominent filters, the coverage line and the column chooser.</summary>
    private UIElement ResultBar(TabReportDef def, TabContext context, int shown)
    {
        var bar = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var top = new WrapPanel();
        var total = context.Data!.Paged(def);
        var count = Ui.Caption(shown != total ? $"{shown:N0} {def.RowNoun} of {total:N0}" : $"{shown:N0} {def.RowNoun}");
        count.Margin = new Thickness(0, 4, 20, 8);
        count.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(count);
        foreach (var choice in def.Choices.Where(c => c.Prominent && (c.Visible?.Invoke(context) ?? true)))
        {
            var pills = new WrapPanel { Margin = new Thickness(0, 0, 20, 0) };
            foreach (var pill in ChoicePills(def, choice, context)) pills.Children.Add(pill);
            top.Children.Add(pills);
        }
        bar.Children.Add(top);

        if (def.Caption?.Invoke(context, shown) is { Length: > 0 } caption)
        {
            var line = Ui.Caption(caption);
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 0, 0, 8);
            bar.Children.Add(line);
        }

        if (def.ColumnChooser)
        {
            var visible = def.VisibleColumns(context).Select(c => c.Key).ToList();
            var chooser = new WrapPanel();
            foreach (var column in def.AvailableColumns(context).Skip(1))
            {
                var on = visible.Contains(column.Key);
                chooser.Children.Add(Chip(column.Label, on, () =>
                {
                    var next = on ? visible.Where(k => k != column.Key).ToList() : visible.Append(column.Key).ToList();
                    var sortGone = _query[def.SortParam] is { } s && !next.Contains(s);
                    Apply(_query.With(("columns", string.Join(",", next)),
                        (def.SortParam, sortGone ? null : _query[def.SortParam]), (def.DirParam, sortGone ? null : _query[def.DirParam])));
                }));
            }
            chooser.Children.Add(Chip("Reset columns", false, () => Apply(_query.With(("columns", null), (def.SortParam, null), (def.DirParam, null)))));
            bar.Children.Add(new Expander { Header = "Columns", Content = chooser, Margin = new Thickness(0, 0, 0, 8) });
        }
        return bar;
    }

    private UIElement Table(TabReportDef def, TabContext context, List<TabRow> rows)
    {
        var columns = def.VisibleColumns(context);
        var grid = rows.Select(r => new TabGridRow(r, columns)).ToList();
        var cols = columns.Select(c => c.Cell == TabCell.State
                ? Col.Pill(c.Label, $"[{c.Key}]", $"Tones[{c.Key}]", c.Width)
                : Col.Text(c.Label, $"[{c.Key}]", c.Width, star: c.Cell == TabCell.Device, mono: c.Cell == TabCell.Mono,
                    wrap: c.Width >= 240, sortBy: $"Keys[{c.Key}]"))
            .ToArray();
        // A state column sorts on its key too; Pill has no sortBy of its own.
        for (var i = 0; i < cols.Length; i++)
            if (columns[i].Cell == TabCell.State) cols[i] = new Col
            {
                Header = cols[i].Header, Path = cols[i].Path, TonePath = cols[i].TonePath, Kind = ColKind.Pill,
                Width = cols[i].Width, SortPath = $"Keys[{columns[i].Key}]",
            };

        var reportUrl = CurrentLink().ToWebUrl(ConfigManager.Instance.WebDashboardUrl);
        var table = new FilteredTable<TabGridRow>(def.Title, "{0} of {1} " + def.RowNoun, grid,
                (r, q) => r.Matches(q), cols, $"Search {def.RowNoun}...",
                def.Paged ? "No reported rows match these filters." : "No collected entries match these filters.")
            .WithQuery(_query[def.SearchParam])
            .WithRowLink(r => def.RowLink(r.Row, _query))
            .WithCsv($"{def.CsvName}-{DateTime.Now:yyyy-MM-dd}.csv", def.CsvHeaders(context).ToArray(), r => def.CsvRow(r.Row, context, reportUrl))
            .Build();
        table.Margin = new Thickness(0, 4, 0, 0);
        return table;
    }
}

/// <summary>A report row as the grid binds it: display text, sort key and tone, each by column key.</summary>
public sealed class TabGridRow
{
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly string _haystack;

    public TabGridRow(TabRow row, IEnumerable<TabColumn> columns)
    {
        Row = row;
        var keys = new Dictionary<string, SortKey>(StringComparer.Ordinal);
        var tones = new Dictionary<string, Tone>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var text = TabReports.Display(column, row);
            _text[column.Key] = text;
            keys[column.Key] = SortKey.Of(column, row);
            if (column.Cell == TabCell.State)
                tones[column.Key] = TabReports.Tone(text.Split(" (")[0]) switch
                {
                    "good" => Tone.Success,
                    "bad" => Tone.Error,
                    "info" => Tone.Info,
                    _ => Tone.Warning,
                };
        }
        Keys = new Lookup<SortKey>(keys, default);
        Tones = new Lookup<Tone>(tones, Tone.Neutral);
        _haystack = string.Join(" ", _text.Values.Append(row.Serial));
    }

    public TabRow Row { get; }

    public string this[string key] => _text.GetValueOrDefault(key) ?? "";

    /// <summary>The sort keys, for <c>SortMemberPath="Keys[column]"</c>.</summary>
    public Lookup<SortKey> Keys { get; }

    public Lookup<Tone> Tones { get; }

    public bool Matches(string query) => _haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

    public sealed class Lookup<T>(Dictionary<string, T> values, T fallback)
    {
        public T this[string key] => values.TryGetValue(key, out var v) ? v : fallback;
    }
}

internal static class AutomationPropertiesHelper
{
    /// <summary>A chip is a toggle to assistive technology: its name and whether it is on.</summary>
    public static void Name(FrameworkElement element, string name, bool pressed) =>
        System.Windows.Automation.AutomationProperties.SetName(element, pressed ? $"{name}, selected" : name);
}

internal static class TabReportDataExtensions
{
    /// <summary>The count the caption compares against: the API's total for a paged report, else the rows.</summary>
    public static int Paged(this TabReportData data, TabReportDef def) => def.Paged ? data.Total : data.Rows.Count;
}
