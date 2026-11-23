using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClipDesk.App;

internal sealed class ClipboardService : IDisposable
{
    private const long MaxImagePixels = 64_000_000;
    private const int MaxTextBytes = 8_000_016;
    private const int MaxRichTextBytes = 16_000_016;
    private static readonly Encoding RtfEncoding = CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.Latin1;
    private readonly Store store;
    private readonly HwndSource source;
    private readonly GlobalHotkeys hotkeys;
    private readonly bool observeClipboard;
    private readonly uint ignoreFormat = Native.ClipboardFormat("Clipboard Viewer Ignore");
    private readonly uint excludeFormat = Native.ClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint historyFormat = Native.ClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint cloudFormat = Native.ClipboardFormat("CanUploadToCloudClipboard");
    private readonly uint pngFormat = Native.ClipboardFormat("PNG");
    private readonly uint htmlFormat = Native.ClipboardFormat(DataFormats.Html);
    private readonly uint rtfFormat = Native.ClipboardFormat(DataFormats.Rtf);
    private uint seenSequence;
    private long captureTicket, localCopyTicket, lastRemoteCopy;
    private volatile bool disposed;
    internal long LastLocalCopy { get; private set; } = Codec.Now;
    internal event Action? Summon;
    internal event Action<string>? WindowRequested;
    internal event Action<string>? Feedback;

    private sealed record CaptureData(Entry Entry, byte[]? Png = null, BitmapSource? Bitmap = null);

    internal ClipboardService(Store store, bool observeClipboard = true)
    {
        this.store = store;
        this.observeClipboard = observeClipboard;
        source = new HwndSource(new HwndSourceParameters("ClipDesk 原生消息") { ParentWindow = new nint(-3), Width = 0, Height = 0 });
        hotkeys = new((id, modifiers, key) => Native.RegisterHotKey(source.Handle, id, modifiers, key), UnregisterHotkey);
        source.AddHook(Hook);
        if (observeClipboard && !Native.AddClipboardFormatListener(source.Handle))
        {
            source.Dispose();
            throw new InvalidOperationException("无法注册系统剪贴板监听");
        }
        seenSequence = Native.GetClipboardSequenceNumber();
    }

    internal void Register(string clipboard) => hotkeys.Register(clipboard);
    internal void Register(LocalSettings settings) => hotkeys.Register(settings);
    internal IReadOnlyList<string> RegisterStartup(LocalSettings settings) => hotkeys.RegisterStartup(settings);

