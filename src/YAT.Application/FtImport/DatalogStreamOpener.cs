using System.IO.Compression;
using System.Text;

namespace YAT.Application.FtImport;

public enum DatalogOpenErrorCode
{
    FileNotFound,
    Unreadable,
    CorruptArchive,
    EmptyArchive,
    AmbiguousArchive,
    InvalidEncoding
}

public sealed class DatalogOpenException : Exception
{
    public DatalogOpenException(DatalogOpenErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public DatalogOpenException(DatalogOpenErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public DatalogOpenErrorCode ErrorCode { get; }
}

// Streaming datalog reader handle owning the underlying file stream, optional ZipArchive, entry stream, and StreamReader.
public sealed class OpenedDatalogStream : IDisposable
{
    private readonly IDisposable? _archive;
    private readonly Stream _rawStream;
    private bool _disposed;

    internal OpenedDatalogStream(
        string sourcePath,
        string? archiveEntryName,
        TextReader reader,
        Stream rawStream,
        IDisposable? archive)
    {
        SourcePath = sourcePath;
        ArchiveEntryName = archiveEntryName;
        Reader = reader;
        _rawStream = rawStream;
        _archive = archive;
    }

    public string SourcePath { get; }

    public string? ArchiveEntryName { get; }

    public TextReader Reader { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reader.Dispose();
        _rawStream.Dispose();
        _archive?.Dispose();
    }
}

// Opens plain-text or ZIP-compressed (.zip / .Reg.zip) datalogs directly as a forward-only UTF-8 stream
// without loading the full file into memory or extracting ZIP entries to disk.
public sealed class DatalogStreamOpener
{
    private const int StreamBufferSize = 65_536;

    // Strict UTF-8 (throws DecoderFallbackException on invalid byte sequences rather than silently substituting U+FFFD).
    private static UTF8Encoding CreateStrictUtf8() => new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static StreamReader CreateStrictReader(Stream stream)
    {
        Span<byte> bom = stackalloc byte[3];
        var read = 0;
        while (read < bom.Length)
        {
            var n = stream.Read(bom[read..]);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return new StreamReader(stream, CreateStrictUtf8(), detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
        }

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            Stream effective = read > 2 ? new PrefixedStream(bom[2..read].ToArray(), stream) : stream;
            return new StreamReader(effective, new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            Stream effective = read > 2 ? new PrefixedStream(bom[2..read].ToArray(), stream) : stream;
            return new StreamReader(effective, new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
        }

        Stream prefixed = read > 0 ? new PrefixedStream(bom[..read].ToArray(), stream) : stream;
        return new StreamReader(prefixed, CreateStrictUtf8(), detectEncodingFromByteOrderMarks: false, StreamBufferSize, leaveOpen: true);
    }

    public OpenedDatalogStream Open(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new DatalogOpenException(DatalogOpenErrorCode.FileNotFound, $"Datalog file '{fullPath}' was not found.");
        }

        FileStream fileStream;
        try
        {
            fileStream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new DatalogOpenException(DatalogOpenErrorCode.Unreadable, $"Could not open datalog file '{fullPath}': {ex.Message}", ex);
        }

        if (fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return OpenZip(fullPath, fileStream, cancellationToken);
        }

        var reader = CreateStrictReader(fileStream);
        return new OpenedDatalogStream(fullPath, archiveEntryName: null, reader, fileStream, archive: null);
    }

    private static OpenedDatalogStream OpenZip(string fullPath, FileStream fileStream, CancellationToken cancellationToken)
    {
        ZipArchive? archive = null;
        Stream? entryStream = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);

            var fileEntries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name)
                    && !entry.FullName.EndsWith('/')
                    && !entry.FullName.EndsWith('\\'))
                .ToArray();

            if (fileEntries.Length == 0)
            {
                throw new DatalogOpenException(DatalogOpenErrorCode.EmptyArchive, $"ZIP archive '{Path.GetFileName(fullPath)}' contains no file entries.");
            }

            if (fileEntries.Length > 1)
            {
                throw new DatalogOpenException(
                    DatalogOpenErrorCode.AmbiguousArchive,
                    $"ZIP archive '{Path.GetFileName(fullPath)}' contains {fileEntries.Length} file entries; a single datalog entry is required.");
            }

            var selected = fileEntries[0];
            entryStream = selected.Open();
            var reader = CreateStrictReader(entryStream);
            return new OpenedDatalogStream(fullPath, selected.FullName, reader, entryStream, archive);
        }
        catch (DatalogOpenException)
        {
            entryStream?.Dispose();
            archive?.Dispose();
            fileStream.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            entryStream?.Dispose();
            archive?.Dispose();
            fileStream.Dispose();
            throw;
        }
        catch (InvalidDataException ex)
        {
            entryStream?.Dispose();
            archive?.Dispose();
            fileStream.Dispose();
            throw new DatalogOpenException(DatalogOpenErrorCode.CorruptArchive, $"ZIP archive '{Path.GetFileName(fullPath)}' is corrupt or invalid: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            entryStream?.Dispose();
            archive?.Dispose();
            fileStream.Dispose();
            throw new DatalogOpenException(DatalogOpenErrorCode.Unreadable, $"Could not read ZIP archive '{Path.GetFileName(fullPath)}': {ex.Message}", ex);
        }
    }

    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < prefix.Length)
            {
                var toCopy = Math.Min(count, prefix.Length - _prefixOffset);
                Buffer.BlockCopy(prefix, _prefixOffset, buffer, offset, toCopy);
                _prefixOffset += toCopy;
                return toCopy;
            }

            return inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
