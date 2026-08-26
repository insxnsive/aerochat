using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;

namespace Aerochat.Connectivity.Rtc;

public enum RtcPeerState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

public sealed record RtcIceCandidate(string Candidate, string? SdpMid = null, int? SdpMLineIndex = null);

/// <summary>
/// Owns the SIPSorcery peer and optional Windows media endpoint. No SIPSorcery
/// types cross this connectivity boundary.
/// </summary>
public sealed class RtcPeerEngine : IAsyncDisposable
{
    private readonly bool _useAudioDevices;
    private RTCPeerConnection? _peer;
    private WindowsAudioEndPoint? _audio;
    private bool _initialized;

    public RtcPeerEngine(bool useAudioDevices = true) => _useAudioDevices = useAudioDevices;

    public event EventHandler<RtcPeerState>? StateChanged;
    public event EventHandler<RtcIceCandidate>? IceCandidateReady;
    public RtcPeerState State { get; private set; } = RtcPeerState.New;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
            return Task.CompletedTask;

        _peer = new RTCPeerConnection(null);
        _peer.onicecandidate += OnIceCandidate;
        _peer.onconnectionstatechange += OnConnectionStateChanged;

        if (_useAudioDevices)
        {
            _audio = new WindowsAudioEndPoint(new AudioEncoder());
            _audio.OnAudioSourceEncodedSample += _peer.SendAudio;
            _peer.addTrack(new MediaStreamTrack(
                _audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
            _peer.OnAudioFormatsNegotiated += formats => _audio.SetAudioSourceFormat(formats.First());
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    public async Task<string> StartCall(string offerSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetState(RtcPeerState.Connecting);
        RTCSessionDescriptionInit remoteOffer = new() { type = RTCSdpType.offer, sdp = offerSdp };
        SetDescriptionResultEnum remoteResult = _peer!.setRemoteDescription(remoteOffer);
        ThrowIfDescriptionFailed(remoteResult);
        RTCSessionDescriptionInit answer = _peer.createAnswer(null);
        await _peer.setLocalDescription(answer).ConfigureAwait(false);
        return answer.sdp;
    }

    public async Task<string> AcceptOffer(string offerSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetState(RtcPeerState.Connecting);
        SetDescriptionResultEnum remoteResult = _peer!.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        });
        ThrowIfDescriptionFailed(remoteResult);
        RTCSessionDescriptionInit answer = _peer.createAnswer(null);
        await _peer.setLocalDescription(answer).ConfigureAwait(false);
        return answer.sdp;
    }

    public async Task ApplyAnswer(string answerSdp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answerSdp);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetDescriptionResultEnum remoteResult = _peer!.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answerSdp
        });
        ThrowIfDescriptionFailed(remoteResult);
    }

    public void AddIceCandidate(RtcIceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_peer is null)
            throw new InvalidOperationException("The RTC peer has not been initialized.");
        _peer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex.HasValue ? checked((ushort)candidate.SdpMLineIndex.Value) : (ushort)0
        });
    }

    public void Mute()
    {
        if (_audio is not null && _peer is not null)
            _audio.OnAudioSourceEncodedSample -= _peer.SendAudio;
    }

    public void Unmute()
    {
        if (_audio is not null && _peer is not null)
            _audio.OnAudioSourceEncodedSample += _peer.SendAudio;
    }

    public async Task Hangup(string reason = "local hangup")
    {
        if (_peer is null)
            return;
        _peer.Close(reason);
        if (_audio is not null)
            await _audio.CloseAudio().ConfigureAwait(false);
        SetState(RtcPeerState.Closed);
    }

    public async ValueTask DisposeAsync() => await Hangup("disposed").ConfigureAwait(false);

    private void OnIceCandidate(RTCIceCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.candidate))
            IceCandidateReady?.Invoke(this, new RtcIceCandidate(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state) =>
        SetState(state switch
        {
            RTCPeerConnectionState.connected => RtcPeerState.Connected,
            RTCPeerConnectionState.disconnected => RtcPeerState.Disconnected,
            RTCPeerConnectionState.failed => RtcPeerState.Failed,
            RTCPeerConnectionState.closed => RtcPeerState.Closed,
            _ => RtcPeerState.Connecting
        });

    private static void ThrowIfDescriptionFailed(SetDescriptionResultEnum result)
    {
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"The RTC session description was rejected: {result}.");
    }

    private void SetState(RtcPeerState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
