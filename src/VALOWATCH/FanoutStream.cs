using System.Collections.Concurrent;

namespace VALOWATCH;

/// <summary>
/// ScreenRecorderLib が書き込む fMP4 を受け取り、
/// ・初期化セグメント(ftyp+moov)を保持
/// ・以降のデータを、購読中の全クライアントへ配信（fan-out）
/// する Stream。
///
/// fMP4 のライブ配信では、後から接続したクライアントにも
/// 「初期化セグメント → 途中から」のデータを送る必要がある。
/// このクラスは、初期化セグメントを覚えておき、新規接続に渡す。
///
/// ScreenRecorderLib は Seek/Length/Position を使うため、
/// 内部に MemoryStream を持って完全対応しつつ、Write を横取りして配信する。
/// </summary>
internal sealed class FanoutStream : Stream
{
    private readonly MemoryStream inner = new();
    private readonly object gate = new();
    private readonly List<Subscriber> subscribers = new();

    // 初期化セグメント（先頭の ftyp+moov）。最初の moof が来るまでの累積。
    private byte[]? initSegment;
    private readonly MemoryStream initBuffer = new();
    private bool initCaptured;

    public byte[]? GetInitSegment()
    {
        lock (gate)
        {
            return initSegment;
        }
    }

    public Subscriber Subscribe()
    {
        var subscriber = new Subscriber();
        lock (gate)
        {
            subscribers.Add(subscriber);
        }

        return subscriber;
    }

    public void Unsubscribe(Subscriber subscriber)
    {
        lock (gate)
        {
            subscribers.Remove(subscriber);
        }

        subscriber.Complete();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        // 内部 MemoryStream にも書く（ライブラリが Seek/読み戻しする場合に対応）。
        inner.Write(buffer, offset, count);

        byte[] data = new byte[count];
        Array.Copy(buffer, offset, data, 0, count);

        lock (gate)
        {
            if (!initCaptured)
            {
                // 最初の 'moof' ボックスが現れるまでを初期化セグメント(ftyp+moov)とみなす。
                initBuffer.Write(data, 0, data.Length);
                int moofIndex = FindFirstMoofBoxStart(initBuffer.GetBuffer(), (int)initBuffer.Length);
                if (moofIndex >= 0)
                {
                    // moof より前が初期化セグメント。
                    initSegment = new byte[moofIndex];
                    Array.Copy(initBuffer.GetBuffer(), 0, initSegment, 0, moofIndex);
                    initCaptured = true;

                    // moof 以降は、通常のメディアデータとして配信。
                    int remaining = (int)initBuffer.Length - moofIndex;
                    if (remaining > 0)
                    {
                        byte[] mediaTail = new byte[remaining];
                        Array.Copy(initBuffer.GetBuffer(), moofIndex, mediaTail, 0, remaining);
                        PublishToSubscribers(mediaTail);
                    }

                    initBuffer.SetLength(0);
                }

                return;
            }

            PublishToSubscribers(data);
        }
    }

    private void PublishToSubscribers(byte[] data)
    {
        foreach (Subscriber subscriber in subscribers)
        {
            subscriber.Enqueue(data);
        }
    }

    /// <summary>
    /// MP4 のトップレベルボックスを正しく辿り、最初の 'moof' ボックスの開始位置を返す。
    /// ボックス構造：[4バイト BE サイズ][4バイト タイプ][データ...]。
    /// サイズを読んで次のボックスへ進むので、moov の内部に偶然現れる 'moof' 文字列を
    /// 誤検出しない。完全な moof に到達していなければ -1。
    /// </summary>
    private static int FindFirstMoofBoxStart(byte[] buffer, int length)
    {
        int pos = 0;
        while (pos + 8 <= length)
        {
            long boxSize =
                ((long)buffer[pos] << 24) |
                ((long)buffer[pos + 1] << 16) |
                ((long)buffer[pos + 2] << 8) |
                buffer[pos + 3];

            bool isMoof =
                buffer[pos + 4] == (byte)'m' &&
                buffer[pos + 5] == (byte)'o' &&
                buffer[pos + 6] == (byte)'o' &&
                buffer[pos + 7] == (byte)'f';

            if (isMoof)
            {
                return pos;
            }

            if (boxSize < 8)
            {
                // サイズ0や不正（64bitサイズ拡張など）は、ここでは扱わず打ち切り。
                return -1;
            }

            pos += (int)boxSize;
        }

        return -1;
    }

    // ==== Stream の Seek 等（ScreenRecorderLib が使うため完全対応）====
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
            lock (gate)
            {
                foreach (Subscriber subscriber in subscribers)
                {
                    subscriber.Complete();
                }

                subscribers.Clear();
            }

            inner.Dispose();
            initBuffer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>1クライアント分の配信キュー。</summary>
    internal sealed class Subscriber
    {
        private readonly BlockingCollection<byte[]> queue = new(new ConcurrentQueue<byte[]>());

        public void Enqueue(byte[] data)
        {
            try
            {
                queue.Add(data);
            }
            catch (InvalidOperationException)
            {
                // 完了済み。
            }
        }

        public async Task<byte[]?> TakeAsync(CancellationToken token)
        {
            try
            {
                return await Task.Run(() => queue.Take(token), token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Complete()
        {
            try
            {
                queue.CompleteAdding();
            }
            catch
            {
            }
        }
    }
}
