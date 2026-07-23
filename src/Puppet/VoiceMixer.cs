using System;
using System.Collections.Concurrent;
using UnityEngine;
using ABI_RC.Systems.Communications.Audio;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Puppet;

// Down-mixes the controller's voice into the puppet's own outgoing mic frame so a single clean Opus
// stream leaves the puppet, instead of interleaving two packet streams through one voice slot (which
// the far end hears as choppy alternating audio).
// Controller frames are queued as they arrive (network thread). On the audio thread (CaptureMixPatch)
// the puppet's clean pre-mix frame is encoded for the side-channel monitor, then one decoded controller
// frame is added into the mic PCM before CVR encodes it. One OpusCodec, touched only from that thread.
internal static class VoiceMixer
{
    private const int MaxQueue = 4;                 // cap latency if network jitter piles frames up
    private static readonly ConcurrentQueue<byte[]> _pending = new();
    private static OpusCodec _codec;                // audio-thread only
    private static ushort _fwdSeq;                  // sequence for the side-channel forward

    // Network thread: queue a controller Opus frame to be mixed into the next mic frame.
    internal static void Enqueue(byte[] opusFrame)
    {
        if (opusFrame == null || opusFrame.Length == 0) return;
        _pending.Enqueue(opusFrame);
        while (_pending.Count > MaxQueue && _pending.TryDequeue(out _)) { }
    }

    internal static void Clear() { while (_pending.TryDequeue(out _)) { } }

    // Audio thread: add one queued controller frame into the mic PCM in place. Returns true
    // if a frame was mixed (so the caller can force the VAD gate open).
    internal static bool MixInto(float[] data)
    {
        if (data == null || !_pending.TryDequeue(out var opus)) return false;
        _codec ??= new OpusCodec(48000, 1);
        float[] ctrl;
        try { ctrl = _codec.Decode(opus, 1); } catch { return false; }
        if (ctrl == null) return false;
        int n = Math.Min(data.Length, ctrl.Length);
        for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(data[i] + ctrl[i], -1f, 1f);
        _codec.Release(ctrl);
        return true;
    }

    // Audio thread: encode the puppet's clean (pre-mix) frame as a PuppetVoice side-channel
    // packet for the controller's stereo monitor. Uses an internal running sequence.
    internal static byte[] BuildSideChannelPacket(float[] data)
    {
        if (data == null) return null;
        _codec ??= new OpusCodec(48000, 1);
        byte[] opus;
        try { opus = _codec.Encode(data, 1); } catch { return null; }
        if (opus == null) return null;
        var payload = VoiceCodec.Encode(_fwdSeq++, opus, PacketType.PuppetVoice);
        _codec.Release(opus);
        return payload;
    }
}