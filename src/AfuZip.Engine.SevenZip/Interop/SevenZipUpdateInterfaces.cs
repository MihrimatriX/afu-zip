using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AfuZip.Engine.SevenZip.Interop;

// Arşiv oluşturma ve güncelleme arayüzleri (CPP/7zip/Archive/IArchive.h, IStream.h, IPassword.h).
// Metot sırası vtable sırasıdır; başlıktaki Z7_IFACEM_* makrolarıyla birebir aynı tutulmalı.

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300040000")]
internal unsafe partial interface IOutStream : ISequentialOutStream
{
    [PreserveSig] int Seek(long offset, uint seekOrigin, ulong* newPosition);
    [PreserveSig] int SetSize(ulong newSize);
}

/// <summary>Biçim ayarları: "x" (seviye), "m0" (yöntem), "s" (solid), "mt" (iş parçacığı), "he" (başlık şifreleme)...</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600030000")]
internal unsafe partial interface ISetProperties
{
    [PreserveSig] int SetProperties(nint* names, PropVariant* values, uint numProps);
}

/// <summary>Şifreli arşiv oluştururken parola: tanımlı mı + parola.</summary>
[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500110000")]
internal partial interface ICryptoGetTextPassword2
{
    [PreserveSig] int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string? password);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600800000")]
internal unsafe partial interface IArchiveUpdateCallback : IProgress
{
    /// <param name="newData">1: içerik yeni (GetStream çağrılır); 0: eski arşivden kopyalanır.</param>
    /// <param name="newProps">1: özellikler yeni (GetProperty çağrılır); 0: eski arşivdeki gibi.</param>
    /// <param name="indexInArchive">Eski arşivdeki indeks; yeni girdi için uint.MaxValue.</param>
    [PreserveSig] int GetUpdateItemInfo(uint index, int* newData, int* newProps, uint* indexInArchive);
    [PreserveSig] int GetProperty(uint index, uint propId, PropVariant* value);
    [PreserveSig] int GetStream(uint index, out ISequentialInStream? inStream);
    [PreserveSig] int SetOperationResult(int operationResult);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600A00000")]
internal unsafe partial interface IOutArchive
{
    [PreserveSig] int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);
    [PreserveSig] int GetFileTimeType(uint* type);
}