    private HotkeyRemoval UnregisterHotkey(int id)
    {
        const int hotkeyNotRegistered = 1419;
        if (Native.UnregisterHotKey(source.Handle, id)) return HotkeyRemoval.Removed;
        return Marshal.GetLastPInvokeError() == hotkeyNotRegistered ? HotkeyRemoval.Missing : HotkeyRemoval.Failed;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && hotkeys.ActionFor(wParam) is { } action)
        {
            handled = true;
            WindowRequested?.Invoke(action);
            if (action == "clipboard") Summon?.Invoke();
        }
        if (observeClipboard && message == 0x031D)
        {
            var sequence = Native.GetClipboardSequenceNumber();
            if (sequence == 0 || sequence == seenSequence) return 0;
            seenSequence = sequence;
            var ticket = ++captureTicket;
            if (Native.GetClipboardOwner() != source.Handle)
            {
                // 暂停、排除和敏感内容仍是本地活动，远端复制应让位。
                LastLocalCopy = Codec.Now;
                _ = Capture(sequence, ticket);
            }
        }
        return 0;
    }

    private async Task Capture(uint sequence, long ticket)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (disposed || ticket != captureTicket || Native.GetClipboardSequenceNumber() != sequence) return;
            CaptureData? captured;
            try
            {
                var settings = store.Settings;
                if (settings.Paused) return;
                var excluded = settings.ExcludedApps.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (!Native.OpenClipboard(source.Handle)) throw new ExternalException("剪贴板暂时被占用");
                try { captured = ReadClipboard(ref sequence, excluded); }
                finally { Native.CloseClipboard(); }
            }
            catch (ExternalException)
            {
                if (attempt == 5) { Feedback?.Invoke("剪贴板暂时无法读取，本次未收录，请重新复制"); return; }
                await Task.Delay(25 * (attempt + 1));
                continue;
            }
            catch (Exception ex)
            {
                if (!disposed) Feedback?.Invoke("收录失败：" + ex.Message);
                return;
            }
            if (captured == null) return;
            try
            {
                await Task.Run(() =>
                {
                    if (disposed) return;
                    var entry = captured.Entry;
                    if (captured.Png != null)
                    {
                        // 解码检查有效性，同时保留 PNG 原始字节。
                        _ = ReadImage(captured.Png);
                        entry = entry with { Assets = [store.Assets.Put(captured.Png)] };
                    }
                    else if (captured.Bitmap != null)
                    {
                        entry = entry with { Assets = [store.Assets.Put(ImageBytes(captured.Bitmap))] };
                    }
                    if (!disposed) store.Save(entry, "capture");
                });
            }
            catch (Exception ex) { if (!disposed) Feedback?.Invoke("收录失败：" + ex.Message); }
            return;
        }
    }

    private CaptureData? ReadClipboard(ref uint sequence, string[] excluded)
    {
        if (Native.GetClipboardSequenceNumber() != sequence) return null;
        try
        {
            var owner = Native.GetClipboardOwner();
            if (owner == source.Handle || Native.IsPrivateClipboard(ignoreFormat, excludeFormat, historyFormat, cloudFormat)) return null;
            var process = Native.ProcessName(owner);
            if (excluded.Length > 0 && (process == null || excluded.Any(name =>
                name.Equals(process, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(process + ".exe", StringComparison.OrdinalIgnoreCase))))
                return null;
            var entry = new Entry { SourceApp = process ?? "未知应用" };
            var png = Native.ReadClipboardBytes(pngFormat, AssetStore.MaxBytes + 16);
            if (png != null)
            {
                var length = PngLength(png);
                if (length > 0)
                {
                    if (length > AssetStore.MaxBytes) throw new InvalidDataException("图片超过 64 MB，请缩小后导入");
                    if (length < png.Length) Array.Resize(ref png, length);
                    return new(entry with { Format = "image" }, Png: png);
                }
            }
            var bitmap = Native.ReadClipboardBitmap(MaxImagePixels);
            if (bitmap != null) return new(entry with { Format = "image" }, Bitmap: bitmap);
            var text = ReadText(Native.ClipboardUnicodeText, Encoding.Unicode, MaxTextBytes);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
            return new(entry with
            {
                Body = text, Format = isLink ? "link" : "text",
                Html = ReadText(htmlFormat, Encoding.UTF8, MaxRichTextBytes),
                Rtf = ReadText(rtfFormat, RtfEncoding, MaxRichTextBytes)
            });
        }
        finally
        {
            // 延迟渲染可在同一个剪贴板锁内推进序号。
            sequence = seenSequence = Native.GetClipboardSequenceNumber();
        }
    }

    private static string? ReadText(uint format, Encoding encoding, int maxBytes)
    {
        var bytes = Native.ReadClipboardBytes(format, maxBytes);
        if (bytes == null) return null;
        var text = encoding.GetString(bytes);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    private static int PngLength(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || !data.StartsWith(signature) || !data.Slice(12, 4).SequenceEqual("IHDR"u8)) return 0;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (width == 0 || height == 0) return 0;
        if ((ulong)width * height > (ulong)MaxImagePixels) throw new InvalidDataException("图片超过 6400 万像素，请缩小后导入");
        // GlobalSize 可能包含分配器补齐的字节，文件到 IEND 为止。
        var offset = 8;
        while (data.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > data.Length - offset - 12) return 0;
            var end = offset + 12 + (int)length;
            if (length == 0 && data.Slice(offset + 4, 4).SequenceEqual("IEND"u8)) return end;
            offset = end;
        }
        return 0;
    }

    internal static byte[] ImageBytes(BitmapSource image)
    {
        if ((long)image.PixelWidth * image.PixelHeight > MaxImagePixels) throw new InvalidOperationException("图片超过 6400 万像素，请缩小后导入");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static byte[]? TryReadClipboardImage()
    {
        if (Clipboard.ContainsText()) return null;
        if (Clipboard.GetDataObject() is { } data && data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is Stream stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var length = PngLength(bytes);
            if (length > 0 && length <= AssetStore.MaxBytes)
            {
                if (length < bytes.Length) Array.Resize(ref bytes, length);
                _ = ReadImage(bytes);
                return bytes;
            }
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            return ImageBytes(bitmap);
        return null;
    }

    internal static BitmapSource ReadImage(byte[] data, int width = 0)
    {
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal Entry Import(string path, string kind = "clip")
    {
        if (new FileInfo(path).Length > AssetStore.MaxBytes) throw new InvalidOperationException("图片超过 64 MB");
        var data = File.ReadAllBytes(path);
        using var stream = new MemoryStream(data, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels)
            throw new InvalidOperationException("图片超过 6400 万像素，请缩小后导入");
        var asset = store.Assets.Put(ImageBytes(frame));
        return new Entry { Kind = kind, Format = "image", Assets = [asset], Protected = true, Title = Path.GetFileNameWithoutExtension(path) };
    }

    internal async Task<uint> Copy(Entry entry, bool plain = false, long? remoteCopyTime = null, Func<bool>? remoteStillCurrent = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var expected = Native.GetClipboardSequenceNumber();
        var ticket = remoteCopyTime.HasValue ? localCopyTicket : ++localCopyTicket;
        if (!remoteCopyTime.HasValue) LastLocalCopy = Codec.Now;
        if (!CanWriteClipboard(expected, ticket, remoteCopyTime, remoteStillCurrent)) return CopySuperseded(remoteCopyTime);
        using var payload = new Native.ClipboardWrite();
        var copied = await Task.Run(() => PrepareCopy(payload, entry, plain));
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!CanWriteClipboard(expected, ticket, remoteCopyTime, remoteStillCurrent)) return CopySuperseded(remoteCopyTime);
            if (payload.TryCommit(source.Handle, expected, out var written))
            {
                if (written == 0) return CopySuperseded(remoteCopyTime);
                seenSequence = written;
                captureTicket++;
                if (remoteCopyTime is { } time) lastRemoteCopy = time;
                else
                {
                    LastLocalCopy = Codec.Now;
                    try { store.RecordCopy(copied); }
                    catch (Exception) { Feedback?.Invoke("已复制到本机，复制事件未能加入同步队列"); }
                }
                return written;
            }
            if (attempt < 5) await Task.Delay(30 * (attempt + 1));
        }
        throw new InvalidOperationException("剪贴板暂时被占用，请重新复制");
    }

    private bool CanWriteClipboard(uint expected, long ticket, long? remoteCopyTime, Func<bool>? remoteStillCurrent)
    {
        if (disposed || ticket != localCopyTicket || Native.GetClipboardSequenceNumber() != expected) return false;
        return remoteCopyTime is not { } time || seenSequence == expected && time > lastRemoteCopy
            && time > LastLocalCopy + 2000 && (remoteStillCurrent?.Invoke() ?? true)
            && store.Settings.Sync is { Enabled: true, ReceiveClipboard: true };
    }

    private static uint CopySuperseded(long? remoteCopyTime) => remoteCopyTime.HasValue
        ? 0u : throw new InvalidOperationException("剪贴板已更新，请重新复制所选内容");

    private Entry PrepareCopy(Native.ClipboardWrite payload, Entry entry, bool plain)
    {
        if (!plain && entry.Assets.Count > 0)
        {
            var id = entry.Assets[0];
            if (!store.Assets.Exists(id)) throw new InvalidOperationException("原图还未下载完成，请同步后重试");
            var png = store.Assets.Read(id);
            if (PngLength(png) == 0) throw new InvalidDataException("原图不是有效的 PNG");
            var image = ReadImage(png);
            payload.Add(pngFormat, png);
            AddBitmap(payload, image);
            return entry.Assets.Count == 1 ? entry : entry with { Assets = [id] };
        }
        var text = entry.Body.Length > 0 ? entry.Body : entry.Title;
        if (text.Length == 0 || text.Contains('\0')) throw new InvalidOperationException("没有可复制的文字");
        if (!plain && entry.Html != null) payload.Add(htmlFormat, Encoding.UTF8.GetBytes(entry.Html + '\0'));
        if (!plain && entry.Rtf != null) payload.Add(rtfFormat, RtfEncoding.GetBytes(entry.Rtf + '\0'));
        payload.Add(Native.ClipboardUnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
        return plain
            ? entry with
            {
                Format = entry.Format == "link" ? "link" : "text",
                Assets = [], Html = null, Rtf = null, OriginalHtml = null, OriginalRtf = null
            }
            : entry;
    }

    private static void AddBitmap(Native.ClipboardWrite payload, BitmapSource image)
    {
        const int headerBytes = 124;
        var pixels = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(pixels.PixelWidth * 4);
        var bytes = checked(stride * pixels.PixelHeight);
        var header = new byte[headerBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), pixels.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), -pixels.PixelHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), bytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), (int)Math.Round(pixels.DpiX / 0.0254));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), (int)Math.Round(pixels.DpiY / 0.0254));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108), 4); // LCS_GM_IMAGES
        payload.Add(Native.ClipboardDibV5, headerBytes + bytes, pointer =>
        {
            Marshal.Copy(header, 0, pointer, header.Length);
            pixels.CopyPixels(Int32Rect.Empty, pointer + headerBytes, bytes, stride);
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (observeClipboard) Native.RemoveClipboardFormatListener(source.Handle);
        hotkeys.Dispose();
        source.Dispose();
    }
}
