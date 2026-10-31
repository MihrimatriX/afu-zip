using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AfuZip.Engine.SevenZip.Interop;

// 7-Zip'in COM benzeri arayüzleri (CPP/7zip/IArchive.h, IStream.h, IProgress.h, IPassword.h).
// IID şablonu: 23170F69-40C1-278A-0000-00GG00II0000 (GG = grup, II = arayüz no).
// Hepsi IUnknown tabanlı ve stdcall; x64/ARM64'te tek çağrı kuralı olduğu için fark etmez.
// Tüm metotlar [PreserveSig]: HRESULT'ları kendimiz yorumluyoruz, istisna sınırı geçmez.

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000000050000")]
internal unsafe partial interface IProgress
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(ulong* completeValue);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300010000")]
internal unsafe partial interface ISequentialInStream
{
    [PreserveSig] int Read(byte* data, uint size, uint* processedSize);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300020000")]
internal unsafe partial interface ISequentialOutStream
{
    [PreserveSig] int Write(byte* data, uint size, uint* processedSize);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300030000")]
internal unsafe partial interface IInStream : ISequentialInStream
{
    /// <param name="seekOrigin">0 = başlangıç, 1 = mevcut konum, 2 = son (SeekOrigin ile aynı)</param>
    [PreserveSig] int Seek(long offset, uint seekOrigin, ulong* newPosition);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500100000")]
internal partial interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string? password);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600100000")]
internal unsafe partial interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(ulong* files, ulong* bytes);
    [PreserveSig] int SetCompleted(ulong* files, ulong* bytes);
}

/// <summary>Çok ciltli arşivlerde (.part2.rar, .7z.002) diğer ciltleri açmak için açılış callback'inden QI ile istenir.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("23170F69-40C1-278A-0000-000600300000")]
internal unsafe partial interface IArchiveOpenVolumeCallback
{
    [PreserveSig] int GetProperty(uint propId, PropVariant* value);
    /// <returns>Cilt yoksa S_FALSE ve null stream.</returns>
    [PreserveSig] int GetStream(string name, out IInStream? inStream);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600200000")]
internal unsafe partial interface IArchiveExtractCallback : IProgress
{
    /// <param name="askExtractMode">0 = çıkar, 1 = test, 2 = atla</param>
    [PreserveSig] int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode);
    [PreserveSig] int PrepareOperation(int askExtractMode);
    [PreserveSig] int SetOperationResult(int opRes);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600600000")]
internal unsafe partial interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, ulong* maxCheckStartPosition, IArchiveOpenCallback? openCallback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint numItems);
    [PreserveSig] int GetProperty(uint index, uint propId, PropVariant* value);
    /// <param name="indices">Artan sırada olmalı; null + numItems = uint.MaxValue tüm girdiler demektir.</param>
    [PreserveSig] int Extract(uint* indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);
    [PreserveSig] int GetArchiveProperty(uint propId, PropVariant* value);
    [PreserveSig] int GetNumberOfProperties(out uint numProps);
    [PreserveSig] int GetPropertyInfo(uint index, nint* name, uint* propId, ushort* varType);
    [PreserveSig] int GetNumberOfArchiveProperties(out uint numProps);
    [PreserveSig] int GetArchivePropertyInfo(uint index, nint* name, uint* propId, ushort* varType);
}
