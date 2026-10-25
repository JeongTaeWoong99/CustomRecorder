using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

public enum VideoCodec { H264, Hevc }

/// <summary>Finds which video encoders this PC has (GPU encoders are preferred automatically by the sink writer).</summary>
public static class EncoderInfo
{
    private const uint MftEnumHardware = 0x4, MftEnumSyncMft = 0x1, MftEnumAsyncMft = 0x2, MftEnumSortAndFilter = 0x40;
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3"); // MFT_FRIENDLY_NAME_Attribute

    public static Guid Subtype(VideoCodec codec) => codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264;

    /// <summary>Names of hardware encoders for the codec, e.g. "NVIDIA HEVC Encoder MFT". Empty if none.</summary>
    public static IReadOnlyList<string> HardwareEncoders(VideoCodec codec) => Enumerate(codec, MftEnumHardware | MftEnumSortAndFilter);

    /// <summary>All usable encoders (hardware and software).</summary>
    public static IReadOnlyList<string> AllEncoders(VideoCodec codec) =>
        Enumerate(codec, MftEnumHardware | MftEnumSyncMft | MftEnumAsyncMft | MftEnumSortAndFilter);

    public static bool IsAvailable(VideoCodec codec) => AllEncoders(codec).Count > 0;

    private static IReadOnlyList<string> Enumerate(VideoCodec codec, uint flags)
    {
        var names = new List<string>();
        try
        {
            MediaFactory.MFStartup(true).CheckError();
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Subtype(codec) };
            foreach (var activate in MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, output))
            {
                using (activate)
                {
                    try { var n = activate.GetAllocatedString(FriendlyName); if (n != null && !names.Contains(n)) names.Add(n); }
                    catch { names.Add("(이름 없음)"); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"인코더 목록 조회 실패 ({codec}): {ex.Message}");
        }
        return names;
    }
}
