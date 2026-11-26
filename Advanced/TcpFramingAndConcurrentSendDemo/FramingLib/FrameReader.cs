using System.Buffers.Binary;

namespace FramingLib;

/// <summary>一条解析出来的完整帧</summary>
public readonly record struct Frame(int Type, byte[] Payload);

/// <summary>
/// 接收端的分帧状态机，负责把"字节流"还原成"消息流"。
///
/// 需要处理四种情况：
///   1. 粘包：一次 Receive 里有多帧，循环切直到切不动
///   2. 半包：帧没收全，保留在缓冲区等后续数据
///   3. 脏数据：开头对不上 Magic，扫描到下一个结束符重新对齐
///   4. 错帧：长度字段指向的位置不是结束符，说明内容已经和别的帧交织错位
///
/// 第 4 种最隐蔽：如果不校验结束符，错位的脏数据会被当成正常内容交给上层，
/// 上层解析（JSON/Protobuf）才报错，排查时只能看到一坨看不懂的报文。
/// </summary>
public sealed class FrameReader
{
    private readonly byte[] _buffer;
    private int _start;
    private int _end;

    public FrameReader(int capacity = 1024 * 1024)
    {
        _buffer = new byte[capacity];
    }

    /// <summary>因结束符校验失败或长度字段非法而被丢弃的帧数</summary>
    public int CorruptFrameCount { get; private set; }

    /// <summary>重新同步时跳过的字节数</summary>
    public long DiscardedByteCount { get; private set; }

    /// <summary>缓冲区里尚未凑成完整帧的字节数，也就是"半包残留"</summary>
    public int PendingBytes => _end - _start;

    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureSpace(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>
    /// 尝试从缓冲区切出一帧。返回 false 表示当前数据不足，等待下一次 Append
    /// </summary>
    public bool TryDequeue(out Frame frame)
    {
        frame = default;
        while (true)
        {
            var available = _end - _start;
            if (available < FrameCodec.HeaderLength + FrameCodec.TailLength)
            {
                return false;//半包，连帧头都不够
            }

            var window = _buffer.AsSpan(_start, available);
            if (!window.StartsWith(FrameCodec.Magic))
            {
                if (!Resync())
                {
                    return false;
                }
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(window[4..]);
            if (payloadLength < 0 || payloadLength > FrameCodec.MaxPayloadLength)
            {
                CorruptFrameCount++;
                if (!Resync())
                {
                    return false;
                }
                continue;
            }

            var total = FrameCodec.HeaderLength + payloadLength + FrameCodec.TailLength;
            if (available < total)
            {
                return false;//半包，内容还没收全
            }

            if (window[FrameCodec.HeaderLength + payloadLength] != FrameCodec.TailSymbol)
            {
                //按长度字段走到帧尾却不是结束符，说明这一帧的内容中间被其它数据插入了
                CorruptFrameCount++;
                if (!Resync())
                {
                    return false;
                }
                continue;
            }

            var type = BinaryPrimitives.ReadInt32LittleEndian(window[8..]);
            var payload = window.Slice(FrameCodec.HeaderLength, payloadLength).ToArray();
            _start += total;
            frame = new Frame(type, payload);
            return true;
        }
    }

    /// <summary>
    /// 跳过当前这段坏数据，把游标移到下一个结束符之后重新对齐。
    /// 返回 false 表示剩余数据里还没出现结束符，需要等更多数据
    /// </summary>
    private bool Resync()
    {
        //从第 1 个字节开始找，避免当前位置本身就是结束符时原地踏步
        var searchFrom = _start + 1;
        for (var i = searchFrom; i < _end; i++)
        {
            if (_buffer[i] != FrameCodec.TailSymbol)
            {
                continue;
            }
            DiscardedByteCount += i + 1 - _start;
            _start = i + 1;
            return true;
        }

        DiscardedByteCount += _end - _start;
        _start = _end;//整段都没有结束符，全部丢弃
        return false;
    }

    private void EnsureSpace(int needed)
    {
        if (_end + needed <= _buffer.Length)
        {
            return;
        }

        //把未处理完的半包前移，腾出尾部空间，避免缓冲区被游标推着单向增长
        var remaining = _end - _start;
        Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
        _start = 0;
        _end = remaining;

        if (_end + needed > _buffer.Length)
        {
            //单帧超过缓冲区容量，再等也凑不齐，只能整段丢弃
            DiscardedByteCount += _end;
            _start = 0;
            _end = 0;
        }
    }
}
