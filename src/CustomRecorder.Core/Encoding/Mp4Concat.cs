using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

/// <summary>
/// Joins video-only MP4 segments that share the same encoding settings into one file without re-encoding
/// (compressed H.264 samples are copied, only their timestamps are shifted), and optionally adds an audio track
/// encoded once from raw PCM.
/// </summary>
public static class Mp4Concat
{
    public sealed record Part(string Path, long Offset100ns);

    /// <summary>16-bit interleaved PCM chunks with timestamps relative to the start of the output.</summary>
    public sealed record PcmTrack(AudioEncodeSettings Settings, IReadOnlyList<(long Time, byte[] Pcm)> Chunks);

    private static readonly Guid SampleDiscontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a"); // MFSampleExtension_Discontinuity

    public static void Join(IReadOnlyList<Part> parts, string outputPath, PcmTrack? audio = null)
    {
        if (parts.Count == 0) throw new ArgumentException("합칠 파일이 없습니다.", nameof(parts));
        MediaFactory.MFStartup(true).CheckError();

        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4).CheckError();
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null!, attributes);

        // Video stream layout from the first part (pass-through: same type in and out).
        var map = new Dictionary<int, int>(); // reader stream index -> writer stream index
        using (var first = MediaFactory.MFCreateSourceReaderFromURL(parts[0].Path, null!))
        {
            for (int i = 0; ; i++)
            {
                IMFMediaType type;
                try { type = first.GetNativeMediaType(i, 0); }
                catch (SharpGenException) { break; } // no more streams
                using (type)
                {
                    int ws = writer.AddStream(type);
                    writer.SetInputMediaType(ws, type, null!);
                    map[i] = ws;
                }
            }
        }

        int audioStream = audio != null ? AddAacStream(writer, audio.Settings) : -1;

        writer.BeginWriting();
        long written = 0;
        int audioIndex = 0;
        long lastVideoEnd = long.MinValue;

        foreach (var part in parts)
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(part.Path, null!);
            foreach (var i in map.Keys) reader.SetStreamSelection(i, true);
            var ended = new HashSet<int>();

            while (ended.Count < map.Count)
            {
                using var sample = reader.ReadSample(SourceReaderIndex.AnyStream, SourceReaderControlFlag.None,
                    out int stream, out var flags, out long timestamp);
                if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException($"읽기 오류: {part.Path}");

                if (sample != null && map.TryGetValue(stream, out int ws))
                {
                    long t = Math.Max(timestamp + part.Offset100ns, lastVideoEnd == long.MinValue ? 0 : lastVideoEnd);
                    sample.SampleTime = t;
                    sample.DeleteItem(SampleDiscontinuity);

                    // Keep the two tracks interleaved: write audio up to slightly past this video frame.
                    if (audio != null) audioIndex = WriteAudioUntil(writer, audioStream, audio, audioIndex, t + 5_000_000);

                    writer.WriteSample(ws, sample);
                    lastVideoEnd = t + sample.SampleDuration;
                    written++;
                }
                if ((flags & SourceReaderFlag.EndOfStream) != 0) ended.Add(stream);
            }
        }

        // Remaining audio, cut at the end of the video.
        if (audio != null) audioIndex = WriteAudioUntil(writer, audioStream, audio, audioIndex, lastVideoEnd);

        writer.Finalize();
        Log.Info($"리플레이 저장: {outputPath} ({parts.Count}개 구간, 영상 샘플 {written}개, 오디오 {audioIndex * 10}ms 분량)");
    }

    private static int AddAacStream(IMFSinkWriter writer, AudioEncodeSettings a)
    {
        int ws;
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)a.SampleRate).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)a.Channels).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)a.AacBytesPerSecond).CheckError();
            ws = writer.AddStream(outType);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            int blockAlign = a.Channels * 2;
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)a.SampleRate).CheckError();
            inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)a.Channels).CheckError();
            inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)blockAlign).CheckError();
            inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(blockAlign * a.SampleRate)).CheckError();
            writer.SetInputMediaType(ws, inType, null!);
        }
        return ws;
    }

    private static int WriteAudioUntil(IMFSinkWriter writer, int stream, PcmTrack audio, int index, long until)
    {
        var a = audio.Settings;
        while (index < audio.Chunks.Count && audio.Chunks[index].Time < until)
        {
            var (time, pcm) = audio.Chunks[index++];
            if (time < 0) continue;
            int frames = pcm.Length / (a.Channels * 2);

            using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
            buffer.Lock(out var ptr, out _, out _);
            try { System.Runtime.InteropServices.Marshal.Copy(pcm, 0, ptr, pcm.Length); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = pcm.Length;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = frames * 10_000_000L / a.SampleRate;
            writer.WriteSample(stream, sample);
        }
        return index;
    }
}
