using System.IO;

namespace PlayerRemoteControl.Transport;

// One CVR Opus voice frame, captured off Comms_Client.SendVoiceData. Small enough (tens to low
// hundreds of bytes) to fit the ModNetwork cap and relay controller→puppet with no external backend.
internal static class VoiceCodec
{
    internal static byte[] Encode(ushort sequence, byte[] data, PacketType type)
    {
        data ??= System.Array.Empty<byte>();
        using var ms = new MemoryStream(data.Length + 8);
        using var w = new BinaryWriter(ms);
        w.Write((byte)type);
        w.Write(sequence);
        w.Write((ushort)data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    // Returns false on a malformed packet (declared length longer than the bytes present).
    internal static bool Decode(byte[] payload, out ushort sequence, out byte[] data)
    {
        sequence = 0;
        data = System.Array.Empty<byte>();
        try
        {
            using var ms = new MemoryStream(payload);
            using var r = new BinaryReader(ms);
            r.ReadByte(); // packet type
            sequence = r.ReadUInt16();
            int len = r.ReadUInt16();
            if (len > ms.Length - ms.Position) return false;
            data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = r.ReadByte();
            return true;
        }
        catch { return false; }
    }
}