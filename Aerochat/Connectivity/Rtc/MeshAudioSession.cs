using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace Aerochat.Connectivity.Rtc;

internal interface IMeshCapture
{
    event Action<uint,byte[]>? Sample;
    Task StartAsync();
    Task CloseAsync();
}
internal interface IMeshPlayback
{
    Task StartAsync();
    Task CloseAsync();
    void Receive(RtcAudioPacket packet);
}

// One owner per explicit mesh media session. The coordinator disposes this owner
// on leave; individual peer removal never closes another peer's capture or sink.
internal sealed class MeshAudioSession : IAsyncDisposable
{
    internal static readonly AudioFormat Format=new(SDPWellKnownMediaFormatsEnum.PCMU);
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly Func<IMeshCapture> _captureFactory;
    private readonly Func<IMeshPlayback> _playbackFactory;
    private readonly Dictionary<RtcPeerEngine,Endpoint> _peers=[];
    private IMeshCapture? _capture;
    private bool _captureStarted;
    private bool _closed;
    private int _muted;
    private Task? _dispose;
    internal MeshAudioSession():this(()=>new WindowsMeshCapture(),()=>new WindowsMeshPlayback()){}
    internal MeshAudioSession(Func<IMeshCapture> capture,Func<IMeshPlayback> playback)
    { _captureFactory=capture;_playbackFactory=playback; }
    internal async Task<RtcPeerEngine> AddPeerAsync(CancellationToken token=default,TurnCredentialProvider? turn=null)
    {
        token.ThrowIfCancellationRequested();RtcPeerEngine peer;
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            if(_peers.Count>=3)throw new InvalidOperationException("Mesh peer limit reached.");
            var endpoint=new Endpoint(this,_playbackFactory());
            peer=new RtcPeerEngine(()=>endpoint,()=>null);
            if(turn is not null)peer.UseTurn(turn);
            _peers.Add(peer,endpoint);
        }
        try { await peer.InitializeAsync(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();lock(_gate)ObjectDisposedException.ThrowIf(_closed,this);return peer; }
        catch { await RemovePeerAsync(peer).ConfigureAwait(false);throw; }
    }
    internal async Task RemovePeerAsync(RtcPeerEngine peer)
    {
        Endpoint? endpoint;lock(_gate){if(!_peers.TryGetValue(peer,out endpoint))return;}
        try { await peer.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await endpoint.StopAsync().ConfigureAwait(false); }
            finally { lock(_gate)_peers.Remove(peer); }
        }
    }
    private async Task StartAsync(Endpoint endpoint)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            lock(_gate)ObjectDisposedException.ThrowIf(_closed,this);
            if(endpoint.Stopped)throw new ObjectDisposedException(nameof(Endpoint));
            if(endpoint.Running)return;
            if(_capture is null){_capture=_captureFactory();_capture.Sample+=OnSample;}
            if(!_captureStarted){await _capture.StartAsync().ConfigureAwait(false);_captureStarted=true;}
            await endpoint.Playback.StartAsync().ConfigureAwait(false);endpoint.Running=true;
        }
        finally { _serial.Release(); }
    }
    private async Task StopAsync(Endpoint endpoint)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(endpoint.Stopped)return;endpoint.Stopped=true;endpoint.Running=false;
            await endpoint.Playback.CloseAsync().ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private void OnSample(uint duration,byte[] bytes)
    {
        Endpoint[] targets;lock(_gate){if(_closed||Volatile.Read(ref _muted)!=0)return;targets=_peers.Values.ToArray();}
        foreach(var target in targets)
            if(target.Running&&!target.Stopped)
                foreach(Action<uint,byte[]> handler in target.Handlers?.GetInvocationList()??[])
                    try { handler(duration,bytes); } catch { /* A failed peer cannot suppress other peers. */ }
    }
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            if(_dispose is not null)return new(_dispose);
            _closed=true;
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_dispose=completion.Task;
            _=FinishDisposeAsync(completion);return new(_dispose);
        }
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        // Do not execute peer teardown synchronously while holding the owner gate.
        await Task.Yield();Exception? failure=null;RtcPeerEngine[] peers;lock(_gate)peers=_peers.Keys.ToArray();
        foreach(var peer in peers)try { await RemovePeerAsync(peer).ConfigureAwait(false); }catch(Exception error){failure??=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_capture is not null)
            {
                _capture.Sample-=OnSample;
                try { await _capture.CloseAsync().ConfigureAwait(false); }catch(Exception error){failure??=error;}
                _capture=null;
            }
        }
        finally { _serial.Release(); }
        if(failure is null)completion.TrySetResult();else completion.TrySetException(failure);
    }
    private sealed class Endpoint(MeshAudioSession owner,IMeshPlayback playback):IRtcAudioEndpoint
    {
        internal IMeshPlayback Playback=>playback;internal volatile bool Running;internal volatile bool Stopped;
        internal Action<uint,byte[]>? Handlers;
        public IReadOnlyList<AudioFormat> SourceFormats=>new[]{Format};
        public event Action<uint,byte[]>? EncodedSample {add=>Handlers+=value;remove=>Handlers-=value;}
        public void SetFormat(AudioFormat format)
        {
            if(!string.Equals(format.FormatName,Format.FormatName,StringComparison.OrdinalIgnoreCase)||format.ClockRate!=Format.ClockRate||format.ChannelCount!=Format.ChannelCount)
                throw new InvalidOperationException("Mesh audio requires PCMU.");
        }
        public void Receive(RtcAudioPacket packet){if(Running&&!Stopped)playback.Receive(packet);}
        public Task StartAsync()=>owner.StartAsync(this);
        public Task StopAsync()=>owner.StopAsync(this);
        public void Mute()=>Interlocked.Exchange(ref owner._muted,1);
        public void Unmute()=>Interlocked.Exchange(ref owner._muted,0);
    }
}

