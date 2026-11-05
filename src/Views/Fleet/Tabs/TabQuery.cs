using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ReportMate.App.Services;

namespace ReportMate.App.Views.Fleet;

/// <summary>
/// The state of one module tab report, held the way the web holds it: as the query
/// string of <c>/&lt;module&gt;?tab=&lt;key&gt;&amp;...</c>. Everything a reader chooses lives
/// here, so the link the page emits reopens the same report in this app, the Mac app
/// or the browser, and a link copied from the web opens the same view here.
/// </summary>
public sealed class TabQuery
{
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    public TabQuery() { }

    public TabQuery(IEnumerable<KeyValuePair<string, string?>> values)
    {
        foreach (var (key, value) in values)
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) _values[key] = value.Trim();
    }

    /// <summary>The query a link carries, without the tab itself.</summary>
    public static TabQuery From(DeepLink? link)
    {
        var query = new TabQuery();
        if (link is null) return query;
        foreach (string? key in link.Query.AllKeys)
            if (key is not null && key != "tab" && link[key] is { } value) query._values[key] = value.Trim();
        return query;
    }

    public string? this[string key] => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public bool Has(string key) => this[key] is not null;

    public IEnumerable<KeyValuePair<string, string>> Values => _values;

    /// <summary>
    /// A list parameter. Exact names travel as a JSON array, because names can hold
    /// commas; everything else is a plain comma list. An unreadable array is an empty
    /// selection, never a guess.
    /// </summary>
    public IReadOnlyList<string> List(string key)
    {
        var raw = this[key];
        if (raw is null) return [];
        if (raw.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
                return doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (JsonException) { return []; }
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A copy with the given keys set, or removed where the value is null or blank.</summary>
    public TabQuery With(params (string Key, string? Value)[] patch)
    {
        var next = new TabQuery(_values.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        foreach (var (key, value) in patch)
        {
            if (string.IsNullOrWhiteSpace(value)) next._values.Remove(key);
            else next._values[key] = value.Trim();
        }
        return next;
    }

    /// <summary>Set a list parameter, as JSON when <paramref name="json"/> says the names may hold commas.</summary>
    public TabQuery WithList(string key, IEnumerable<string> values, bool json)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList();
        return With((key, list.Count == 0 ? null : json ? JsonSerializer.Serialize(list) : string.Join(",", list)));
    }

    /// <summary>Add the value to a list parameter, or take it out if it is already there.</summary>
    public TabQuery Toggle(string key, string value, bool json = false)
    {
        var list = List(key).ToList();
        if (!list.Remove(value)) list.Add(value);
        return WithList(key, list, json);
    }

    /// <summary>The API query string: only the keys the report sends, in a stable order.</summary>
    public static string Encode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    /// <summary>The link that reopens this view: <c>/&lt;module&gt;?tab=&lt;tab&gt;&amp;...</c>.</summary>
    public DeepLink Link(string module, string tab) =>
        DeepLink.For(module, null, new[] { ("tab", (string?)tab) }
            .Concat(_values.Select(kv => (kv.Key, (string?)kv.Value))).ToArray());

    public override string ToString() => Encode(_values.Select(kv => (kv.Key, kv.Value)));
}
