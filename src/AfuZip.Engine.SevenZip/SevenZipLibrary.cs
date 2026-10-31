using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;
using AfuZip.Engine.SevenZip.Interop;

namespace AfuZip.Engine.SevenZip;

/// <summary>7z.dll içindeki bir format işleyicisi (handler).</summary>
public sealed record SevenZipFormat(
    int Index,
    string Name,
    Guid ClassId,
    IReadOnlyList<string> Extensions,
    bool CanUpdate,
    IReadOnlyList<byte[]> Signatures,
    int SignatureOffset)
{
    internal bool MatchesSignature(ReadOnlySpan<byte> header)
    {
        foreach (var signature in Signatures)
        {
            if (signature.Length > 0 &&
                header.Length >= SignatureOffset + signature.Length &&
                header.Slice(SignatureOffset, signature.Length).SequenceEqual(signature))
                return true;
        }
        return false;
    }
}

/// <summary>
/// 7z.dll'i yükler, export'larını çözer ve format listesini okur.
/// 7z.dll değiştirilmeden kullanılır (LGPL); RAR yalnızca okunur (unRAR kısıtı).
/// </summary>
public sealed unsafe class SevenZipLibrary : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly nint _handle;
    private readonly delegate* unmanaged[Stdcall]<Guid*, Guid*, void**, int> _createObject;

    private SevenZipLibrary(string path, nint handle)
    {
        Path = path;
        _handle = handle;
        _createObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(handle, "CreateObject");
        Formats = LoadFormats(handle);
        EncoderMethods = LoadEncoderMethods(handle);
    }

    public string Path { get; }

    public IReadOnlyList<SevenZipFormat> Formats { get; }

    /// <summary>Bu 7z.dll'in sıkıştırabildiği yöntemler (LZMA2, Deflate, PPMD; 7-Zip ZS'de ZSTD...).</summary>
    public IReadOnlySet<string> EncoderMethods { get; }

    /// <summary>Aranan konumlar: açık yol, AFUZIP_7Z_DLL ortam değişkeni, uygulama klasörü, 7-Zip ZS ve 7-Zip kurulumları.</summary>
    public static IEnumerable<string> CandidatePaths(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        if (Environment.GetEnvironmentVariable("AFUZIP_7Z_DLL") is { Length: > 0 } fromEnv)
            yield return fromEnv;

        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "7z.dll");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            yield return System.IO.Path.Combine(programFiles, "7-Zip-Zstandard", "7z.dll");
            yield return System.IO.Path.Combine(programFiles, "7-Zip", "7z.dll");
        }
    }

    public static SevenZipLibrary? TryLoad(string? explicitPath = null)
    {
        // PROPVARIANT düzeni (24 bayt, değer 8. baytta) 64 bit varsayar; 32 bit süreçte 7z.dll kullanılmaz.
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return null;

        foreach (var candidate in CandidatePaths(explicitPath))
        {
            if (!File.Exists(candidate) || !NativeLibrary.TryLoad(candidate, out var handle))
                continue;

            if (NativeLibrary.TryGetExport(handle, "CreateObject", out _) &&
                NativeLibrary.TryGetExport(handle, "GetNumberOfFormats", out _) &&
                NativeLibrary.TryGetExport(handle, "GetHandlerProperty2", out _))
                return new SevenZipLibrary(candidate, handle);

            NativeLibrary.Free(handle);
        }
        return null;
    }

    internal IOutArchive CreateOutArchive(Guid classId) => Create<IOutArchive>(classId);

    internal IInArchive CreateInArchive(Guid classId) => Create<IInArchive>(classId);

    /// <summary>Ada göre işleyici ("7z", "zip", "gzip"...).</summary>
    public SevenZipFormat? FindFormat(string handlerName) =>
        Formats.FirstOrDefault(f => f.Name.Equals(handlerName, StringComparison.OrdinalIgnoreCase));

    private T Create<T>(Guid classId) where T : class
    {
        var iid = typeof(T).GUID;
        void* instance = null;
        var hr = _createObject(&classId, &iid, &instance);
        if (hr != HResult.S_OK || instance == null)
            throw new InvalidOperationException($"7z.dll CreateObject başarısız (0x{hr:X8}).");

        try
        {
            return (T)ComWrappers.GetOrCreateObjectForComInstance((nint)instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            // Sarmalayıcı kendi referansını aldı; CreateObject'in verdiği referansı bırakıyoruz.
            Marshal.Release((nint)instance);
        }
    }

    /// <summary>Sırası: format ipucu, içerik imzası, uzantı. Hiçbiri eşleşmezse boş döner.</summary>
    internal IReadOnlyList<SevenZipFormat> CandidatesFor(string archivePath, ArchiveFormat hint, ReadOnlySpan<byte> header)
    {
        var result = new List<SevenZipFormat>();

        if (HandlerName(hint) is { } hinted)
        {
            foreach (var format in Formats)
            {
                if (format.Name.Equals(hinted, StringComparison.OrdinalIgnoreCase))
                    result.Add(format);
            }
        }

        foreach (var format in Formats)
        {
            if (!result.Contains(format) && format.MatchesSignature(header))
                result.Add(format);
        }

        var extension = System.IO.Path.GetExtension(archivePath).TrimStart('.');
        if (extension.Length > 0)
        {
            foreach (var format in Formats)
            {
                if (!result.Contains(format) && format.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    result.Add(format);
            }
        }

        return result;
    }

    internal static string? HandlerName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.Rar4 => "Rar",
        ArchiveFormat.Rar5 => "Rar5",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.GZip => "gzip",
        ArchiveFormat.BZip2 => "bzip2",
        ArchiveFormat.Xz => "xz",
        ArchiveFormat.Zstd => "zstd",
        ArchiveFormat.Lz4 => "lz4",
        ArchiveFormat.Brotli => "brotli",
        ArchiveFormat.Lzma => "lzma",
        ArchiveFormat.Iso => "Iso",
        ArchiveFormat.Cab => "Cab",
        ArchiveFormat.Wim => "wim",
        _ => null,
    };

    private static IReadOnlyList<SevenZipFormat> LoadFormats(nint handle)
    {
        var getCount = (delegate* unmanaged[Stdcall]<uint*, int>)NativeLibrary.GetExport(handle, "GetNumberOfFormats");
        var getProperty = (delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int>)NativeLibrary.GetExport(handle, "GetHandlerProperty2");

        uint count = 0;
        if (getCount(&count) != HResult.S_OK)
            return [];

        var formats = new List<SevenZipFormat>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var name = Read(getProperty, i, HandlerPropId.Name, static v => v.AsString());
            var classId = Read(getProperty, i, HandlerPropId.ClassId, static v => v.AsBytes());
            if (name is null || classId is not { Length: 16 })
                continue;

            var extensions = Read(getProperty, i, HandlerPropId.Extension, static v => v.AsString())?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var canUpdate = Read(getProperty, i, HandlerPropId.Update, static v => v.AsBool()) ?? false;
            var offset = (int)(Read(getProperty, i, HandlerPropId.SignatureOffset, static v => v.AsUInt64()) ?? 0);

            var signatures = new List<byte[]>();
            if (Read(getProperty, i, HandlerPropId.Signature, static v => v.AsBytes()) is { Length: > 0 } single)
                signatures.Add(single);
            if (Read(getProperty, i, HandlerPropId.MultiSignature, static v => v.AsBytes()) is { Length: > 0 } multi)
            {
                // Biçim: [uzunluk][imza baytları] tekrarı
                for (var p = 0; p < multi.Length;)
                {
                    int length = multi[p++];
                    if (p + length > multi.Length)
                        break;
                    signatures.Add(multi[p..(p + length)]);
                    p += length;
                }
            }

            formats.Add(new SevenZipFormat((int)i, name, new Guid(classId), extensions, canUpdate, signatures, offset));
        }
        return formats;
    }

    /// <summary>GetNumberOfMethods / GetMethodProperty: kodlayıcısı olan yöntemlerin adları.</summary>
    private static IReadOnlySet<string> LoadEncoderMethods(nint handle)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!NativeLibrary.TryGetExport(handle, "GetNumberOfMethods", out var countExport) ||
            !NativeLibrary.TryGetExport(handle, "GetMethodProperty", out var propertyExport))
            return result;

        var getCount = (delegate* unmanaged[Stdcall]<uint*, int>)countExport;
        var getProperty = (delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int>)propertyExport;
        uint count = 0;
        if (getCount(&count) != HResult.S_OK)
            return result;

        const uint Name = 1, Encoder = 3, EncoderIsAssigned = 8; // NMethodPropID
        for (uint i = 0; i < count; i++)
        {
            if (Read(getProperty, i, Name, static v => v.AsString()) is not { Length: > 0 } name)
                continue;
            var hasEncoder = Read(getProperty, i, EncoderIsAssigned, static v => v.AsBool())
                             ?? Read(getProperty, i, Encoder, static v => v.AsBytes()) is { Length: > 0 };
            if (hasEncoder)
                result.Add(name);
        }
        return result;
    }

    private static T? Read<T>(delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int> getProperty,
        uint index, uint propId, Func<PropVariant, T?> map)
    {
        PropVariant value = default;
        try
        {
            return getProperty(index, propId, &value) == HResult.S_OK ? map(value) : default;
        }
        finally
        {
            NativeMethods.PropVariantClear(&value);
        }
    }

    public void Dispose()
    {
        // 7z.dll süreç boyunca yüklü kalır: açık arşiv nesneleri kodunu kullanıyor olabilir.
        GC.SuppressFinalize(this);
    }
}
