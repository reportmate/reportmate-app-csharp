using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>How a column's value reads in a cell.</summary>
public enum TabCell { Text, Mono, Time, State, Device, Number }

/// <summary>A value a selection or choice can take, with the device count the API gave it.</summary>
public sealed record TabOption(string Value, string Label, int? Count = null);

/// <summary>
/// One table column. <see cref="Value"/> is the raw value, which the CSV and sorting
/// use; the cell shows <see cref="Display"/> when set. A value that is null reads as
/// <see cref="Empty"/>, which defaults to the web's wording for the cell type.
/// </summary>
public sealed record TabColumn(string Key, string Label, Func<TabRow, string?> Value, TabCell Cell = TabCell.Text)
{
    /// <summary>Item kinds this column describes; other rows read "Not applicable".</summary>
    public string[]? AppliesTo { get; init; }

    public Func<TabRow, string?>? Display { get; init; }

    /// <summary>What the column sorts on when that is not the text: a number, a time, a rank.</summary>
    public Func<TabRow, double?>? Rank { get; init; }

    public string? Empty { get; init; }

    public double Width { get; init; } = 150;

    public bool Applies(TabRow row) => AppliesTo is null || AppliesTo.Contains(row.Kind, StringComparer.Ordinal);
}

public enum ChoiceMode
{
    /// <summary>Any number of values, as a comma list; "All" clears it.</summary>
    Multi,
    /// <summary>Exactly one value; the default value is the parameter being absent.</summary>
    Single,
    /// <summary>One switch that sets the parameter to its value or clears it.</summary>
    Toggle,
}

/// <summary>A filter group: the web's row of choice pills under a label.</summary>
public sealed record TabChoice(string Param, string Label, ChoiceMode Mode = ChoiceMode.Multi)
{
    public IReadOnlyList<TabOption> Options { get; init; } = [];

    /// <summary>Choices read from the selections catalog, the report, or the other selections.</summary>
    public Func<TabContext, IReadOnlyList<TabOption>>? From { get; init; }

    public string? Default { get; init; }

    /// <summary>The label of the choice that clears a multi-select group.</summary>
    public string AllLabel { get; init; } = "All";

    /// <summary>The prefix of the API summary counter for each choice; "" for the bare value.</summary>
    public string? SummaryKey { get; init; }

    /// <summary>Applied to the loaded rows rather than sent to the API.</summary>
    public Func<TabRow, string, TabQuery, bool>? Client { get; init; }

    public Func<TabContext, bool>? Visible { get; init; }

    /// <summary>Shown above the table beside the row count, as the web shows its state pills.</summary>
    public bool Prominent { get; init; }

    /// <summary>Parameters a change to this one clears.</summary>
    public string[] Resets { get; init; } = [];

    public IReadOnlyList<TabOption> Resolve(TabContext context) => From?.Invoke(context) ?? Options;
}

/// <summary>An inventory-backed picker: the collected values, searchable, any number selected.</summary>
public sealed record TabSelection(string Param, string Label, Func<TabContext, IReadOnlyList<TabOption>> Options)
{
    /// <summary>Selected values travel as a JSON array because names can hold commas.</summary>
    public bool Json { get; init; } = true;

    public Func<TabContext, bool>? Visible { get; init; }

    public string[] Resets { get; init; } = [];

    public int? Max { get; init; }

    /// <summary>The selections an old link names by substring rather than exactly.</summary>
    public Func<TabContext, IReadOnlyList<string>>? Selected { get; init; }

    public IReadOnlyList<string> Current(TabContext context) =>
        Selected?.Invoke(context) is { } legacy ? legacy : context.Query.List(Param);
}

/// <summary>What a definition's functions can see: the link state, the platform, the catalog and the data.</summary>
public sealed record TabContext(TabQuery Query, string Platform, JsonElement? Catalog = null, TabReportData? Data = null)
{
    /// <summary>The scope lists (<c>usages</c>, <c>rooms</c>, ...) from the catalog, or from the report when it carries them.</summary>
    public IReadOnlyList<string> Scope(string key) =>
        TabJson.Array(TabJson.Prop(Catalog, "options") ?? TabJson.Prop(Data?.Root, "options"), key)
            .Select(TabJson.Text).Where(s => s is not null).Select(s => s!).ToList();
}

