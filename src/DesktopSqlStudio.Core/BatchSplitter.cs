using System.Text;

namespace DesktopSqlStudio.Core;

/// <summary>Splits a script into batches on lines containing only GO, like SSMS does.</summary>
public static class BatchSplitter
{
    public static List<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var inBlockComment = false;

        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (!inBlockComment && IsGoLine(line))
            {
                Flush();
                continue;
            }

            current.AppendLine(line);
            inBlockComment = UpdateBlockCommentState(line, inBlockComment);
        }
        Flush();
        return batches;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0) batches.Add(text);
            current.Clear();
        }
    }

    private static bool IsGoLine(string line)
    {
        var t = line.Trim();
        if (t.Length < 2 || !t.StartsWith("GO", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = t[2..].Trim();
        // "GO", "GO;", or "GO 5" (repeat count is not supported; treated as a single GO)
        return rest.Length == 0 || rest == ";" || rest.All(char.IsDigit);
    }

    // Tracks /* ... */ across lines so a GO inside a block comment is not a separator.
    // Ignores string literals; good enough for batch splitting.
    private static bool UpdateBlockCommentState(string line, bool inComment)
    {
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (!inComment && line[i] == '-' && line[i + 1] == '-') break;
            if (!inComment && line[i] == '/' && line[i + 1] == '*') { inComment = true; i++; }
            else if (inComment && line[i] == '*' && line[i + 1] == '/') { inComment = false; i++; }
        }
        return inComment;
    }
}
