using System.IO;

namespace PlayerRemoteControl.Transport;

// Serializes the advanced-avatar-settings blob (float[]/int[]/byte[]), the same params CVR syncs,
// including face tracking, OSC and contacts. Its own packet since it can be large. Heavy avatars may
// exceed the cap, so the controller only sends on change (ControllerCapturePatches).
internal static class AasCodec
{
    internal static byte[] Encode(float[] floats, int[] ints, byte[] bytes)
    {
        floats ??= System.Array.Empty<float>();
        ints ??= System.Array.Empty<int>();
        bytes ??= System.Array.Empty<byte>();

        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms);
        w.Write((byte)PacketType.Aas);
        w.Write((ushort)floats.Length);
        foreach (var f in floats) w.Write(f);
        w.Write((ushort)ints.Length);
        foreach (var i in ints) w.Write(i);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
        return ms.ToArray();
    }

    // Returns false on a malformed packet. Each declared length is checked against the bytes actually
    // left in the stream before allocating, so a tiny packet claiming huge counts can't force a large
    // allocation or throw.
    internal static bool Decode(byte[] payload, out float[] floats, out int[] ints, out byte[] bytes)
    {
        floats = System.Array.Empty<float>();
        ints = System.Array.Empty<int>();
        bytes = System.Array.Empty<byte>();
        try
        {
            using var ms = new MemoryStream(payload);
            using var r = new BinaryReader(ms);
            r.ReadByte(); // PacketType.Aas

            int lf = r.ReadUInt16();
            if ((long)lf * 4 > ms.Length - ms.Position) return false;
            floats = new float[lf];
            for (int i = 0; i < lf; i++) floats[i] = r.ReadSingle();

            int li = r.ReadUInt16();
            if ((long)li * 4 > ms.Length - ms.Position) return false;
            ints = new int[li];
            for (int i = 0; i < li; i++) ints[i] = r.ReadInt32();

            int lb = r.ReadUInt16();
            if (lb > ms.Length - ms.Position) return false;
            bytes = new byte[lb];
            for (int i = 0; i < lb; i++) bytes[i] = r.ReadByte();
            return true;
        }
        catch { return false; }
    }
}