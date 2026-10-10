// Explicit usings: this file is also compiled into the test project, which does not
// enable implicit usings.
using System;
using System.Collections.Generic;

namespace ReportMate.App.Services;

/// <summary>
/// Which device a typed query means, for opening one device on Return. A field
/// that equals the query beats one that starts with it, which beats one that only
/// contains it; among equals, the earlier field (name, then the identifiers) wins,
/// then the earlier device.
/// </summary>
public static class DeviceMatch
{
    /// <summary>
    /// How well <paramref name="fields"/> match <paramref name="query"/>: lower is
    /// better, null is no match. Blank fields are skipped.
    /// </summary>
    public static int? Score(string query, IReadOnlyList<string?> fields)
    {
        query = query.Trim();
        if (query.Length == 0) return null;

        int? best = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.IsNullOrWhiteSpace(field)) continue;

            int kind;
            if (field.Equals(query, StringComparison.OrdinalIgnoreCase)) kind = 0;
            else if (field.StartsWith(query, StringComparison.OrdinalIgnoreCase)) kind = 1;
            else if (field.Contains(query, StringComparison.OrdinalIgnoreCase)) kind = 2;
            else continue;

            var score = kind * fields.Count + i;
            if (best is null || score < best) best = score;
        }
        return best;
    }

    /// <summary>The item whose fields best match the query, or default when none does.</summary>
    public static T? Best<T>(IEnumerable<T> items, string query, Func<T, IReadOnlyList<string?>> fields)
    {
        T? best = default;
        int? bestScore = null;
        foreach (var item in items)
        {
            if (Score(query, fields(item)) is not { } score) continue;
            if (bestScore is null || score < bestScore)
            {
                best = item;
                bestScore = score;
            }
        }
        return best;
    }
}
