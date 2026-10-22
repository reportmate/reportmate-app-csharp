// Explicit usings: this file is also compiled into the test project.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReportMate.App.Services;

/// <summary>Comma-separated values for a report's Export, quoted the way spreadsheets read them.</summary>
public static class Csv
{
    /// <summary>
    /// One cell, the one helper every export in the app writes through. A value a
    /// spreadsheet would run as a formula -- starting with =, +, -, @, a tab or a
    /// carriage return -- is prefixed with ' and quoted, since the values come from
    /// what devices reported. Any other value is quoted when it holds a comma, quote
    /// or line break, with quotes doubled.
    /// </summary>
    public static string Field(string? value)
    {
        var v = value ?? "";
        var formula = v.Length > 0 && "=+-@\t\r".Contains(v[0]);
        if (formula) v = "'" + v;
        return formula || v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Line(IEnumerable<string?> fields) => string.Join(",", fields.Select(Field));

    public static string Document(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Line(headers)).Append("\r\n");
        foreach (var row in rows) sb.Append(Line(row)).Append("\r\n");
        return sb.ToString();
    }
}
