namespace WinPadHost.Services;

public static class BgraToNv12Converter
{
    public static void Convert(
        byte[] bgra,
        int width,
        int height,
        int sourceStride,
        byte[] nv12)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(nv12);

        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 dimensions must be positive and even.");
        if (sourceStride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(sourceStride));
        if (bgra.Length < sourceStride * height)
            throw new ArgumentException("BGRA buffer is too small.", nameof(bgra));

        int yPlaneLength = width * height;
        int requiredNV12Length = yPlaneLength + yPlaneLength / 2;
        if (nv12.Length < requiredNV12Length)
            throw new ArgumentException("NV12 buffer is too small.", nameof(nv12));

        Parallel.For(0, height / 2, chromaRow =>
        {
            int y = chromaRow * 2;
            int sourceRow0 = y * sourceStride;
            int sourceRow1 = (y + 1) * sourceStride;
            int yRow0 = y * width;
            int yRow1 = (y + 1) * width;
            int uvRow = yPlaneLength + chromaRow * width;

            for (int x = 0; x < width; x += 2)
            {
                int pixel00 = sourceRow0 + x * 4;
                int pixel01 = pixel00 + 4;
                int pixel10 = sourceRow1 + x * 4;
                int pixel11 = pixel10 + 4;

                WriteLuma(bgra, pixel00, nv12, yRow0 + x);
                WriteLuma(bgra, pixel01, nv12, yRow0 + x + 1);
                WriteLuma(bgra, pixel10, nv12, yRow1 + x);
                WriteLuma(bgra, pixel11, nv12, yRow1 + x + 1);

                int blue = (bgra[pixel00] + bgra[pixel01] +
                            bgra[pixel10] + bgra[pixel11] + 2) >> 2;
                int green = (bgra[pixel00 + 1] + bgra[pixel01 + 1] +
                             bgra[pixel10 + 1] + bgra[pixel11 + 1] + 2) >> 2;
                int red = (bgra[pixel00 + 2] + bgra[pixel01 + 2] +
                           bgra[pixel10 + 2] + bgra[pixel11 + 2] + 2) >> 2;

                nv12[uvRow + x] = ClampToByte(
                    ((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                nv12[uvRow + x + 1] = ClampToByte(
                    ((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
            }
        });
    }

    private static void WriteLuma(
        byte[] bgra,
        int sourceOffset,
        byte[] nv12,
        int destinationOffset)
    {
        int blue = bgra[sourceOffset];
        int green = bgra[sourceOffset + 1];
        int red = bgra[sourceOffset + 2];
        nv12[destinationOffset] = ClampToByte(
            ((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
    }

    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