/// <summary>A loaded report: the first page as the API sent it, and every row.</summary>
public sealed class TabReportData
{
    public TabReportData(JsonElement root, IEnumerable<JsonElement> rows)
    {
        Root = root.Clone();
        Rows = rows.Select(r => new TabRow(r, this)).ToList();
    }

    public JsonElement Root { get; }

    public List<TabRow> Rows { get; }

    public string? GeneratedAt => TabJson.Str(Root, "generatedAt");

    public DateTime Snapshot => TabJson.Time(GeneratedAt) ?? DateTime.UtcNow;

    public int Total => TabJson.Int(Root, "total") ?? Rows.Count;

    /// <summary>A summary counter, or null when the API did not send one.</summary>
    public int? Summary(string key, string? section = "summary") =>
        TabJson.Int(TabJson.Prop(Root, section ?? "summary"), key);
}

/// <summary>A response as the page's fetcher saw it.</summary>
public sealed record TabFetch(bool Ok, int? Status, string? Error, JsonElement? Body);

public sealed record TabLoad(TabReportData? Data, string? Error, bool NotFound = false)
{
    public bool Ok => Data is not null;
}

/// <summary>
/// A module tab report: where it reads, what it lets a reader choose, how its rows
/// read and what its CSV holds. One definition per web tab, so the Windows app
/// shows the same states, filters, columns and export as the browser.
/// </summary>
public sealed class TabReportDef
{
    public required string Module { get; init; }
    public required string Tab { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string Footnote { get; init; } = "";
    public required string Api { get; init; }
    public string? FiltersApi { get; init; }

    /// <summary>The parameters the selections catalog takes besides the platform.</summary>
    public string[] FiltersParams { get; init; } = TabReports.ScopeKeys.Append("includeArchived").ToArray();

    /// <summary>Whether the catalog is asked for one platform; some catalogs are fleet-wide and filtered here.</summary>
    public bool FiltersPlatform { get; init; } = true;

    public string RowsProperty { get; init; } = "rows";
    public bool Paged { get; init; } = true;
    public int PageSize { get; init; } = 1000;
    public int? MaxRows { get; init; }

    /// <summary>The URL parameters forwarded to the API; anything else is view state.</summary>
    public required string[] ApiParams { get; init; }

    /// <summary>How the API names a platform: the web sends "Windows", "macOS" or "all" to most reports.</summary>
    public Func<string, string> PlatformValue { get; init; } = p => p;

    public Func<TabQuery, IEnumerable<(string Key, string Value)>>? ExtraApi { get; init; }

    public Func<TabQuery, bool> HasQuery { get; init; } = _ => true;
    public string Prompt { get; init; } = "";

    public IReadOnlyList<TabSelection> Selections { get; init; } = [];
    public IReadOnlyList<TabChoice> Choices { get; init; } = [];
    public required IReadOnlyList<TabColumn> Columns { get; init; }

    /// <summary>Columns this context can show; others are left out of the table and the chooser.</summary>
    public Func<TabContext, TabColumn, bool>? Available { get; init; }
    public Func<TabContext, IReadOnlyList<string>> DefaultColumns { get; init; } = _ => [];
    public bool ColumnChooser { get; init; } = true;

    public string SortParam { get; init; } = "sort";
    public string DirParam { get; init; } = "direction";
    public string? DefaultSort { get; init; } = "deviceName";
    public bool DefaultDescending { get; init; }

    public string SearchParam { get; init; } = "search";

    public Func<TabRow, string>? RowKey { get; init; }
    public Func<TabRow, TabQuery, DeepLink?> RowLink { get; init; } = (r, _) => TabReports.DeviceLink(r, "system");

    public required string CsvName { get; init; }
    public required Func<TabContext, IReadOnlyList<string>> CsvHeaders { get; init; }
    public required Func<TabRow, TabContext, string, IEnumerable<string?>> CsvRow { get; init; }

    /// <summary>The caption under the toolbar: coverage, rows, devices not reporting.</summary>
    public Func<TabContext, int, string?>? Caption { get; init; }

    /// <summary>The platform this report does not apply to, and what to say there.</summary>
    public string? NotApplicableOn { get; init; }
    public string NotApplicableText { get; init; } = "";

    public string RowNoun { get; init; } = "rows";

    public string NotFoundText { get; init; } = "This report is not available on this server yet.";

    // ── Requests ──────────────────────────────────────────────────────────

    public IEnumerable<(string Key, string Value)> ApiPairs(TabQuery query, string platform)
    {
        var client = Choices.Where(c => c.Client is not null).Select(c => c.Param).ToHashSet(StringComparer.Ordinal);
        foreach (var key in ApiParams)
            if (!client.Contains(key) && query[key] is { } value) yield return (key, value);
        yield return ("platform", PlatformValue(platform));
        if (ExtraApi is not null)
            foreach (var pair in ExtraApi(query)) yield return pair;
    }

    public string Path(TabQuery query, string platform, int offset = 0)
    {
        var pairs = ApiPairs(query, platform).ToList();
        if (Paged)
        {
            pairs.Add(("offset", offset.ToString(CultureInfo.InvariantCulture)));
            pairs.Add(("limit", PageSize.ToString(CultureInfo.InvariantCulture)));
        }
        return Api + "?" + TabQuery.Encode(pairs);
    }

    public string? OptionsPath(TabQuery query, string platform)
    {
        if (FiltersApi is null) return null;
        var pairs = new List<(string, string)>();
        if (FiltersPlatform) pairs.Add(("platform", PlatformValue(platform)));
        foreach (var key in FiltersParams)
            if (query[key] is { } value) pairs.Add((key, value));
        return pairs.Count == 0 ? FiltersApi : FiltersApi + "?" + TabQuery.Encode(pairs);
    }

    /// <summary>
    /// Every page of the report, through bounded requests. A report that changes
    /// between pages is an error, never a stitched-together table.
    /// </summary>
    public async Task<TabLoad> LoadAsync(TabQuery query, string platform, Func<string, Task<TabFetch>> fetch, Action<int, int>? progress = null)
    {
        var rows = new List<JsonElement>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? first = null;
        int? total = null;
        do
        {
            var response = await fetch(Path(query, platform, rows.Count));
            if (!response.Ok || response.Body is not { ValueKind: JsonValueKind.Object } body)
                return response.Status == 404
                    ? new TabLoad(null, NotFoundText, NotFound: true)
                    : new TabLoad(null, response.Error ?? "The report could not be loaded. Please retry.");

            if (TabJson.Prop(body, RowsProperty) is not { ValueKind: JsonValueKind.Array } page)
                return new TabLoad(null, "The report could not be read: it has no rows.");
            if (TabJson.Prop(body, "queryRequired") is { ValueKind: JsonValueKind.True})
                return new TabLoad(new TabReportData(body, []), null);

            if (!Paged)
                return new TabLoad(new TabReportData(body, page.EnumerateArray()), null);

            var pageTotal = TabJson.Int(body, "total");
            var offset = TabJson.Int(body, "offset") ?? rows.Count;
            if (pageTotal is not >= 0 || offset != rows.Count || (total is not null && pageTotal != total))
                return new TabLoad(null, "The report changed while loading. Please retry.");
            if (MaxRows is { } max && pageTotal > max)
                return new TabLoad(null, $"This report has {pageTotal:N0} rows. Select fewer items or narrow the filters.");
            total = pageTotal;
            first ??= body.Clone();

            var items = page.EnumerateArray().ToList();
            if (items.Count == 0 && rows.Count < total)
                return new TabLoad(null, "The report returned an incomplete page. Please retry.");
            foreach (var item in items)
            {
                if (RowKey is not null)
                {
                    var key = RowKey(new TabRow(item));
                    if (string.IsNullOrEmpty(new TabRow(item).Serial) || !keys.Add(key))
                        return new TabLoad(null, "The report changed while loading. Please retry.");
                }
                rows.Add(item.Clone());
            }
            if (rows.Count > total)
                return new TabLoad(null, "The report changed while loading. Please retry.");
            progress?.Invoke(rows.Count, total.Value);
        } while (rows.Count < total);

        return new TabLoad(new TabReportData(first!.Value, rows), null);
    }

    // ── The view ─────────────────────────────────────────────────────────

    public IReadOnlyList<TabColumn> AvailableColumns(TabContext context) =>
        Columns.Where(c => Available?.Invoke(context, c) ?? true).ToList();

    /// <summary>The visible columns, in definition order; the device column is always there.</summary>
    public IReadOnlyList<TabColumn> VisibleColumns(TabContext context)
    {
        var available = AvailableColumns(context);
        if (!ColumnChooser) return available;
        var requested = context.Query.Has("columns") ? context.Query.List("columns") : DefaultColumns(context);
        return available.Where(c => c.Key == Columns[0].Key || requested.Contains(c.Key)).ToList();
    }

    public (string? Sort, bool Descending) SortOf(TabContext context)
    {
        var columns = VisibleColumns(context);
        var sort = context.Query[SortParam] is { } s && columns.Any(c => c.Key == s) ? s : DefaultSort;
        var dir = context.Query[DirParam];
        // A report with no default column (the CVE summary) runs every sort descending first.
        var descending = dir is "desc" || (dir is not "asc" && DefaultDescending && (DefaultSort is null || sort == DefaultSort));
        return (sort, descending);
    }

    /// <summary>The rows on screen and in the export: the client filters applied, sorted as the link says.</summary>
    public List<TabRow> ViewRows(TabContext context)
    {
        if (context.Data is null) return [];
        IEnumerable<TabRow> rows = context.Data.Rows;
        foreach (var choice in Choices.Where(c => c.Client is not null))
            if (context.Query[choice.Param] is { } value)
                rows = rows.Where(r => choice.Client!(r, value, context.Query));
        var (sort, descending) = SortOf(context);
        var column = sort is null ? null : Columns.FirstOrDefault(c => c.Key == sort);
        if (column is null) return rows.ToList();
        var keyed = rows.Select(r => (Row: r, Key: SortKey.Of(column, r))).ToList();
        keyed.Sort((a, b) => a.Key.CompareTo(b.Key, descending) is var c && c != 0 ? c
            : SortKey.Natural(a.Row.Serial, b.Row.Serial));
        return keyed.Select(k => k.Row).ToList();
    }

    public string Csv(TabContext context, IEnumerable<TabRow> rows, string reportUrl) =>
        ReportMate.App.Services.Csv.Document(CsvHeaders(context), rows.Select(r => CsvRow(r, context, reportUrl)));
}

/// <summary>A column's sort value: unknown and not-applicable values sort last in either direction.</summary>
public readonly record struct SortKey(double? Number, string? Text) : IComparable<SortKey>, IComparable
{
    public bool Missing => Number is null && string.IsNullOrEmpty(Text);

    public static SortKey Of(TabColumn column, TabRow row)
    {
        if (!column.Applies(row)) return default;
        if (column.Rank is { } rank) return new(rank(row), null);
        var value = column.Value(row);
        if (string.IsNullOrWhiteSpace(value)) return default;
        if (column.Cell == TabCell.Time)
            return TabJson.Time(value) is { } at ? new(at.Ticks, null) : new(null, value);
        if (column.Cell == TabCell.Number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return new(n, null);
        return new(null, value);
    }

    public int CompareTo(SortKey other, bool descending)
    {
        if (Missing) return other.Missing ? 0 : 1;
        if (other.Missing) return -1;
        var c = Number is { } a && other.Number is { } b ? a.CompareTo(b) : Natural(Text ?? Number?.ToString(CultureInfo.InvariantCulture), other.Text ?? other.Number?.ToString(CultureInfo.InvariantCulture));
        return descending ? -c : c;
    }

    public int CompareTo(SortKey other) => CompareTo(other, false);

    public int CompareTo(object? obj) => obj is SortKey other ? CompareTo(other) : 1;

    /// <summary>Case-insensitive, with runs of digits compared as numbers, as the web's collator does.</summary>
    public static int Natural(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; while (i < a.Length && char.IsDigit(a[i])) i++;
                var sj = j; while (j < b.Length && char.IsDigit(b[j])) j++;
                var x = a[si..i].TrimStart('0'); var y = b[sj..j].TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                var d = string.CompareOrdinal(x, y);
                if (d != 0) return d;
                continue;
            }
            var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (c != 0) return c;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
