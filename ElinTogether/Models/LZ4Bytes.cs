using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using LZ4;
using MessagePack;
using Newtonsoft.Json;

namespace ElinTogether.Models;

[MessagePackObject]
public class LZ4Bytes
{
    private static readonly JsonSerializer _serializer = JsonSerializer.Create(GameIOContext.Settings);

    // what another game sends is read with the types of the game and its mods only: the game's settings name the type
    // to make in the text, any type at all (upstream 6f0fda1, 2026-10-14)
    private static readonly JsonSerializer _deserializer = CreateDeserializer();
    public static LZ4Bytes Empty => field ??= new() { Bytes = [] };

    // a map is some MB once decompressed; this only stops a buggy peer's bomb from eating the memory of the game
    private const long MaxDecompressed = 256L * 1024 * 1024;

    [Key(0)]
    public required byte[] Bytes { get; init; }

    // LZ4Stream's default block is 1 MB, a new array at each call even for a 200-byte card (efficiency audit
    // 2026-11-03): the block is the payload's size rounded up to a power of two, at most 1 MB, the old default. Each
    // block is packed on its own (no match reaches back into the block before), so a 64 KB cap packed a 280 KB list
    // 26% bigger (bench, 2026-11-03); with 1 MB the bytes are the old ones for every size. The reader takes each
    // block's size from the block itself, so data packed either way reads the same
    private const int MinBlock = 256;
    private const int MaxBlock = 1024 * 1024;

    // the JSON text and the packed bytes, one of each kept per thread between calls (a call inside a call takes new
    // ones); one grown past 1 MB (a map) is let go rather than kept
    private const int KeepAtMost = 1024 * 1024;

    [ThreadStatic]
    private static MemoryStream? _text;

    [ThreadStatic]
    private static MemoryStream? _packed;

    internal static int BlockSize(long length)
    {
        var block = MinBlock;
        while (block < length && block < MaxBlock) {
            block <<= 1;
        }

        return block;
    }

    private static MemoryStream Rent(ref MemoryStream? slot)
    {
        var ms = slot ?? new MemoryStream();
        slot = null;
        ms.SetLength(0);
        return ms;
    }

    private static void Return(ref MemoryStream? slot, MemoryStream ms)
    {
        if (ms.Capacity <= KeepAtMost) {
            slot = ms;
        }
    }

    public static LZ4Bytes Create<T>(T data)
    {
        var text = Rent(ref _text);
        try {
            // (the same bytes as before, written to the LZ4 stream directly: UTF-8 with its mark first)
            using (var sw = new StreamWriter(text, Encoding.UTF8, 1024, true))
            using (var jw = new JsonTextWriter(sw)) {
                _serializer.Serialize(jw, data, typeof(T));
                jw.Flush();
            }

            return Pack(text.GetBuffer(), (int)text.Length);
        } finally {
            Return(ref _text, text);
        }
    }

    private static LZ4Bytes Pack(byte[] input, int length)
    {
        var packed = Rent(ref _packed);
        try {
            using (var lz4 = new LZ4Stream(packed, CompressionMode.Compress, LZ4StreamFlags.IsolateInnerStream, BlockSize(length))) {
                lz4.Write(input, 0, length);
                lz4.Flush();
            }

            return new() {
                Bytes = packed.ToArray(),
            };
        } finally {
            Return(ref _packed, packed);
        }
    }

    /// <summary>
    ///     The same text as <see cref="Create{T}" /> compresses, to compare cheaply
    /// </summary>
    public static string ToJson<T>(T data)
    {
        using var sw = new StringWriter();
        using var jw = new JsonTextWriter(sw);
        _serializer.Serialize(jw, data, typeof(T));
        jw.Flush();
        return sw.ToString();
    }

    public static LZ4Bytes CreateFromFile(string filePath)
    {
        using var input = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var packed = Rent(ref _packed);
        try {
            // fast mode: high compression froze the main thread at each join (~0.4 s on a small map, empload 2026-10-27).
            // Maps are ~40% bigger, which SteamNetPeer sends in pieces (NetFragments)
            using (var lz4 = new LZ4Stream(packed, CompressionMode.Compress, LZ4StreamFlags.IsolateInnerStream,
                       BlockSize(input.Length))) {
                input.CopyTo(lz4);
                lz4.Flush();
            }

            return new() {
                Bytes = packed.ToArray(),
            };
        } finally {
            Return(ref _packed, packed);
        }
    }

    public static LZ4Bytes CreateFromBytes(byte[] input)
    {
        return Pack(input, input.Length);
    }

    public T Decompress<T>()
    {
        using var input = new MemoryStream(Bytes);
        using var lz4 = new CappedStream(new LZ4Stream(input, CompressionMode.Decompress));
        using var sr = new StreamReader(lz4, Encoding.UTF8);
        using var jr = new JsonTextReader(sr);

        return _deserializer.Deserialize<T>(jr)!;
    }

    private static JsonSerializer CreateDeserializer()
    {
        var deserializer = JsonSerializer.Create(GameIOContext.Settings);
        deserializer.SerializationBinder = NetTypeBinder.Instance;
        return deserializer;
    }

    public string DecompressToString()
    {
        using var input = new MemoryStream(Bytes);
        using var lz4 = new CappedStream(new LZ4Stream(input, CompressionMode.Decompress));
        using var sr = new StreamReader(lz4, Encoding.UTF8);

        return sr.ReadToEnd();
    }

    public byte[] DecompressToBytes()
    {
        using var input = new MemoryStream(Bytes);
        using var lz4 = new CappedStream(new LZ4Stream(input, CompressionMode.Decompress));
        using var ms = new MemoryStream();

        lz4.CopyTo(ms);

        return ms.ToArray();
    }

    public void DecompressToStream(Stream output, long max = MaxDecompressed)
    {
        using var input = new MemoryStream(Bytes);
        using var lz4 = new CappedStream(new LZ4Stream(input, CompressionMode.Decompress), max);

        lz4.CopyTo(output);
    }

    /// <returns>the bytes written</returns>
    /// <param name="max">at most this many, else it throws (a file of several makes them share one budget)</param>
    public long DecompressToFile(string filePath, long max = MaxDecompressed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var fs = File.Create(filePath);

        DecompressToStream(fs, Math.Min(max, MaxDecompressed));
        return fs.Length;
    }

    internal sealed class CappedStream(Stream inner, long max = MaxDecompressed) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > max) {
                throw new InvalidDataException($"Decompressed data over {max} bytes");
            }

            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
