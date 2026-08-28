using System.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace Aerochat.Connectivity.Rtc;

internal sealed record RtcAudioPacket(
    IPEndPoint RemoteEndPoint,
    uint SyncSource,
    uint SequenceNumber,
    uint Timestamp,
    int PayloadType,
    bool Marker,
    byte[] Payload);

internal interface IRtcAudioEndpoint
{
    IReadOnlyList<AudioFormat> SourceFormats { get; }

    event Action<uint, byte[]>? EncodedSample;

    void SetFormat(AudioFormat format);
    void Receive(RtcAudioPacket packet);
    Task StartAsync();
    Task StopAsync();
    void Mute();
    void Unmute();
}

internal sealed class WindowsRtcAudioEndpoint : IRtcAudioEndpoint
{
    private readonly WindowsAudioEndPoint _endpoint = new(new AudioEncoder());
    private int _started;
    private int _stopped;
    private int _muted;
    private AudioFormat _format;
    private uint _lastTimestamp;
    private bool _hasFormat;
    private bool _hasTimestamp;

    public WindowsRtcAudioEndpoint()
    {
        _endpoint.OnAudioSourceEncodedSample += OnEncodedSample;
    }

    public IReadOnlyList<AudioFormat> SourceFormats => _endpoint.GetAudioSourceFormats();

    public event Action<uint, byte[]>? EncodedSample;

    public void SetFormat(AudioFormat format)
    {
        _format = format;
        _hasFormat = true;
        _endpoint.SetAudioSourceFormat(format);
        _endpoint.SetAudioSinkFormat(format);
    }

    public void Receive(RtcAudioPacket packet)
    {
        if (!_hasFormat)
            return;

        uint durationMilliseconds = 20;
        if (_hasTimestamp && _format.RtpClockRate > 0)
        {
            uint timestampDelta = unchecked(packet.Timestamp - _lastTimestamp);
            durationMilliseconds = Math.Max(
                1,
                (uint)((ulong)timestampDelta * 1000UL / (uint)_format.RtpClockRate));
        }

        _lastTimestamp = packet.Timestamp;
        _hasTimestamp = true;
        _endpoint.GotEncodedMediaFrame(new EncodedAudioFrame(
            0,
            _format,
            durationMilliseconds,
            packet.Payload));
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        try
        {
            await _endpoint.StartAudio().ConfigureAwait(false);
            await _endpoint.StartAudioSink().ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        await _endpoint.CloseAudio().ConfigureAwait(false);
        await _endpoint.CloseAudioSink().ConfigureAwait(false);
    }

    public void Mute() => Interlocked.Exchange(ref _muted, 1);

    public void Unmute() => Interlocked.Exchange(ref _muted, 0);

    private void OnEncodedSample(uint durationRtpUnits, byte[] sample)
    {
        if (Volatile.Read(ref _muted) == 0)
            EncodedSample?.Invoke(durationRtpUnits, sample);
    }
}
