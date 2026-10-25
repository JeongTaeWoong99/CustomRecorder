using CustomRecorder.Core.Util;
using NAudio.Wave;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

public enum AudioFileFormat { M4a, Mp3, Flac, Wav }

/// <summary>Writes 16-bit PCM into a sound-only file.</summary>
public sealed class AudioFileWriter : IDisposable
{
    private readonly IMFSinkWriter? _mf;
    private readonly WaveFileWriter? _wav;
    private readonly AudioEncodeSettings _settings;
    private bool _finished;

    public static string Extension(AudioFileFormat f) => f switch
    {
        AudioFileFormat.Mp3 => "mp3",
        AudioFileFormat.Flac => "flac",
        AudioFileFormat.Wav => "wav",
        _ => "m4a",
    };

    public AudioFileWriter(string path, AudioFileFormat format, AudioEncodeSettings settings)
    {
        _settings = settings;
        if (format == AudioFileFormat.Wav)
        {
            _wav = new WaveFileWriter(path, new WaveFormat(settings.SampleRate, 16, settings.Channels));
            Log.Info($"녹음 시작: {path} WAV");
            return;
        }

        MediaFactory.MFStartup(true).CheckError();
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, format switch
        {
            AudioFileFormat.Mp3 => TranscodeContainerTypeGuids.Mp3,
            AudioFileFormat.Flac => TranscodeContainerTypeGuids.Flac,
            _ => TranscodeContainerTypeGuids.Mpeg4,
        }).CheckError();
        _mf = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        try
        {
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)settings.SampleRate).CheckError();
                outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)settings.Channels).CheckError();
                switch (format)
                {
                    case AudioFileFormat.Mp3:
                        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Mp3).CheckError();
                        outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u).CheckError(); // 192 kbps
                        outType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u).CheckError();
                        break;
                    case AudioFileFormat.Flac:
                        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Flac).CheckError();
                        outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                        break;
                    default:
                        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
                        outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                        outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u).CheckError();
                        break;
                }
                _mf.AddStream(outType);
            }

            using (var inType = MediaFactory.MFCreateMediaType())
            {
                int blockAlign = settings.Channels * 2;
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)settings.SampleRate).CheckError();
                inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)settings.Channels).CheckError();
                inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)blockAlign).CheckError();
                inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(blockAlign * settings.SampleRate)).CheckError();
                _mf.SetInputMediaType(0, inType, null!);
            }
            _mf.BeginWriting();
        }
        catch
        {
            _mf.Dispose();
            try { File.Delete(path); } catch { }
            throw new NotSupportedException($"이 PC에서 {Extension(format).ToUpperInvariant()} 녹음을 지원하지 않습니다. 다른 형식을 선택하세요.");
        }
        Log.Info($"녹음 시작: {path} {format}");
    }

    public void Write(ReadOnlySpan<byte> pcm, long time100ns)
    {
        if (_finished || pcm.Length == 0) return;
        if (_wav != null) { _wav.Write(pcm); return; }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var ptr, out _, out _);
        try { unsafe { pcm.CopyTo(new Span<byte>((void*)ptr, pcm.Length)); } }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = pcm.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time100ns;
        sample.SampleDuration = pcm.Length / (_settings.Channels * 2) * 10_000_000L / _settings.SampleRate;
        _mf!.WriteSample(0, sample);
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _wav?.Dispose();
        _mf?.Finalize();
    }

    public void Dispose()
    {
        try { Finish(); } catch (Exception ex) { Log.Error("녹음 파일 마무리 실패", ex); }
        _mf?.Dispose();
    }
}
