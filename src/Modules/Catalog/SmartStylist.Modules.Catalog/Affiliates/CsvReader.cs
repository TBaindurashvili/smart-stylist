using System.Text;

namespace SmartStylist.Modules.Catalog.Affiliates;

/// <summary>A small RFC 4180 reader: quoted fields, doubled quotes and line breaks inside quotes.</summary>
internal static class CsvReader
{
    public static async IAsyncEnumerable<IReadOnlyList<string>> ReadAsync(TextReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var buffer = new char[8192];
        var pendingQuote = false;
        var sawAny = false;
        int read;

        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                sawAny = true;
                if (quoted)
                {
                    if (pendingQuote)
                    {
                        pendingQuote = false;
                        if (c == '"')
                        {
                            field.Append('"');
                            continue;
                        }

                        quoted = false;
                    }
                    else if (c == '"')
                    {
                        pendingQuote = true;
                        continue;
                    }
                    else
                    {
                        field.Append(c);
                        continue;
                    }
                }

                switch (c)
                {
                    case '"' when field.Length == 0:
                        quoted = true;
                        break;
                    case ',':
                        fields.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        fields.Add(field.ToString());
                        field.Clear();
                        yield return fields;
                        fields = [];
                        sawAny = false;
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
        }

        if (sawAny || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }
}
