using System;
using System.Security.Cryptography;

namespace PlayerRemoteControl.Transport;

// Per-session authentication token. After the handshake, every control packet carries the token the
// puppet issued in its AcceptControl reply. A client that only spoofs a sender UUID can't inject into
// a live session: the AcceptControl is addressed to the real controller, so the spoofer never receives
// it and never learns the token. Authenticated layout: [type][token: 4 little-endian bytes][body...].
internal static class ControlPacket
{
    internal static uint NewToken()
    {
        var b = new byte[4];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        uint t = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        return t == 0 ? 1u : t; // 0 doubles as "no token yet"
    }

    // Insert the token after the type byte, keeping the rest of the payload intact.
    internal static byte[] Wrap(byte[] payload, uint token)
    {
        var outp = new byte[payload.Length + 4];
        outp[0] = payload.Length > 0 ? payload[0] : (byte)0;
        outp[1] = (byte)token;
        outp[2] = (byte)(token >> 8);
        outp[3] = (byte)(token >> 16);
        outp[4] = (byte)(token >> 24);
        if (payload.Length > 1) Array.Copy(payload, 1, outp, 5, payload.Length - 1);
        return outp;
    }

    // Validate the token and hand back the original [type][body] so existing codecs decode unchanged.
    internal static bool Unwrap(byte[] payload, uint expected, out byte[] inner)
    {
        inner = null;
        if (expected == 0 || payload == null || payload.Length < 5) return false;
        uint token = (uint)(payload[1] | (payload[2] << 8) | (payload[3] << 16) | (payload[4] << 24));
        if (token != expected) return false;
        inner = new byte[payload.Length - 4];
        inner[0] = payload[0];
        Array.Copy(payload, 5, inner, 1, payload.Length - 5);
        return true;
    }
}
