using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

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
public sealed class RtcPeerEngine : IRtcPeerEngine
{
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly Func<IRtcAudioEndpoint?> _audioEndpointFactory;
    private Task? _disposeTask;
    private RTCPeerConnection? _peer;
    private IRtcAudioEndpoint? _audio;
    private Action<uint, byte[]>? _encodedSampleHandler;
    private Action<List<AudioFormat>>? _formatsHandler;
    private Action<IPEndPoint, SDPMediaTypesEnum, RTPPacket>? _rtpHandler;
    private bool _initialized;
    private bool _audioStarted;
    private bool _muted;
    private bool _disposed;

    public RtcPeerEngine(bool useAudioDevices = true)
        : this(() => useAudioDevices ? new WindowsRtcAudioEndpoint() : null)
    {
    }

    internal RtcPeerEngine(Func<IRtcAudioEndpoint?> audioEndpointFactory)
    {
        _audioEndpointFactory = audioEndpointFactory
            ?? throw new ArgumentNullException(nameof(audioEndpointFactory));
    }

    public event EventHandler<RtcPeerState>? StateChanged;
    public event EventHandler<RtcIceCandidate>? IceCandidateReady;
    public RtcPeerState State { get; private set; } = RtcPeerState.New;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return Task.CompletedTask;

            _peer = new RTCPeerConnection(null);
            _peer.onicecandidate += OnIceCandidate;
            _peer.onconnectionstatechange += OnConnectionStateChanged;
            _audio = _audioEndpointFactory();

            if (_audio is not null)
            {
                IRtcAudioEndpoint audio = _audio;
                _encodedSampleHandler = _peer.SendAudio;
                _formatsHandler = formats => audio.SetFormat(formats.First());
                _rtpHandler = (remoteEndPoint, mediaType, packet) =>
                {
                    if (mediaType == SDPMediaTypesEnum.audio)
                    {
                        audio.Receive(new RtcAudioPacket(
                            remoteEndPoint,
                            packet.Header.SyncSource,
                            packet.Header.SequenceNumber,
                            packet.Header.Timestamp,
                            packet.Header.PayloadType,
                            packet.Header.MarkerBit == 1,
                            packet.Payload));
                    }
                };
                audio.EncodedSample += _encodedSampleHandler;
                _peer.OnAudioFormatsNegotiated += _formatsHandler;
                _peer.OnRtpPacketReceived += _rtpHandler;
                _peer.addTrack(new MediaStreamTrack(
                    audio.SourceFormats.ToList(), MediaStreamStatusEnum.SendRecv));
            }
            else
            {
                _peer.addTrack(new MediaStreamTrack(
                    new[] { SDPWellKnownMediaFormatsEnum.PCMU }));
            }

            _initialized = true;
            _audioStarted = false;
            _muted = false;
            if (State == RtcPeerState.Closed)
                State = RtcPeerState.New;
        }
        return Task.CompletedTask;
    }

    public async Task<string> StartCall(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetState(RtcPeerState.Connecting);
        RTCSessionDescriptionInit offer = _peer!.createOffer(null);
        await _peer.setLocalDescription(offer).ConfigureAwait(false);
        return offer.sdp;
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
        await StartAudioAsync().ConfigureAwait(false);
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
        await StartAudioAsync().ConfigureAwait(false);
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
        IRtcAudioEndpoint? audio;
        lock (_gate)
        {
            if (_muted)
                return;
            _muted = true;
            audio = _audio;
        }
        audio?.Mute();
    }

    public void Unmute()
    {
        IRtcAudioEndpoint? audio;
        lock (_gate)
        {
            if (!_muted)
                return;
            _muted = false;
            audio = _audio;
        }
        audio?.Unmute();
    }

    public async Task Hangup(string reason = "local hangup")
    {
        RTCPeerConnection? peer;
        IRtcAudioEndpoint? audio;
        Action<uint, byte[]>? encodedSampleHandler;
        Action<List<AudioFormat>>? formatsHandler;
        Action<IPEndPoint, SDPMediaTypesEnum, RTPPacket>? rtpHandler;
        lock (_gate)
        {
            peer = _peer;
            audio = _audio;
            encodedSampleHandler = _encodedSampleHandler;
            formatsHandler = _formatsHandler;
            rtpHandler = _rtpHandler;
            _peer = null;
            _audio = null;
            _encodedSampleHandler = null;
            _formatsHandler = null;
            _rtpHandler = null;
            _initialized = false;
            _audioStarted = false;
            _muted = false;
        }

        if (peer is null && audio is null)
            return;

        if (audio is not null && encodedSampleHandler is not null)
            audio.EncodedSample -= encodedSampleHandler;
        if (peer is not null && formatsHandler is not null)
            peer.OnAudioFormatsNegotiated -= formatsHandler;
        if (peer is not null && rtpHandler is not null)
            peer.OnRtpPacketReceived -= rtpHandler;
        peer?.Close(reason);
        if (audio is not null)
            await audio.StopAsync().ConfigureAwait(false);
        SetState(RtcPeerState.Closed);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            lock (_gate)
                _disposed = true;
            _ = CompleteDisposeAsync(completion);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await Hangup("disposed").ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task StartAudioAsync()
    {
        IRtcAudioEndpoint? audio;
        lock (_gate)
        {
            if (_audioStarted || _audio is null)
                return;
            _audioStarted = true;
            audio = _audio;
        }

        try
        {
            await audio.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                _audioStarted = false;
            throw;
        }
    }

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
