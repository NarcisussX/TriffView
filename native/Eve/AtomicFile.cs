using System.IO;
using System.Text;

namespace TriffView.Eve;

internal static class AtomicFile
{
    public static void WriteText(string path, string contents, bool keepBackup = true)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("File path needs a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false, throwOnInvalidBytes: true)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, keepBackup ? backup : null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
                if (keepBackup) File.Copy(path, backup, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static string? ReadPrimaryOrBackup(string path, long maxBytes, out string warning)
    {
        warning = string.Empty;
        if (!File.Exists(path)) return null;

        try
        {
            return ReadBoundedText(path, maxBytes);
        }
        catch (Exception primaryError) when (IsFileFailure(primaryError))
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            try
            {
                warning = $"Could not read {Path.GetFileName(path)}; recovered its last-known-good backup. {primaryError.Message}";
                return ReadBoundedText(backup, maxBytes);
            }
            catch (Exception backupError) when (IsFileFailure(backupError))
            {
                throw new IOException($"Could not read {Path.GetFileName(path)} or its backup. Primary: {primaryError.Message} Backup: {backupError.Message}", backupError);
            }
        }
    }

    public static string PreserveCorrupt(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        var directory = Path.GetDirectoryName(path)!;
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(directory, $"{Path.GetFileName(path)}.corrupt-{stamp}");
        File.Move(path, destination);
        return destination;
    }

    public static string ReadBoundedText(string path, long maxBytes)
    {
        if (maxBytes is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan);
        if (stream.Length > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {maxBytes:N0}-byte limit.");

        using var contents = new MemoryStream(stream.Length > 0 ? checked((int)stream.Length) : 0);
        var buffer = new byte[16_384];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (contents.Length + read > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {maxBytes:N0}-byte limit.");
            contents.Write(buffer, 0, read);
        }

        var bytes = contents.GetBuffer().AsSpan(0, checked((int)contents.Length));
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static void RestoreBackup(string path, long maxBytes)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup)) throw new FileNotFoundException("No last-known-good backup exists.", backup);
        var contents = ReadBoundedText(backup, maxBytes);
        WriteText(path, contents, keepBackup: false);
    }

    private static bool IsFileFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // A same-directory temp file is recoverable and ignored by every reader.
        }
    }
}
