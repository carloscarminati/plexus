using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Model;

// Strategy (b): the universal safety net. For plain prose or non-cooperating
// models, lift obvious structures out of text — fenced code -> code, markdown
// tables -> table, bare URLs -> link_card, everything else -> markdown.
// Lossy but always works.
public static partial class FallbackParser
{
    [GeneratedRegex(@"^\s*```([\w+-]*)\s*$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^\s*https?://\S+\s*$")]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$")]
    private static partial Regex TableSeparatorRegex();

    public static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var markdownBuffer = new StringBuilder();

        void FlushMarkdown()
        {
            var md = markdownBuffer.ToString().Trim();
            if (md.Length > 0)
                blocks.Add(new MarkdownBlock { Text = md });
            markdownBuffer.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Fenced code block.
            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                FlushMarkdown();
                var language = fence.Groups[1].Value;
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !FenceRegex().IsMatch(lines[i]))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                blocks.Add(new CodeBlock
                {
                    Language = string.IsNullOrEmpty(language) ? "text" : language,
                    Code = code.ToString().TrimEnd('\n'),
                });
                continue;
            }

            // Markdown table: a header row, a separator row, then data rows.
            if (LooksLikeTableRow(line) && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1]))
            {
                FlushMarkdown();
                var header = SplitTableRow(line);
                i += 2; // skip header + separator
                var rows = new List<Dictionary<string, JsonElement>>();
                while (i < lines.Length && LooksLikeTableRow(lines[i]))
                {
                    var cells = SplitTableRow(lines[i]);
                    var row = new Dictionary<string, JsonElement>();
                    for (var c = 0; c < header.Count; c++)
                    {
                        var key = ColumnKey(header[c], c);
                        var value = c < cells.Count ? cells[c] : "";
                        row[key] = JsonSerializer.SerializeToElement(value);
                    }
                    rows.Add(row);
                    i++;
                }
                i--; // for-loop will advance

                var columns = new List<TableColumn>();
                for (var c = 0; c < header.Count; c++)
                    columns.Add(new TableColumn { Key = ColumnKey(header[c], c), Label = header[c] });

                blocks.Add(new TableBlock { Columns = columns, Rows = rows });
                continue;
            }

            // Bare URL on its own line.
            if (BareUrlRegex().IsMatch(line))
            {
                FlushMarkdown();
                blocks.Add(new LinkCardBlock { Url = line.Trim() });
                continue;
            }

            markdownBuffer.Append(line).Append('\n');
        }

        FlushMarkdown();

        if (blocks.Count == 0)
            blocks.Add(new MarkdownBlock { Text = text.Trim() });

        return blocks;
    }

    private static bool LooksLikeTableRow(string line)
    {
        var t = line.Trim();
        return t.Contains('|') && t.Length > 1 && !TableSeparatorRegex().IsMatch(line);
    }

    private static List<string> SplitTableRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    private static string ColumnKey(string label, int index)
    {
        var key = Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        return string.IsNullOrEmpty(key) ? $"col{index}" : key;
    }
}
