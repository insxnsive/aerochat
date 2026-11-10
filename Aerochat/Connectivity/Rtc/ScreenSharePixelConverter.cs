using SIPSorceryMedia.Abstractions;

namespace Aerochat.Connectivity.Rtc;

internal static class ScreenSharePixelConverter
{
    internal static bool TryConvertToBgra(
        byte[] source,
        int width,
        int height,
        int sourceStride,
        VideoPixelFormatsEnum format,
        out ScreenShareFrame? frame)
    {
        frame = null;
        ArgumentNullException.ThrowIfNull(source);
        int sourceBytesPerPixel = format switch
        {
            VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb => 3,
            VideoPixelFormatsEnum.Bgra or VideoPixelFormatsEnum.Rgba => 4,
            _ => 0
        };
        if (sourceBytesPerPixel == 0
            || width is <= 0 or > ScreenShareFrame.MaxWidth
            || height is <= 0 or > ScreenShareFrame.MaxHeight)
            return false;

        try
        {
            int minimumSourceStride = checked(width * sourceBytesPerPixel);
            int requiredSourceLength = checked(sourceStride * height);
            if (sourceStride < minimumSourceStride || source.Length < requiredSourceLength)
                return false;

            int destinationStride = checked(width * ScreenShareFrame.BytesPerPixel);
            byte[] destination = new byte[checked(destinationStride * height)];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * sourceStride;
                int destinationRow = y * destinationStride;
                for (int x = 0; x < width; x++)
                {
                    int sourceOffset = sourceRow + x * sourceBytesPerPixel;
                    int destinationOffset = destinationRow + x * ScreenShareFrame.BytesPerPixel;
                    switch (format)
                    {
                        case VideoPixelFormatsEnum.Bgr:
                            destination[destinationOffset] = source[sourceOffset];
                            destination[destinationOffset + 1] = source[sourceOffset + 1];
                            destination[destinationOffset + 2] = source[sourceOffset + 2];
                            destination[destinationOffset + 3] = 0xFF;
                            break;
                        case VideoPixelFormatsEnum.Rgb:
                            destination[destinationOffset] = source[sourceOffset + 2];
                            destination[destinationOffset + 1] = source[sourceOffset + 1];
                            destination[destinationOffset + 2] = source[sourceOffset];
                            destination[destinationOffset + 3] = 0xFF;
                            break;
                        case VideoPixelFormatsEnum.Bgra:
                            Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, 4);
                            break;
                        case VideoPixelFormatsEnum.Rgba:
                            destination[destinationOffset] = source[sourceOffset + 2];
                            destination[destinationOffset + 1] = source[sourceOffset + 1];
                            destination[destinationOffset + 2] = source[sourceOffset];
                            destination[destinationOffset + 3] = source[sourceOffset + 3];
                            break;
                    }
                }
            }

            frame = new ScreenShareFrame(width, height, destinationStride, destination);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
