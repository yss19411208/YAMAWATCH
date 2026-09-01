using System.Collections.Concurrent;

namespace VALOWATCH;

/// <summary>
/// ScreenRecorderLib が書き込む fMP4 を受け取り、ffmpeg の標準入力へ流すための Stream。
///
/// ScreenRecorderLib は Seek/Length/Position を使うため、内部に MemoryStream を持って
/// 完全対応する。同時に、書き込まれたデータをキューに積み、PumpToAsync で ffmpeg の
/// 標準入力（パイプ、Seek不可）へ順次書き出す。
/// </summary>
internal sealed class RawStream : Stream
{
    private readonly MemoryStream inner = new();
    private readonly BlockingCollection<byte[]> queue = new(new ConcurrentQueue<byte[]>());

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);

        byte[] data = new byte[count];
        Array.Copy(buffer, offset, data, 0, count);
        try
        {
            queue.Add(data);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>キューに積まれたデータを、ffmpeg の標準入力へ順次書き出す。</summary>
    public async Task PumpToAsync(Stream destination, CancellationToken token)
    {
        try
        {
            foreach (byte[] chunk in queue.GetConsumingEnumerable(token))
            {
                await destination.WriteAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                destination.Close();
            }
            catch
            {
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { queue.CompleteAdding(); } catch { }
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
