using System.Text;

namespace BMAP;

/// <summary>Completes a file beside its destination before replacing the previous version.</summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".bmap-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void WriteAllText(string path, string text, Encoding? encoding = null) => Write(path, stream =>
    {
        using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false), leaveOpen: true);
        writer.Write(text);
    });

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));
}
