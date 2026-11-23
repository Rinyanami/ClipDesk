using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ClipDesk.Core;

public sealed record BackupData(int Protocol, List<Change> Changes, Dictionary<string, Draft> Drafts);

public static class Backup
{
    private const int MaxDataBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    public static void Export(Store store, string path)
    {
        var backup = store.BackupSnapshot();
        Validate(backup);
        var temp = path + "." + Codec.Id() + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    long total;
                    using (var entry = zip.CreateEntry("data.json").Open())
                    using (var bounded = new ManifestWriter(entry))
                    {
                        JsonSerializer.Serialize(bounded, backup, Codec.Options);
                        total = bounded.Length;
                    }
                    foreach (var id in AssetIds(backup))
                    {
                        if (!store.Assets.Exists(id)) throw new InvalidOperationException("仍有图片未下载或校验失败，请先补齐原图");
                        var bytes = store.Assets.Read(id);
                        total += bytes.Length;
                        if (total > MaxTotalBytes) throw new InvalidDataException("备份超过本版导入上限（总计 2 GB）");
                        using var output = zip.CreateEntry(AssetName(id), CompressionLevel.Fastest).Open();
                        output.Write(bytes);
                    }
                }
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static int Import(Store store, string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = IndexEntries(zip);
        var data = entries.GetValueOrDefault("data.json") ?? throw new InvalidDataException("备份缺少数据文件");
        var backup = ReadManifest(data);
        Validate(backup);

        var staging = Path.Combine(store.Root, ".restore-" + Codec.Id());
        Dictionary<string, string> staged = new(StringComparer.Ordinal);
        try
        {
            foreach (var id in AssetIds(backup))
            {
                var entry = entries.GetValueOrDefault(AssetName(id)) ?? throw new InvalidDataException("备份中的图片缺失");
                var bytes = ReadEntry(entry);
                AssetStore.VerifyBytes(id, bytes);
                if (store.Assets.Exists(id)) continue;
                var stagedPath = Path.Combine(staging, id + ".png");
                AtomicFile.Write(stagedPath, bytes);
                staged.Add(id, stagedPath);
            }
            return store.MergeBackup(backup, () =>
            {
                var published = 0;
                foreach (var (id, stagedPath) in staged)
                    if (store.Assets.PublishStaged(id, stagedPath)) published++;
                return published;
            });
        }
        finally
        {
            foreach (var stagedPath in staged.Values) File.Delete(stagedPath);
            if (Directory.Exists(staging)) Directory.Delete(staging);
        }
    }

    private static string AssetName(string id) => "assets/" + id + ".png";
    private static IEnumerable<string> AssetIds(BackupData backup)
        => backup.Changes.Where(c => c.Action != "copy").SelectMany(c => c.Value.Assets)
            .Concat(backup.Drafts.Values.SelectMany(d => d.Value.Assets)).Distinct(StringComparer.Ordinal);

    private static void Validate(BackupData backup)
    {
        if (backup.Protocol != 1 || backup.Changes == null || backup.Drafts == null)
            throw new InvalidDataException("备份版本或数据结构不受支持");
        HashSet<string> events = new(StringComparer.Ordinal);
        foreach (var change in backup.Changes)
        {
            Store.Validate(change);
            Store.ValidateSerializedChange(Codec.Json(change));
            if (!events.Add(change.EventId)) throw new InvalidDataException("备份中的变更标识重复");
        }
        foreach (var (key, draft) in backup.Drafts) Store.ValidateDraft(key, draft);
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var manifest = name == "data.json";
            var isAsset = name.Length == 75 && name.StartsWith("assets/", StringComparison.Ordinal) &&
                name.EndsWith(".png", StringComparison.Ordinal) && AssetStore.Valid(name[7..71]);
            if ((!manifest && !isAsset) || !entries.TryAdd(name, entry))
                throw new InvalidDataException("备份中的文件名无效或重复");
            if (entry.Length < 0 || entry.Length > (manifest ? MaxDataBytes : AssetStore.MaxBytes) || entry.Length > MaxTotalBytes - total)
                throw new InvalidDataException("备份超过本版导入上限（总计 2 GB，数据 128 MB，单张图片 64 MB）");
            total += entry.Length;
        }
        return entries;
    }

    private static BackupData ReadManifest(ZipArchiveEntry entry)
    {
        ReadOnlySpan<byte> json = ReadEntry(entry);
        if (json.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
        try
        {
            return JsonSerializer.Deserialize<BackupData>(json, Codec.Options)
                ?? throw new InvalidDataException("备份数据为空");
        }
        catch (JsonException ex) { throw new InvalidDataException("备份中的数据格式不正确", ex); }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        // 中央目录中的声明长度只用于设定读取上限，实际解压内容必须等长。
        using var stream = entry.Open();
        var bytes = new byte[(int)entry.Length];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("备份文件内容不完整", ex); }
        if (stream.ReadByte() != -1) throw new InvalidDataException("备份文件实际长度与声明不一致");
        return bytes;
    }

    private sealed class ManifestWriter(Stream output) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaxDataBytes - written) throw new InvalidDataException("备份数据超过本版导入上限（128 MB）");
            output.Write(buffer);
            written += buffer.Length;
        }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
