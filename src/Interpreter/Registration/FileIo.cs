namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Registers File, Directory, and Path namespaces for file I/O operations.</summary>
    private void RegisterFileFunctions()
    {
        // A curated namespace answers the CLR surface BEHIND it too (TryInvokeClrNamespaceMember): the members
        // registered below always win, and a name they do not hold is looked up on the type, so
        // File.ReadAllTextAsync, File.GetLastWriteTime or Directory.GetFiles work without this file restating a
        // 109-member surface. The path rule and the message map travel with the fallback, which is why it is the
        // interpreter's rather than the namespace's own.
        Namespace.ClrMemberFallback ClrOf(Type clrType) => (string name, object?[] args, int line, int column,
            out object? result) => TryInvokeClrNamespaceMember(clrType, name, args, line, column, out result);

        // ── File namespace ────────────────────────────────────────────────
        var fileNs = new Namespace(_comparer);
        fileNs.ClrFallback = ClrOf(typeof(System.IO.File));

        fileNs.AddMethod("Exists", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.Exists expects a file path.");
            return File.Exists(ResolvePath(path));
        });

        fileNs.AddMethod("Delete", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.Delete expects a file path.");
            File.Delete(ResolvePath(path));
            return null;
        });

        fileNs.AddMethod("Copy", args =>
        {
            if (args.Length < 2 || args[0] is not string src || args[1] is not string dst)
                throw new ScriptException("File.Copy expects (source, dest[, overwrite]).");
            var overwrite = args.Length > 2 && args[2] is bool and true;
            File.Copy(ResolvePath(src), ResolvePath(dst), overwrite);
            return null;
        });

        fileNs.AddMethod("Move", args =>
        {
            if (args.Length < 2 || args[0] is not string src || args[1] is not string dst)
                throw new ScriptException("File.Move expects (source, dest).");
            File.Move(ResolvePath(src), ResolvePath(dst), overwrite: false);
            return null;
        });

        fileNs.AddMethod("ReadAllText", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.ReadAllText expects a file path.");
            return File.ReadAllText(ResolvePath(path));
        });

        fileNs.AddMethod("ReadAllLines", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.ReadAllLines expects a file path.");
            var lines = File.ReadAllLines(ResolvePath(path));
            var result = new ScriptBoxedList(new TypeInfo(TokenType.String, false, 0, 0, null));
            foreach (var line in lines) result.AddRaw(line);
            return result;
        });

        fileNs.AddMethod("WriteAllText", args =>
        {
            if (args.Length < 2 || args[0] is not string path || args[1] is not string content)
                throw new ScriptException("File.WriteAllText expects (path, content).");
            File.WriteAllText(ResolvePath(path), content);
            return null;
        });

        fileNs.AddMethod("AppendAllText", args =>
        {
            if (args.Length < 2 || args[0] is not string path || args[1] is not string content)
                throw new ScriptException("File.AppendAllText expects (path, content).");
            File.AppendAllText(ResolvePath(path), content);
            return null;
        });

        fileNs.AddMethod("ReadAllBytes", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.ReadAllBytes expects a file path.");
            return File.ReadAllBytes(ResolvePath(path));
        });

        fileNs.AddMethod("WriteAllBytes", args =>
        {
            if (args.Length < 2 || args[0] is not string path)
                throw new ScriptException("File.WriteAllBytes expects (path, bytes).");
            // Accept both byte[] and Array
            byte[] bytes;
            if (args[1] is byte[] ba) bytes = ba;
            else if (args[1] is Array arr) { bytes = new byte[arr.Length]; for (var i = 0; i < arr.Length; i++) bytes[i] = Convert.ToByte(arr.GetValue(i)!); }
            else throw new ScriptException("File.WriteAllBytes expects a byte array as second argument.");
            File.WriteAllBytes(ResolvePath(path), bytes);
            return null;
        });

        fileNs.AddMethod("GetSize", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.GetSize expects a file path.");
            return new FileInfo(ResolvePath(path)).Length;
        });

        // ── File.OpenRead(path), line-by-line streaming reader ──────────────
        fileNs.AddMethod("OpenRead", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.OpenRead expects a file path.");
            return new FileStreamReader(ResolvePath(path));
        });

        // ── File.OpenWrite(path), write handle with WriteLine/Close ────────
        fileNs.AddMethod("OpenWrite", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.OpenWrite expects a file path.");
            var append = args.Length > 1 && args[1] is bool and true;
            return new FileStreamWriter(ResolvePath(path), append);
        });

        // ── File.OpenWriteBytes(path), a BYTE handle: the save side of a chunked upload ──
        fileNs.AddMethod("OpenWriteBytes", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("File.OpenWriteBytes expects a file path.");
            var append = args.Length > 1 && args[1] is bool and true;
            return new FileByteWriter(ResolvePath(path), append);
        });

        CurrentFrame.Scopes.Define("File", fileNs);

        // ── Directory namespace ───────────────────────────────────────────
        var dirNs = new Namespace(_comparer);
        dirNs.ClrFallback = ClrOf(typeof(System.IO.Directory));

        dirNs.AddMethod("Exists", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Directory.Exists expects a path.");
            return Directory.Exists(ResolvePath(path));
        });

        dirNs.AddMethod("CreateDirectory", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Directory.CreateDirectory expects a path.");
            Directory.CreateDirectory(ResolvePath(path));
            return null;
        });

        dirNs.AddMethod("Delete", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Directory.Delete expects a path.");
            var recursive = args.Length > 1 && args[1] is bool and true;
            Directory.Delete(ResolvePath(path), recursive);
            return null;
        });

        dirNs.AddMethod("GetFiles", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Directory.GetFiles expects a path.");
            var pattern = args.Length > 1 && args[1] is string p ? p : "*";
            var files = Directory.GetFiles(ResolvePath(path), pattern);
            var result = new ScriptBoxedList(new TypeInfo(TokenType.String, false, 0, 0, null));
            foreach (var f in files) result.AddRaw(f);
            return result;
        });

        dirNs.AddMethod("GetDirectories", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Directory.GetDirectories expects a path.");
            var dirs = Directory.GetDirectories(ResolvePath(path));
            var result = new ScriptBoxedList(new TypeInfo(TokenType.String, false, 0, 0, null));
            foreach (var d in dirs) result.AddRaw(d);
            return result;
        });

        dirNs.AddMethod("GetCurrentDirectory", _ => Directory.GetCurrentDirectory());

        CurrentFrame.Scopes.Define("Directory", dirNs);

        // ── Path namespace ────────────────────────────────────────────────
        var pathNs = new Namespace(_comparer);
        pathNs.ClrFallback = ClrOf(typeof(System.IO.Path));

        pathNs.AddMethod("Combine", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Path.Combine expects at least 2 path segments.");
            var parts = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                parts[i] = args[i]?.ToString() ?? "";
            return Path.Combine(parts);
        });

        pathNs.AddMethod("GetFileName", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.GetFileName expects a path.");
            return Path.GetFileName(path);
        });

        pathNs.AddMethod("GetFileNameWithoutExtension", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.GetFileNameWithoutExtension expects a path.");
            return Path.GetFileNameWithoutExtension(path);
        });

        pathNs.AddMethod("GetDirectoryName", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.GetDirectoryName expects a path.");
            return Path.GetDirectoryName(path);
        });

        pathNs.AddMethod("GetExtension", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.GetExtension expects a path.");
            return Path.GetExtension(path);
        });

        pathNs.AddMethod("ChangeExtension", args =>
        {
            if (args.Length < 2 || args[0] is not string path || args[1] is not string ext)
                throw new ScriptException("Path.ChangeExtension expects (path, newExtension).");
            return Path.ChangeExtension(path, ext);
        });

        pathNs.AddMethod("HasExtension", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.HasExtension expects a path.");
            return Path.HasExtension(path);
        });

        pathNs.AddMethod("GetFullPath", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Path.GetFullPath expects a path.");
            return Path.GetFullPath(path);
        });

        CurrentFrame.Scopes.Define("Path", pathNs);
    }
}

