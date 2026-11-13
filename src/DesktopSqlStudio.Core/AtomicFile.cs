using System.Text;

namespace DesktopSqlStudio.Core;

public static class AtomicFile
{
    /// <summary>
    /// Writes to a temp file, flushes to disk, then swaps it in. A crash mid-write
    /// leaves either the old file or the new file, never a half-written one.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
