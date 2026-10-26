using System.Globalization;
using System.Text;

namespace PARScope.Core.Data;

/// <summary>Small RFC 4180 reader: header-indexed rows, quoted fields, doubled quotes.</summary>
internal sealed class CsvReader
{
    private readonly TextReader _reader;
    private readonly Dictionary<string, int> _index;
    private string[] _row = [];

    public CsvReader(TextReader reader)
    {
        _reader = reader;
        var header = ReadRow() ?? throw new InvalidDataException("CSV has no header row");
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) _index.TryAdd(header[i].Trim(), i);
    }

    public bool Has(string column) => _index.ContainsKey(column);

    public bool Read()
    {
        var row = ReadRow();
        if (row == null) return false;
        _row = row;
        return true;
    }

    public string Get(string column) =>
        _index.TryGetValue(column, out int i) && i < _row.Length ? _row[i].Trim() : "";

    public double? GetDouble(string column) =>
        double.TryParse(Get(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private string[]? ReadRow()
    {
        var line = _reader.ReadLine();
        if (line == null) return null;
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        while (true)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            if (!quoted) break;
            // Quoted field spanning lines.
            var next = _reader.ReadLine();
            if (next == null) break;
            sb.Append('\n');
            line = next;
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