/// <summary>Line-by-line streaming file reader. Returned by File.OpenRead().</summary>
public sealed class FileStreamReader : IDisposable
{
    private StreamReader? _reader;
    private readonly string _path;

    internal FileStreamReader(string path)
    {
        _path = path;
        _reader = new StreamReader(File.OpenRead(path));
    }

    /// <summary>Read the next line, or null at EOF.</summary>
    public string? ReadLine()
    {
        return _reader?.ReadLine();
    }

    /// <summary>Read all remaining lines as an array, WITHOUT their terminators and with no phantom last line.
    /// It asks the engine's one boundary rule (<see cref="LineSplitter"/>: CR, LF or CRLF end a line), which is what
    /// <c>File.ReadAllLines</c> answers too. A split on '\n' alone did not: it kept a CRLF line's carriage return, read
    /// a CR-only file as ONE line, and counted a trailing break as an extra empty line (measured on the same four files:
    /// it answered 3/3/1/1 where ReadAllLines answers 2/2/2/1).</summary>
    public string[] ReadToEnd()
    {
        var text = _reader?.ReadToEnd() ?? "";
        if (text.Length == 0) return [];
        // ONE array, sized from the splitter itself, so nothing grows and nothing is copied twice: a List plus ToArray
        // measured 19.2% more bytes at 5,000 lines and 21.7% at 17,000, where it was also about twice as slow.
        var lines = new string[TextLines.Lines(text)];
        var at = 0;
        foreach (var line in new LineSplitter(text)) lines[at++] = line.Text.ToString();
        return lines;
    }

