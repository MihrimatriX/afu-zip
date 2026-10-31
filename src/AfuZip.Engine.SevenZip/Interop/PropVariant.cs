using System.Runtime.InteropServices;

namespace AfuZip.Engine.SevenZip.Interop;

/// <summary>
/// Windows PROPVARIANT'ın 64-bit düzeni: 2 bayt VARTYPE + 6 bayt rezerv + 16 baytlık birleşim = 24 bayt.
/// Yalnızca 7-Zip'in döndürdüğü tipler (BSTR, BOOL, UI4, UI8, FILETIME) okunur.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_EMPTY = 0;
    public const ushort VT_I4 = 3;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI1 = 17;
    public const ushort VT_UI2 = 18;
    public const ushort VT_UI4 = 19;
    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
    public const ushort VT_FILETIME = 64;

    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public short Bool;
    [FieldOffset(8)] public nint Pointer;

    public readonly string? AsString() =>
        VarType == VT_BSTR && Pointer != 0 ? Marshal.PtrToStringBSTR(Pointer) : null;

    /// <summary>BSTR içindeki ham baytlar (7-Zip imza ve CLSID'leri böyle döndürür).</summary>
    public readonly byte[]? AsBytes()
    {
        if (VarType != VT_BSTR || Pointer == 0)
            return null;
        var length = Marshal.ReadInt32(Pointer, -4); // BSTR uzunluk öneki, bayt cinsinden
        var bytes = new byte[length];
        Marshal.Copy(Pointer, bytes, 0, length);
        return bytes;
    }

    public readonly ulong? AsUInt64() => VarType switch
    {
        VT_UI8 or VT_I8 => (ulong)Int64,
        VT_UI4 => UInt32,
        VT_I4 => (ulong)(int)UInt32,
        VT_UI2 => (ushort)UInt32,
        VT_UI1 => (byte)UInt32,
        _ => null,
    };

    public readonly bool? AsBool() => VarType == VT_BOOL ? Bool != 0 : null;

    public readonly DateTimeOffset? AsFileTime()
    {
        if (VarType != VT_FILETIME || Int64 <= 0)
            return null;
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(Int64));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>7-Zip PropID.h'deki kpid değerleri (yalnızca kullanılanlar).</summary>
internal static class ItemPropId
{
    public const uint Path = 3;
    public const uint Name = 4;
    public const uint IsDir = 6;
    public const uint Size = 7;
    public const uint PackSize = 8;
    public const uint Attrib = 9;
    public const uint CTime = 10;
    public const uint ATime = 11;
    public const uint MTime = 12;
    public const uint Solid = 13;
    public const uint Encrypted = 15;
    public const uint Crc = 19;
    public const uint IsAnti = 21;
    public const uint Method = 22;
    public const uint Block = 27;
    public const uint PhySize = 44;
    public const uint SymLink = 54;
    public const uint Error = 55;
    public const uint ErrorFlags = 71;
    public const uint WarningFlags = 72;
    public const uint Warning = 73;
}

/// <summary>kpidErrorFlags / kpidWarningFlags bitleri (IArchive.h, kpv_ErrorFlags_*).</summary>
[Flags]
internal enum ArchiveErrorFlags : uint
{
    None = 0,
    IsNotArc = 1 << 0,
    HeadersError = 1 << 1,
    EncryptedHeadersError = 1 << 2,
    UnavailableStart = 1 << 3,
    UnconfirmedStart = 1 << 4,
    UnexpectedEnd = 1 << 5,
    DataAfterEnd = 1 << 6,
    UnsupportedMethod = 1 << 7,
    UnsupportedFeature = 1 << 8,
    DataError = 1 << 9,
    CrcError = 1 << 10,
}

/// <summary>GetHandlerProperty2 için NArchive::NHandlerPropID değerleri.</summary>
internal static class HandlerPropId
{
    public const uint Name = 0;
    public const uint ClassId = 1;
    public const uint Extension = 2;
    public const uint AddExtension = 3;
    public const uint Update = 4;
    public const uint Signature = 6;
    public const uint MultiSignature = 7;
    public const uint SignatureOffset = 8;
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);
}

internal enum OperationResult
{
    Ok = 0,
    UnsupportedMethod = 1,
    DataError = 2,
    CrcError = 3,
    Unavailable = 4,
    UnexpectedEnd = 5,
    DataAfterEnd = 6,
    IsNotArc = 7,
    HeadersError = 8,
    WrongPassword = 9,
}

internal static partial class NativeMethods
{
    [LibraryImport("ole32.dll")]
    internal static unsafe partial int PropVariantClear(PropVariant* value);
}
