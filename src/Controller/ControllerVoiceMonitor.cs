using System;
using System.Collections.Generic;
using UnityEngine;
using ABI_RC.Systems.Communications.Audio;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Controller;

// Controller-side monitor for the puppet's own voice on the PuppetVoice side-channel. The puppet's
// real stream is the fused mix (puppet + controller), which is muted to avoid self-echo, so this is
// how the controller hears just the puppet, in stereo (2D).
// Decodes the Opus frames directly into a streaming AudioClip on a 2D AudioSource, rather than
// standing up a CVR participant pipeline (which needs wiring that didn't drive sound on a bare
// instance). The PCM reader callback pulls from a buffered queue and outputs silence on underrun.
// Unity objects are created on the main thread; OnFrame only decodes and enqueues.
internal sealed class ControllerVoiceMonitor
{
    private const int SampleRate = 48000;
    private const int MaxBuffered = SampleRate;   // 1s latency cap

    private GameObject _go;
    private AudioSource _source;
    private OpusCodec _codec;
    private readonly Queue<float> _buffer = new();
    private readonly object _lock = new();
    private int _framesReceived;

    internal ControllerVoiceMonitor(string puppetUuid)
    {
        try
        {
            _codec = new OpusCodec(SampleRate, 1);
            _go = new GameObject("PRC_PuppetVoiceMonitor");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _source = _go.AddComponent<AudioSource>();
            _source.clip = AudioClip.Create("PRC_PuppetVoice", SampleRate, 1, SampleRate, true, OnPcmRead);
            _source.loop = true;
            _source.spatialBlend = 0f; // stereo / 2D
            _source.priority = 0;
            _source.Play();
        }
        catch (Exception e)
        {
            PlayerRemoteControlMod.Log.Warning($"Puppet-voice monitor init failed: {e.Message}");
        }
    }

    internal void OnFrame(byte[] payload)
    {
        if (_codec == null) return;
        if (!VoiceCodec.Decode(payload, out _, out byte[] opus)) return;
        float[] pcm;
        try { pcm = _codec.Decode(opus, 1); } catch { return; }
        if (pcm == null) return;

        lock (_lock)
        {
            foreach (var s in pcm) _buffer.Enqueue(s);
            while (_buffer.Count > MaxBuffered) _buffer.Dequeue();
        }
        _codec.Release(pcm);

        if (_framesReceived++ == 0) PlayerRemoteControlMod.Log.Msg("Puppet-voice monitor: receiving audio.");
    }

    // Audio thread: fill from the buffered queue, silence on underrun.
    private void OnPcmRead(float[] data)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = _buffer.Count > 0 ? _buffer.Dequeue() : 0f;
        }
    }

    internal void Dispose()
    {
        if (_source != null) _source.Stop();
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _codec?.Dispose();
        _go = null; _source = null; _codec = null;
        lock (_lock) _buffer.Clear();
    }
}