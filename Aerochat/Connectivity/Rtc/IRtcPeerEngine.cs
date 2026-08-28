namespace Aerochat.Connectivity.Rtc;

public interface IRtcPeerEngine : IAsyncDisposable
{
    event EventHandler<RtcPeerState>? StateChanged;
    event EventHandler<RtcIceCandidate>? IceCandidateReady;

    RtcPeerState State { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string> StartCall(CancellationToken cancellationToken = default);
    Task<string> AcceptOffer(string offerSdp, CancellationToken cancellationToken = default);
    Task ApplyAnswer(string answerSdp, CancellationToken cancellationToken = default);
    void AddIceCandidate(RtcIceCandidate candidate);
    void Mute();
    void Unmute();
    Task Hangup(string reason = "local hangup");
}
