using Vortice.MediaFoundation;
using System.Runtime.InteropServices;

namespace WinPadHost.Services;

public static class H264CapabilityProbe
{
    public static string Describe()
    {
        var startup = MediaFactory.MFStartup();
        if (startup.Failure)
            return $"H.264 probe: Media Foundation startup failed ({startup.Code}).";

        try
        {
            var outputType = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };

            uint hardware = CountEncoders(
                (uint)EnumFlag.EnumFlagHardware,
                outputType);
            uint all = CountEncoders(
                (uint)EnumFlag.EnumFlagAll,
                outputType);

            return
                $"H.264 probe: hardware encoders={hardware}, " +
                $"all encoders={all}, software fallback={Math.Max(0, (int)all - (int)hardware)}.";
        }
        catch (Exception ex)
        {
            return "H.264 probe failed: " + ex.Message;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static uint CountEncoders(
        uint flags,
        RegisterTypeInfo outputType)
    {
        IntPtr encoderClsids = IntPtr.Zero;

        try
        {
            MediaFactory.MFTEnum(
                TransformCategoryGuids.VideoEncoder,
                flags,
                null,
                outputType,
                null,
                out encoderClsids,
                out uint count);

            return count;
        }
        finally
        {
            if (encoderClsids != IntPtr.Zero)
                Marshal.FreeCoTaskMem(encoderClsids);
        }
    }
}
