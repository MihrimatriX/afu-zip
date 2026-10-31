using System.Runtime.InteropServices.Marshalling;

namespace AfuZip.Engine.SevenZip.Interop;

/// <summary>.NET stream'ini 7-Zip'in IInStream'i olarak sunar. Hiçbir istisna native sınırı geçmez.</summary>
[GeneratedComClass]
internal sealed unsafe partial class InStreamAdapter(Stream stream) : IInStream
{
    public int Read(byte* data, uint size, uint* processedSize)
    {
        if (processedSize != null)
            *processedSize = 0;
        try
        {
            var read = size == 0 ? 0 : stream.Read(new Span<byte>(data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != null)
                *processedSize = (uint)read;
            return HResult.S_OK;
        }
        catch (Exception)
        {
            return HResult.E_FAIL;
        }
    }

    // IStream.h: negatif konuma seek için önerilen kod HRESULT_FROM_WIN32(ERROR_NEGATIVE_SEEK).
    private const int NegativeSeek = unchecked((int)0x80070083);

    public int Seek(long offset, uint seekOrigin, ulong* newPosition)
    {
        try
        {
            var origin = (SeekOrigin)seekOrigin;
            var basePosition = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => stream.Position,
                SeekOrigin.End => stream.Length,
                _ => -1,
            };
            if (basePosition < 0)
                return HResult.E_FAIL;
            if (basePosition + offset < 0)
                return NegativeSeek;

            var position = stream.Seek(offset, origin);
            if (newPosition != null)
                *newPosition = (ulong)position;
            return HResult.S_OK;
        }
        catch (Exception)
        {
            return HResult.E_FAIL;
        }
    }
}

/// <summary>7-Zip'in yazdığı baytları bir .NET stream'ine aktarır. Hata (ör. zip bomb limiti) callback'e bildirilir.</summary>
[GeneratedComClass]
internal sealed unsafe partial class OutStreamAdapter(Stream stream, Action<Exception> onFault) : ISequentialOutStream
{
    public int Write(byte* data, uint size, uint* processedSize)
    {
        if (processedSize != null)
            *processedSize = 0;
        try
        {
            if (size > 0)
                stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            if (processedSize != null)
                *processedSize = size;
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            onFault(ex);
            return HResult.E_ABORT;
        }
    }
}