    /// <summary>Close the reader.</summary>
    public void Close()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Close();
    public override string ToString() => ScriptNameOf(typeof(FileStreamReader)) ?? "FileStreamReader";
}

/// <summary>Write handle for streaming file output. Returned by File.OpenWrite().</summary>
public sealed class FileStreamWriter : IDisposable
{
    private StreamWriter? _writer;
    private readonly string _path;

    internal FileStreamWriter(string path, bool append)
    {
        _path = path;
        _writer = new StreamWriter(File.Open(path, append ? FileMode.Append : FileMode.Create));
    }

    /// <summary>Write a line of text.</summary>
    public void WriteLine(string text)
    {
        _writer?.WriteLine(text);
    }

    /// <summary>Write text without a newline.</summary>
    public void Write(string text)
    {
        _writer?.Write(text);
    }

    /// <summary>Flush buffered data to disk.</summary>
    public void Flush()
    {
        _writer?.Flush();
    }

    /// <summary>Close the writer.</summary>
    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Close();
    public override string ToString() => ScriptNameOf(typeof(FileStreamWriter)) ?? "FileStreamWriter";
}

/// <summary>Write handle for BINARY file output, returned by <c>File.OpenWriteBytes(path [, append])</c>: the save side
/// of a chunked upload, where the bytes must never become text.
///
/// It exists beside <see cref="FileStreamWriter"/> rather than as one more overload on it, because that type wraps a
/// <c>StreamWriter</c> and therefore an ENCODING: writing a chunk of a PNG through it would transcode every byte above
/// 0x7F and pad a lone surrogate, which is exactly the corruption a binary handle is for. One <c>FileStream</c>, one
/// <c>Write</c> per chunk, so a 2 GB upload costs one open handle and no script value bigger than one chunk.
///
/// The handle is the CALLER's to close: <c>Close()</c> by name or <c>using</c>/<c>Dispose()</c> releases it, and
/// <c>using</c> is what a script writes when the whole write is one block. It is not thread safe, like the stream it
/// wraps.</summary>
public sealed class FileByteWriter : IDisposable
{
    private FileStream? _stream;
    private readonly string _path;

    internal FileByteWriter(string path, bool append)
    {
        _path = path;
        // A 64 KB buffer: the chunk size a script reads its request in, so one Write is one buffered copy at most.
        _stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
            64 * 1024);
    }

    /// <summary>Append one chunk of bytes. The whole array is written, so a short write is a failure rather than a
    /// silently smaller file (the platform's own guarantee for a FileStream, restated because the caller's message
    /// about "bytes saved" depends on it).</summary>
    public void Write(byte[] data)
    {
        if (data == null) throw new ScriptException("FileByteWriter.Write expects the bytes to write, not null.");
        var stream = _stream ?? throw new ScriptException($"this byte writer for '{_path}' was closed: nothing more "
            + "can be written through it.");
        stream.Write(data, 0, data.Length);
    }

    /// <summary>Write a slice of a chunk, for the tail of a body read into a buffer larger than what arrived.</summary>
    public void Write(byte[] data, int offset, int count)
    {
        if (data == null) throw new ScriptException("FileByteWriter.Write expects the bytes to write, not null.");
        var stream = _stream ?? throw new ScriptException($"this byte writer for '{_path}' was closed: nothing more "
            + "can be written through it.");
        stream.Write(data, offset, count);
    }

    /// <summary>The file's length NOW, in bytes: what a caller checks against the size it expected once the last
    /// chunk is written. It flushes nothing (the FileStream's own buffer is pushed by <see cref="Flush"/> or by
    /// closing), and it answers 0 for a closed handle rather than throwing, so a report can still be built.</summary>
    public long Length => _stream?.Length ?? 0;

    /// <summary>Push buffered bytes to the file. Closing does this too, so an ordinary upload needs only Close.</summary>
    public void Flush() => _stream?.Flush();

    /// <summary>Close the handle. Closing twice is a no-op, so a `catch` that closes and rethrows cannot fail on the
    /// way out.</summary>
    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Close();
    public override string ToString() => ScriptNameOf(typeof(FileByteWriter)) ?? "FileByteWriter";
}
