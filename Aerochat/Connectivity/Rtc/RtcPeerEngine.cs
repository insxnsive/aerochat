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
public sealed class RtcPeerEngine : IRtcPeerEngine, IScreenShareFrameSource, IScreenShareFrameSink
{
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly Func<IRtcAudioEndpoint?> _audioEndpointFactory;
    private readonly Func<IRtcVideoEndpoint?> _videoEndpointFactory;
    private Task? _disposeTask;
    private RTCPeerConnection? _peer;
    private IRtcAudioEndpoint? _audio;
    private IRtcVideoEndpoint? _video;
    private Action<uint, byte[]>? _encodedSampleHandler;
    private Action<List<AudioFormat>>? _formatsHandler;
    private Action<IPEndPoint, SDPMediaTypesEnum, RTPPacket>? _rtpHandler;
    private Action<uint, byte[]>? _videoEncodedSampleHandler;
    private Action<List<VideoFormat>>? _videoFormatsHandler;
    private Action<IPEndPoint, uint, byte[], VideoFormat>? _videoFrameHandler;
    private EventHandler<ScreenShareFrame>? _decodedFrameHandler;
    private bool _initialized;
    private bool _audioStarted;
    private bool _videoStarted;
    private bool _muted;
    private bool _disposed;

    public RtcPeerEngine(bool useAudioDevices = true)
        : this(
            () => useAudioDevices ? new WindowsRtcAudioEndpoint() : null,
            () => new Vp8RtcVideoEndpoint())
    {
    }

    internal RtcPeerEngine(Func<IRtcAudioEndpoint?> audioEndpointFactory)
        : this(audioEndpointFactory, () => null)
    {
    }

    internal RtcPeerEngine(
        Func<IRtcAudioEndpoint?> audioEndpointFactory,
        Func<IRtcVideoEndpoint?> videoEndpointFactory)
    {
        _audioEndpointFactory = audioEndpointFactory
            ?? throw new ArgumentNullException(nameof(audioEndpointFactory));
        _videoEndpointFactory = videoEndpointFactory
            ?? throw new ArgumentNullException(nameof(videoEndpointFactory));
    }

    public event EventHandler<RtcPeerState>? StateChanged;
    public event EventHandler<RtcIceCandidate>? IceCandidateReady;
    public event EventHandler<ScreenShareFrame>? FrameReceived;
    public RtcPeerState State { get; private set; } = RtcPeerState.New;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return Task.CompletedTask;

            RTCPeerConnection peer = new RTCPeerConnection(null);
            _peer = peer;
            peer.onicecandidate += candidate => OnPeerIceCandidate(peer, candidate);
            peer.onconnectionstatechange += state => OnPeerConnectionStateChanged(peer, state);
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

            _video = _videoEndpointFactory();
            if (_video is not null)
            {
                IRtcVideoEndpoint video = _video;
                _videoEncodedSampleHandler = _peer.SendVideo;
                _videoFormatsHandler = formats => video.SetFormat(formats.First());
                _videoFrameHandler = video.Receive;
                _decodedFrameHandler = (_, frame) => FrameReceived?.Invoke(this, frame);
                video.EncodedSample += _videoEncodedSampleHandler;
                video.FrameDecoded += _decodedFrameHandler;
                _peer.OnVideoFormatsNegotiated += _videoFormatsHandler;
                _peer.OnVideoFrameReceived += _videoFrameHandler;
                _peer.addTrack(new MediaStreamTrack(
                    video.SourceFormats.ToList(), MediaStreamStatusEnum.SendRecv));
            }

            _initialized = true;
            _audioStarted = false;
            _videoStarted = false;
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
        await StartVideoAsync().ConfigureAwait(false);
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
        await StartVideoAsync().ConfigureAwait(false);
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

