using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;

namespace SpawnTimer.Core;

/// <summary>Generated beeps (no sound files to ship) and optional Windows text-to-speech.</summary>
public sealed class Alerts
{
    private readonly SoundPlayer _spawn = MakeTone((880, 140), (0, 70), (880, 140), (0, 70), (1175, 280));
    private readonly SoundPlayer _warn = MakeTone((660, 180));
    private readonly SoundPlayer _drop = MakeTone((784, 90), (988, 90), (1319, 240));
    private readonly SoundPlayer _named = MakeTone((523, 160), (0, 60), (523, 160), (0, 60), (392, 320));
    private object? _voice;

    public void Drop(DropAlertSettings settings, string item)
    {
        if (settings.Sound) _drop.Play();
        if (settings.Speak) Speak($"{item} dropped");
    }

    public void Named(NamedSettings settings, string speech)
    {
        if (settings.Sound) _named.Play();
        if (settings.Speak) Speak(speech);
    }

    public void Spawn(AlertSettings settings, string timerName)
    {
        if (settings.SoundOnSpawn) _spawn.Play();
        if (settings.SpeakOnSpawn) Speak($"{timerName} is up");
    }

    public void Warn(AlertSettings settings)
    {
        if (settings.SoundOnWarning) _warn.Play();
    }

    public void Speak(string text)
    {
        try
        {
            if (_voice == null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type == null) return;
                _voice = Activator.CreateInstance(type);
            }
            const int SpeakAsync = 1;
            ((dynamic)_voice!).Speak(text, SpeakAsync);
        }
        catch (COMException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }

    private static SoundPlayer MakeTone(params (int Hz, int Ms)[] parts)
    {
        const int rate = 22050;
        const double volume = 0.35;
        var samples = new MemoryStream();
        var writer = new BinaryWriter(samples);
        foreach (var (hz, ms) in parts)
        {
            var count = rate * ms / 1000;
            var fade = Math.Min(count / 2, rate / 200); // 5 ms fade to avoid clicks
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, count - 1 - i) / (double)Math.Max(1, fade));
                var value = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * volume * envelope;
                writer.Write((short)(value * short.MaxValue));
            }
        }

        var data = samples.ToArray();
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(rate);
        w.Write(rate * 2);      // byte rate
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits per sample
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        wav.Position = 0;

        var player = new SoundPlayer(wav);
        player.Load();
        return player;
    }
}
