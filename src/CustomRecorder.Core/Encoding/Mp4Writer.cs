using System.Runtime.InteropServices;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

/// <param name="Width">Encoded frame size (frames are scaled to this size before they reach the writer).</param>
/// <param name="KeyframeSeconds">GOP length; 0 = encoder default.</param>
public sealed record VideoEncodeSettings(int Width, int Height, int Fps, int BitrateBps,
    VideoCodec Codec = VideoCodec.H264, int KeyframeSeconds = 2);

/// <param name="Tracks">Number of AAC tracks (1 = mix only; 3 = mix, system, microphone).</param>
public sealed record AudioEncodeSettings(int SampleRate = 48000, int Channels = 2, int AacBytesPerSecond = 24000, int Tracks = 1);

/// <summary>
/// Writes H.264/HEVC + AAC into (fragmented) MP4 through the Media Foundation sink writer.
/// The sink writer picks the GPU encoder (NVENC / Quick Sync / AMF) automatically when a D3D manager is attached,
/// and converts BGRA to NV12 on the GPU. Fragmented MP4 stays playable even if the process is killed mid-recording.
/// </summary>
public sealed class Mp4Writer : IMediaSink, CustomRecorder.Core.Recording.IEncodeProgress
{
    private static int _mfStarted;

    private readonly object _writeLock = new();
    private readonly IMFSinkWriter _writer = null!;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly int _videoStream;
    private readonly int[] _audioStreams = Array.Empty<int>();
    private readonly AudioEncodeSettings? _audio;
    private bool _finished;

    public string Path { get; }

    /// <summary>True when the file is a fragmented MP4 (crash-safe while recording).</summary>
    public bool Fragmented { get; private set; }

    /// <summary>The settings actually in use (after any fallback).</summary>
    public VideoEncodeSettings Video { get; }

    // Codec API properties (codecapi.h), applied to the encoder by the sink writer.
    private static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private const uint RateControlUnconstrainedVbr = 2;