    public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        IRtcVideoEndpoint video;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            video = _video ?? throw new InvalidOperationException("The RTC video endpoint is unavailable.");
            if (!_videoStarted)
                throw new InvalidOperationException("The RTC video endpoint has not started.");
        }

        video.Send(frame);
        return Task.CompletedTask;
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
        IRtcVideoEndpoint? video;
        Action<uint, byte[]>? encodedSampleHandler;
        Action<List<AudioFormat>>? formatsHandler;
        Action<IPEndPoint, SDPMediaTypesEnum, RTPPacket>? rtpHandler;
        Action<uint, byte[]>? videoEncodedSampleHandler;
        Action<List<VideoFormat>>? videoFormatsHandler;
        Action<IPEndPoint, uint, byte[], VideoFormat>? videoFrameHandler;
        EventHandler<ScreenShareFrame>? decodedFrameHandler;
        lock (_gate)
        {
            peer = _peer;
            audio = _audio;
            video = _video;
            encodedSampleHandler = _encodedSampleHandler;
            formatsHandler = _formatsHandler;
            rtpHandler = _rtpHandler;
            videoEncodedSampleHandler = _videoEncodedSampleHandler;
            videoFormatsHandler = _videoFormatsHandler;
            videoFrameHandler = _videoFrameHandler;
            decodedFrameHandler = _decodedFrameHandler;
            _peer = null;
            _audio = null;
            _video = null;
            _encodedSampleHandler = null;
            _formatsHandler = null;
            _rtpHandler = null;
            _videoEncodedSampleHandler = null;
            _videoFormatsHandler = null;
            _videoFrameHandler = null;
            _decodedFrameHandler = null;
            _initialized = false;
            _audioStarted = false;
            _videoStarted = false;
            _muted = false;
        }

        if (peer is null && audio is null && video is null)
            return;

        if (audio is not null && encodedSampleHandler is not null)
            audio.EncodedSample -= encodedSampleHandler;
        if (peer is not null && formatsHandler is not null)
            peer.OnAudioFormatsNegotiated -= formatsHandler;
        if (peer is not null && rtpHandler is not null)
            peer.OnRtpPacketReceived -= rtpHandler;
        if (video is not null && videoEncodedSampleHandler is not null)
            video.EncodedSample -= videoEncodedSampleHandler;
        if (video is not null && decodedFrameHandler is not null)
            video.FrameDecoded -= decodedFrameHandler;
        if (peer is not null && videoFormatsHandler is not null)
            peer.OnVideoFormatsNegotiated -= videoFormatsHandler;
        if (peer is not null && videoFrameHandler is not null)
            peer.OnVideoFrameReceived -= videoFrameHandler;
        peer?.Close(reason);
        Exception? cleanupFailure = null;
        try
        {
            if (audio is not null)
                await audio.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            if (video is not null)
                await video.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }
        finally
        {
            video?.Dispose();
        }
        SetState(RtcPeerState.Closed);
        if (cleanupFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
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

    private async Task StartVideoAsync()
    {
        IRtcVideoEndpoint? video;
        lock (_gate)
        {
            if (_videoStarted || _video is null)
                return;
            _videoStarted = true;
            video = _video;
        }

        try
        {
            await video.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
                _videoStarted = false;
            throw;
        }
    }

    // A candidate from a peer that is no longer the engine's current peer (for example one that a
    // hangup closed) must not reach the next call.
    // Peer callbacks use an opaque sender stamp. No SIPSorcery type leaves this class.
    private sealed record PeerEventSource(RtcPeerEngine Owner, RTCPeerConnection Peer);

    internal bool IsPeerEvent(object? sender) => sender is PeerEventSource;

    internal bool IsCurrentPeerEvent(object? sender)
    {
        lock (_gate)
            return sender is PeerEventSource source
                && ReferenceEquals(source.Owner, this) && ReferenceEquals(source.Peer, _peer);
    }

    internal Action? BeforePeerEventDelivery { get; set; }

    internal void OnPeerIceCandidate(RTCPeerConnection source, RTCIceCandidate candidate)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(source, _peer))
                return;
        }

        BeforePeerEventDelivery?.Invoke();

        if (!string.IsNullOrWhiteSpace(candidate.candidate))
            IceCandidateReady?.Invoke(new PeerEventSource(this, source), new RtcIceCandidate(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
    }

    internal RTCPeerConnection? CurrentPeer
    {
        get
        {
            lock (_gate)
            {
                return _peer;
            }
        }
    }

    // A late event from a peer that is no longer the engine's current peer (for example one that a
    // hangup closed) must not change the state of the next call. The hangup itself raises Closed.
    internal void OnPeerConnectionStateChanged(RTCPeerConnection source, RTCPeerConnectionState state)
    {
        RtcPeerState mapped = state switch
        {
            RTCPeerConnectionState.connected => RtcPeerState.Connected,
            RTCPeerConnectionState.disconnected => RtcPeerState.Disconnected,
            RTCPeerConnectionState.failed => RtcPeerState.Failed,
            RTCPeerConnectionState.closed => RtcPeerState.Closed,
            _ => RtcPeerState.Connecting
        };
        lock (_gate)
        {
            if (!ReferenceEquals(source, _peer) || State == mapped)
                return;
            State = mapped;
        }

        BeforePeerEventDelivery?.Invoke();
        StateChanged?.Invoke(new PeerEventSource(this, source), mapped);
    }

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
