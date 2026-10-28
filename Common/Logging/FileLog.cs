using System.Text;

namespace Common.Logging;

public sealed class FileLog : IDisposable
{
    private readonly object _gate = new object();
    private readonly TextWriter _originalOutput;
    private readonly TextWriter _originalError;
    private readonly StreamWriter _file;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private int _disposed;
    private int _fileError;

    public bool HasFileError => Volatile.Read(ref _fileError) != 0;

    public FileLog(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _originalOutput = Console.Out;
        _originalError = Console.Error;
        _file = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        { AutoFlush = true };
        Console.SetOut(new TeeWriter(this, _originalOutput));
        Console.SetError(new TeeWriter(this, _originalError));
        _output = Console.Out;
        _error = Console.Error;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (ReferenceEquals(Console.Out, _output)) Console.SetOut(_originalOutput);
        if (ReferenceEquals(Console.Error, _error)) Console.SetError(_originalError);
        lock (_gate)
        {
            try { _file.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { FileFailed(error); }
        }
    }

    private void Write(TextWriter original, string? value, bool newLine)
    {
        lock (_gate)
        {
            try { if (newLine) original.WriteLine(value); else original.Write(value); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            if (_disposed != 0 || _fileError != 0) return;
            try { if (newLine) _file.WriteLine(value); else _file.Write(value); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { FileFailed(error); }
        }
    }

    private void FileFailed(Exception error)
    {
        if (Interlocked.Exchange(ref _fileError, 1) != 0) return;
        try { _originalError.WriteLine($"[FileLog] 파일 기록 중단: {error.GetType().Name}. 콘솔 기록을 계속합니다."); }
        catch (Exception fallbackError) when (fallbackError is IOException or ObjectDisposedException) { }
    }

    private sealed class TeeWriter(FileLog owner, TextWriter original) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value) => owner.Write(original, value.ToString(), false);
        public override void Write(string? value) => owner.Write(original, value, false);
        public override void WriteLine(string? value) => owner.Write(original, value, true);
        public override void Flush()
        {
            lock (owner._gate)
            {
                try { original.Flush(); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                if (owner._disposed != 0 || owner._fileError != 0) return;
                try { owner._file.Flush(); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { owner.FileFailed(error); }
            }
        }
    }
}
