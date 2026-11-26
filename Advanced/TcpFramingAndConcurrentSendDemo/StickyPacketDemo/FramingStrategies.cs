using FramingLib;

namespace StickyPacketDemo;

/// <summary>
/// 反面教材：不做任何分帧，默认"一次 Receive 就是一条消息"。
///
/// 这是最常见的入门误解。它在本机、小数据量、低频发送时往往"看起来能跑"，
/// 因为每条消息恰好各自占用一次读取；一旦发送变快或消息变大就会立刻错乱。
/// </summary>
public sealed class NaiveFramer : IFramingStrategy
{
    private readonly Queue<byte[]> _chunks = new();

    public string Name => "朴素(无分帧)";

    public string Note => "把一次读取当作一条消息，粘包半包下必然错乱";

    public int PendingBytes => 0;//从不保留半包，读到什么就当成什么

    public byte[] Encode(byte[] payload) => payload;

    public void Append(ReadOnlySpan<byte> data) => _chunks.Enqueue(data.ToArray());

    public bool TryDequeue(out byte[] payload)
    {
        if (_chunks.Count > 0)
        {
            payload = _chunks.Dequeue();
            return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }
}

/// <summary>
/// 固定长度分帧：每条消息都补齐到同一长度。
/// 实现最简单，但长度必须按最大消息来定，短消息会浪费大量带宽，
/// 而且一旦出现超长消息就彻底放不下。
/// </summary>
public sealed class FixedLengthFramer : IFramingStrategy
{
    private readonly int _frameSize;
    private readonly List<byte> _buffer = new();

    public FixedLengthFramer(int frameSize)
    {
        _frameSize = frameSize;
    }

    public string Name => $"固定长度({_frameSize}B)";

    public string Note => "实现最简单，但按最大消息补齐，短消息浪费严重";

    public int PendingBytes => _buffer.Count;

    public byte[] Encode(byte[] payload)
    {
        if (payload.Length > _frameSize)
        {
            throw new ArgumentException($"消息长度 {payload.Length} 超出固定帧长 {_frameSize}");
        }
        var frame = new byte[_frameSize];
        payload.CopyTo(frame, 0);

        //末尾补 0，解码时需要把填充去掉，所以这个方案不能传输以 0 结尾的二进制内容
        return frame;
    }

    public void Append(ReadOnlySpan<byte> data) => _buffer.AddRange(data.ToArray());

    public bool TryDequeue(out byte[] payload)
    {
        if (_buffer.Count < _frameSize)
        {
            payload = Array.Empty<byte>();
            return false;
        }

        var frame = _buffer.GetRange(0, _frameSize);
        _buffer.RemoveRange(0, _frameSize);

        var end = frame.Count;
        while (end > 0 && frame[end - 1] == 0)
        {
            end--;
        }
        payload = frame.GetRange(0, end).ToArray();
        return true;
    }
}

/// <summary>
/// 分隔符分帧：消息之间插入一个约定的结束符。
/// 适合纯文本协议（HTTP 头、Redis RESP 都是这个思路），
/// 但内容里一旦出现分隔符就会被误切，必须转义，因此不是二进制安全的。
/// </summary>
public sealed class DelimiterFramer : IFramingStrategy
{
    private readonly byte _delimiter;
    private readonly List<byte> _buffer = new();

    public DelimiterFramer(byte delimiter)
    {
        _delimiter = delimiter;
    }

    public string Name => "分隔符";

    public string Note => "适合文本协议；内容含分隔符就会被误切，需转义";

    public int PendingBytes => _buffer.Count;

    public byte[] Encode(byte[] payload)
    {
        var frame = new byte[payload.Length + 1];
        payload.CopyTo(frame, 0);
        frame[^1] = _delimiter;
        return frame;
    }

    public void Append(ReadOnlySpan<byte> data) => _buffer.AddRange(data.ToArray());

    public bool TryDequeue(out byte[] payload)
    {
        var index = _buffer.IndexOf(_delimiter);
        if (index < 0)
        {
            payload = Array.Empty<byte>();
            return false;
        }
        payload = _buffer.GetRange(0, index).ToArray();
        _buffer.RemoveRange(0, index + 1);
        return true;
    }
}

/// <summary>
/// 长度前缀分帧：帧头里写明负载长度，接收端先读头再按长度取内容。
/// 二进制安全、无需转义、无需扫描，是绝大多数自定义协议的选择。
///
/// 这里直接复用 <see cref="FramingLib"/> 里那套帧格式，
/// 也就是并发写 Demo 用的同一份实现——可见粘包与交织虽然症状相似，
/// 但前者靠分帧就能彻底解决，后者分帧再好也救不回来。
/// </summary>
public sealed class LengthPrefixFramer : IFramingStrategy
{
    private readonly FrameReader _reader = new();

    public string Name => "长度前缀";

    public string Note => "二进制安全、无需扫描，推荐方案";

    public int PendingBytes => _reader.PendingBytes;

    public byte[] Encode(byte[] payload) => FrameCodec.Encode(type: 0, payload);

    public void Append(ReadOnlySpan<byte> data) => _reader.Append(data);

    public bool TryDequeue(out byte[] payload)
    {
        if (_reader.TryDequeue(out var frame))
        {
            payload = frame.Payload;
            return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }
}
