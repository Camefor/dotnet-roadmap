namespace StickyPacketDemo;

/// <summary>
/// 一种分帧方案。发送端用 <see cref="Encode"/> 把消息变成线上字节，
/// 接收端把读到的字节 <see cref="Append"/> 进来，再反复 <see cref="TryDequeue"/> 取出完整消息。
///
/// 接收端之所以必须是"喂字节 + 反复取"的形态，正是因为 TCP 是字节流：
/// 一次读取里可能有多条消息（粘包），也可能只有半条（半包），
/// 读取边界和消息边界之间没有任何关系。
/// </summary>
public interface IFramingStrategy
{
    string Name { get; }

    /// <summary>方案的适用性说明，用于对比输出</summary>
    string Note { get; }

    /// <summary>缓冲区里尚未凑成完整消息的字节数，也就是"半包残留"</summary>
    int PendingBytes { get; }

    byte[] Encode(byte[] payload);

    void Append(ReadOnlySpan<byte> data);

    bool TryDequeue(out byte[] payload);
}