    private static int AddVideoStream(IMFSinkWriter writer, VideoEncodeSettings video, bool withExtras)
    {
        int stream;
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            outType.Set(MediaTypeAttributeKeys.Subtype, EncoderInfo.Subtype(video.Codec)).CheckError();
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)video.BitrateBps).CheckError();
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* MFVideoInterlace_Progressive */).CheckError();
            // eAVEncH264VProfile_High = 100, eAVEncH265VProfile_Main_420_8 = 1
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, video.Codec == VideoCodec.Hevc ? 1u : 100u).CheckError();
            MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)video.Fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            stream = writer.AddStream(outType);
        }

        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32).CheckError();
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        MediaFactory.MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)video.Width, (uint)video.Height).CheckError();
        MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)video.Fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();

        if (withExtras)
        {
            using var encoderParams = MediaFactory.MFCreateAttributes(3);
            encoderParams.Set(AVEncCommonRateControlMode, RateControlUnconstrainedVbr).CheckError();
            encoderParams.Set(AVEncMPVDefaultBPictureCount, 0u).CheckError(); // simpler timestamps, easier editing
            if (video.KeyframeSeconds > 0)
                encoderParams.Set(AVEncMPVGOPSize, (uint)(video.KeyframeSeconds * video.Fps)).CheckError();
            writer.SetInputMediaType(stream, inType, encoderParams);
        }
        else
        {
            writer.SetInputMediaType(stream, inType, null!);
        }
        return stream;
    }
    public bool HasAudio => _audioStreams.Length > 0;

    public Mp4Writer(string path, D3DContext d3d, VideoEncodeSettings video, AudioEncodeSettings? audio, bool fragmented = true)
    {
        Path = path;
        _audio = audio;
        EnsureStarted();

        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(d3d.Device).CheckError();

        // Encoder parameters (GOP / rate control) and fragmented MP4 are not supported by every encoder/driver
        // combination, and some failures only show up in BeginWriting: try configurations from best to safest.
        // (The Windows fragmented-MP4 sink does not accept HEVC, so HEVC ends up in a regular MP4.)
        var attempts = fragmented
            ? new List<(bool Extras, bool Fragmented)> { (true, true), (true, false), (false, true), (false, false) }
            : new List<(bool Extras, bool Fragmented)> { (true, false), (false, false) };
        if (fragmented && video.Codec == VideoCodec.Hevc) attempts.RemoveAll(a => a.Fragmented);

        Exception? last = null;
        foreach (var (extras, frag) in attempts)
        {
            IMFSinkWriter? writer = null;
            try
            {
                using var attributes = MediaFactory.MFCreateAttributes(4);
                attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u).CheckError();
                attributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager).CheckError();
                attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, frag ? TranscodeContainerTypeGuids.Fmpeg4 : TranscodeContainerTypeGuids.Mpeg4).CheckError();

                writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
                int videoStream = AddVideoStream(writer, video, extras);
                var audioStreams = audio != null
                    ? Enumerable.Range(0, Math.Max(1, audio.Tracks)).Select(_ => AddAudioStream(writer, audio)).ToArray()
                    : Array.Empty<int>();
                writer.BeginWriting();

                _writer = writer;
                _videoStream = videoStream;
                _audioStreams = audioStreams;
                Fragmented = frag;
                Video = extras ? video : video with { KeyframeSeconds = 0 };
                if (last != null) Log.Warn($"인코더 설정 일부 미지원 → 대체 설정 사용 (고급 매개변수={extras}, 조각 저장={frag})");
                Log.Info($"MP4 시작: {path} {video.Codec} {video.Width}x{video.Height}@{video.Fps} {video.BitrateBps / 1000}kbps GOP={(extras ? video.KeyframeSeconds : 0)}s audio={(audio != null ? $"{audio.Tracks}트랙" : "없음")}");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"인코더 구성 실패 (고급 매개변수={extras}, 조각 저장={frag}): {ex.Message}");
                writer?.Dispose();
                try { File.Delete(path); } catch { }
            }
        }
        _deviceManager.Dispose();
        throw new InvalidOperationException($"{video.Codec} 인코더를 시작하지 못했습니다: {last?.Message}", last);
    }

    private static int AddAudioStream(IMFSinkWriter writer, AudioEncodeSettings audio)
    {
        int stream;
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels).CheckError();
            outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)audio.AacBytesPerSecond).CheckError();
            stream = writer.AddStream(outType);
        }

        using var inType = MediaFactory.MFCreateMediaType();
        int blockAlign = audio.Channels * 2;
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate).CheckError();
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels).CheckError();
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)blockAlign).CheckError();
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(blockAlign * audio.SampleRate)).CheckError();
        writer.SetInputMediaType(stream, inType, null!);
        return stream;
    }

    /// <summary>Encodes one frame. The texture must not be reused afterwards (the encoder reads it asynchronously).</summary>
    public void WriteVideo(ID3D11Texture2D texture, long time100ns, long duration100ns)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        using (var buffer2d = buffer.QueryInterface<IMF2DBuffer>())
        {
            buffer.CurrentLength = buffer2d.ContiguousLength;
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time100ns;
        sample.SampleDuration = duration100ns;

        lock (_writeLock)
        {
            if (_finished) return;
            _writer.WriteSample(_videoStream, sample);
            _videoSubmitted++;
        }
    }

    private long _videoSubmitted;
    private bool _statsBroken;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStatisticsFn(IntPtr self, int streamIndex, ref SinkWriterStatistics stats);
    private static GetStatisticsFn? _getStatistics;

    /// <summary>
    /// IMFSinkWriter::GetStatistics needs <c>cb</c> filled in by the caller; the generated wrapper passes 0 and gets
    /// E_INVALIDARG, so call the vtable slot (13) directly.
    /// </summary>
    private static SinkWriterStatistics ReadStatistics(IMFSinkWriter writer, int stream)
    {
        if (_getStatistics == null)
        {
            var vtbl = Marshal.ReadIntPtr(writer.NativePointer);
            _getStatistics = Marshal.GetDelegateForFunctionPointer<GetStatisticsFn>(Marshal.ReadIntPtr(vtbl, 13 * IntPtr.Size));
        }
        var st = new SinkWriterStatistics { Cb = Marshal.SizeOf<SinkWriterStatistics>() };
        int hr = _getStatistics(writer.NativePointer, stream, ref st);
        Marshal.ThrowExceptionForHR(hr);
        return st;
    }

    public long VideoSubmitted { get { lock (_writeLock) return _videoSubmitted; } }

    public long VideoEncoded
    {
        get
        {
            if (_statsBroken) return -1;
            try
            {
                lock (_writeLock)
                {
                    if (_finished) return -1;
                    var st = ReadStatistics(_writer, _videoStream);
                    // Hardware encoders often leave "encoded" at 0; "processed" (written by the sink) also proves the encoder is done.
                    return (long)Math.Max(st.NumSamplesEncoded, st.NumSamplesProcessed);
                }
            }
            catch (Exception ex)
            {
                _statsBroken = true; // statistics unsupported: the texture pool falls back to plain allocation
                Log.Warn($"인코더 통계를 읽을 수 없음: {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>Encodes interleaved 16-bit PCM.</summary>
    public void WriteAudio(ReadOnlySpan<byte> pcm, long time100ns, int track = 0)
    {
        if (track < 0 || track >= _audioStreams.Length || pcm.Length == 0 || _audio == null) return;

        int frames = pcm.Length / (_audio.Channels * 2);
        long duration = frames * 10_000_000L / _audio.SampleRate;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            unsafe
            {
                pcm.CopyTo(new Span<byte>((void*)ptr, pcm.Length));
            }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = pcm.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time100ns;
        sample.SampleDuration = duration;

        lock (_writeLock)
        {
            if (_finished) return;
            _writer.WriteSample(_audioStreams[track], sample);
        }
    }

    /// <summary>Closes without finalizing (used for a segment that received no frames; finalizing an empty file fails).</summary>
    public void Abort()
    {
        lock (_writeLock) _finished = true;
        Dispose();
        try { File.Delete(Path); } catch { }
    }

    public void Finish()
    {
        lock (_writeLock)
        {
            if (_finished) return;
            _finished = true;
            try
            {
                _writer.Finalize();
                Log.Info($"MP4 저장 완료: {Path}");
            }
            catch (Exception ex)
            {
                Log.Error("Finalize 실패 (조각 저장 방식이라 파일은 재생 가능할 수 있음)", ex);
                throw;
            }
        }
    }

    public void Dispose()
    {
        try { Finish(); } catch { /* already logged */ }
        _writer.Dispose();
        _deviceManager.Dispose();
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(true).CheckError();
    }
}