internal sealed class WindowsMeshCapture:IMeshCapture
{
    private readonly WindowsAudioEndPoint _endpoint=new(new AudioEncoder());
    internal WindowsMeshCapture(){_endpoint.SetAudioSourceFormat(MeshAudioSession.Format);_endpoint.OnAudioSourceEncodedSample+=Forward;}
    public event Action<uint,byte[]>? Sample;
    private void Forward(uint duration,byte[] bytes)=>Sample?.Invoke(duration,bytes);
    public Task StartAsync()=>_endpoint.StartAudio();
    public async Task CloseAsync()
    {
        _endpoint.OnAudioSourceEncodedSample-=Forward;
        try { await _endpoint.CloseAudio().ConfigureAwait(false); }
        finally { await _endpoint.CloseAudioSink().ConfigureAwait(false); }
    }
}
internal sealed class WindowsMeshPlayback:IMeshPlayback
{
    private readonly WindowsAudioEndPoint _endpoint=new(new AudioEncoder());
    private uint? _timestamp;
    internal WindowsMeshPlayback()=>_endpoint.SetAudioSinkFormat(MeshAudioSession.Format);
    public Task StartAsync()=>_endpoint.StartAudioSink();
    public async Task CloseAsync()
    {
        try { await _endpoint.CloseAudioSink().ConfigureAwait(false); }
        finally { await _endpoint.CloseAudio().ConfigureAwait(false); }
    }
    public void Receive(RtcAudioPacket packet)
    {
        uint duration=_timestamp is uint previous?Math.Max(1,(uint)((ulong)unchecked(packet.Timestamp-previous)*1000/(uint)MeshAudioSession.Format.RtpClockRate)):20;
        _timestamp=packet.Timestamp;
        _endpoint.GotEncodedMediaFrame(new EncodedAudioFrame(0,MeshAudioSession.Format,duration,packet.Payload));
    }
}
