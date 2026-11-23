using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ClipDesk.Core;

public sealed partial class AssetStore(string root)
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private const long MaxPixels = 64_000_000;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, DateTime CreatedTime, bool Valid)> verified = new(StringComparer.Ordinal);
    [GeneratedRegex(@"\A[a-f0-9]{64}\z")] private static partial Regex HashPattern();
    public static bool Valid(string? id) => id is { Length: 64 } && HashPattern().IsMatch(id);
    public string PathFor(string id)
    {
        if (!Valid(id)) throw new InvalidDataException("图片标识不合法");
        return Path.Combine(root, "assets", id[..2], id + ".png");
    }
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public string Put(byte[] data)
    {
        ValidatePng(data);
        var id = Hash(data);
        WriteVerified(id, data);
        return id;
    }

    public byte[] Read(string id)
    {
        using var stream = OpenRead(id);
        var length = stream.Length;
        if (length is < 33 or > MaxBytes) throw new InvalidDataException("本机图片长度不正确");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (!ValidPngHeader(bytes) || Hash(bytes) != id)
        {
            verified.TryRemove(id, out _);
            throw new InvalidDataException("本机图片校验失败，请同步或从备份恢复");
        }
        return bytes;
    }

    public bool FileExists(string id)
    {
        if (!Valid(id)) return false;
        var info = new FileInfo(PathFor(id));
        return info.Exists && info.Length is >= 33 and <= MaxBytes;
    }

    public bool Exists(string id) => Exists(id, recheck: false);

    private bool Exists(string id, bool recheck)
    {
        var info = new FileInfo(PathFor(id));
        if (!info.Exists || info.Length is < 33 or > MaxBytes)
        {
            verified.TryRemove(id, out _);
            return false;
        }
        var length = info.Length;
        var writeTime = info.LastWriteTimeUtc;
        var createdTime = info.CreationTimeUtc;
        if (!recheck && verified.TryGetValue(id, out var cached) && cached.Length == length && cached.WriteTime == writeTime && cached.CreatedTime == createdTime)
            return cached.Valid;
        try
        {
            using var stream = OpenRead(id);
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            stream.Position = 0;
            var valid = stream.Length == length && ValidPngHeader(header) && Convert.ToHexStringLower(SHA256.HashData(stream)) == id;
            verified[id] = (length, writeTime, createdTime, valid);
            return valid;
        }
        catch (IOException) { return false; }
    }

    private FileStream OpenRead(string id) => new(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public void Receive(string id, byte[] bytes)
    {
        VerifyBytes(id, bytes);
        WriteVerified(id, bytes);
    }

    internal static void VerifyBytes(string id, byte[] bytes)
    {
        ValidatePng(bytes);
        if (!Valid(id) || Hash(bytes) != id) throw new InvalidDataException("图片校验值不正确");
    }

    public static void ValidatePng(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException("图片超过 64 MB，请缩小后导入");
        if (data.Length < 33 || !ValidPngHeader(data))
            throw new InvalidDataException("PNG 原图格式不正确或超过 6400 万像素");
    }

    private static bool ValidPngHeader(ReadOnlySpan<byte> data)
    {
        if (!data[..8].SequenceEqual(PngSignature) || BinaryPrimitives.ReadUInt32BigEndian(data[8..12]) != 13 ||
            !data[12..16].SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        return width != 0 && height != 0 && (ulong)width * height <= MaxPixels;
    }

    internal bool PublishStaged(string id, string stagedPath)
    {
        if (Exists(id)) return false;
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.Move(stagedPath, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 另一写入者可能刚发布同一摘要。只有目标确实损坏时才尝试替换。
            if (Exists(id, recheck: true)) return false;
            if (!File.Exists(path)) throw;
            try { File.Move(stagedPath, path, true); }
            catch (Exception retry) when ((retry is IOException or UnauthorizedAccessException) && Exists(id, recheck: true)) { return false; }
        }
        verified.TryRemove(id, out _);
        return true;
    }

    private void WriteVerified(string id, byte[] bytes)
    {
        if (Exists(id)) return;
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stagedPath = path + "." + Codec.Id() + ".tmp";
        try
        {
            AtomicFile.WriteNew(stagedPath, bytes);
            PublishStaged(id, stagedPath);
        }
        finally { File.Delete(stagedPath); }
    }
}

public static class AtomicFile
{
    public static void Write(string path, byte[] data, bool replace = true)
    {
        if (!replace && File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Codec.Id() + ".tmp";
        try
        {
            WriteNew(temp, data);
            try { File.Move(temp, path, replace); }
            catch (IOException) when (!replace && File.Exists(path)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void WriteNew(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(true);
    }
}
