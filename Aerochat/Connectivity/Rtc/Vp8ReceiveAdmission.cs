namespace Aerochat.Connectivity.Rtc;

/// <summary>
/// Decides, before any native decode, whether a received VP8 frame may reach the decoder.
/// The peer controls these bytes, so size, keyframe dimensions and decode rate are all bounded here.
/// </summary>
internal sealed class Vp8ReceiveAdmission
{
    internal const int MaxEncodedFrameBytes = 1024 * 1024;
    internal const int MaxFramesPerSecond = 30;
    private const long WindowMilliseconds = 1000;

    private bool _hasKeyframe;
    private bool _windowStarted;
    private long _windowStart;
    private int _windowCount;

    internal bool TryAdmit(ReadOnlySpan<byte> frame, long nowMilliseconds)
    {
        if (frame.Length < 3 || frame.Length > MaxEncodedFrameBytes)
            return false;

        // VP8 frame tag: bit 0 of the first byte is 0 for a keyframe.
        bool keyframe = (frame[0] & 0x01) == 0;
        if (keyframe)
        {
            if (!HasAllowedKeyframeSize(frame))
            {
                _hasKeyframe = false;
                return false;
            }
        }
        else if (!_hasKeyframe)
        {
            return false;
        }

        if (!_windowStarted
            || nowMilliseconds < _windowStart
            || nowMilliseconds - _windowStart >= WindowMilliseconds)
        {
            _windowStarted = true;
            _windowStart = nowMilliseconds;
            _windowCount = 0;
        }

        if (_windowCount >= MaxFramesPerSecond)
        {
            // Later interframes depend on this dropped frame, so wait for the next keyframe.
            _hasKeyframe = false;
            return false;
        }

        _windowCount++;
        if (keyframe)
            _hasKeyframe = true;
        return true;
    }

    private static bool HasAllowedKeyframeSize(ReadOnlySpan<byte> frame)
    {
        // Keyframe header: 3-byte tag, start code 9D 01 2A, then 14-bit width and height (top 2 bits are scaling).
        if (frame.Length < 10 || frame[3] != 0x9D || frame[4] != 0x01 || frame[5] != 0x2A)
            return false;

        int width = (frame[6] | (frame[7] << 8)) & 0x3FFF;
        int height = (frame[8] | (frame[9] << 8)) & 0x3FFF;
        return width is > 0 and <= ScreenShareFrame.MaxWidth
            && height is > 0 and <= ScreenShareFrame.MaxHeight;
    }
}
