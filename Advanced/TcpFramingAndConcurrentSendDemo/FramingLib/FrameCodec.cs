using System.Buffers.Binary;

namespace FramingLib;

/// <summary>
/// 分帧协议的编码与常量。
///
/// TCP 只保证"字节有序到达"，不保证"一次 Send 对应一次 Receive"：
/// 多次 Send 的内容可能被合并成一次 Receive（粘包），
/// 一次 Send 的内容也可能被拆成多次 Receive（半包）。
/// 所以必须在应用层自带边界信息，接收端才能还原出一条完整消息。
///
/// 帧结构：Magic(4) + PayloadLength(4) + Type(4) + Payload(N) + Tail(1)
/// 既有长度字段又有结束符，是因为两者承担不同职责：
///   长度字段负责"切得快"，结束符负责"切得对"——用来校验这一帧是否真的完整。
/// </summary>
public static class FrameCodec
{
    public const int HeaderLength = 12;

    public const int TailLength = 1;

    /// <summary>帧结束符，同时用于帧完整性校验和错帧后的重新同步</summary>
    public const byte TailSymbol = (byte)'$';

    /// <summary>
    /// 长度字段的合法上限。
    /// 长度字段一旦被污染会变成一个巨大的数，没有这个上限，
    /// 接收端会一直等"永远凑不齐的一帧"，直到缓冲区被撑爆
    /// </summary>
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    private static readonly byte[] MagicBytes = "DEMO"u8.ToArray();

    public static ReadOnlySpan<byte> Magic => MagicBytes;

    public static byte[] Encode(int type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length + TailLength];
        MagicBytes.CopyTo(frame.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), type);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        frame[^1] = TailSymbol;
        return frame;
    }
}
