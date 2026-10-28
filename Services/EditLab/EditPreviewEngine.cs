using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Services.EditLab;

/// <summary>
/// The live half of the Edit Lab: a background producer renders the current
/// <see cref="EditRenderPlan"/> through <see cref="EditTimelineRenderer"/> (the same code the offline
/// render uses) into small chunks a little ahead of the playhead, and the audio thread only copies
/// chunks out — no file opening, seeking or DSP on the audio thread. Seeking, looping and plan
/// changes bump a generation number so stale chunks are dropped instantly.
/// </summary>
public sealed class EditPreviewStream : ISampleProvider, IDisposable
{
    private const int ChunkFrames = 2048;

    private sealed record Chunk(int Generation, long StartFrame, float[] Samples, int Frames);

    private readonly ConcurrentQueue<Chunk> _queue = new();
    private readonly object _producerLock = new();
    private readonly int _aheadFrames;
    private Chunk? _current;
    private int _currentOffset;
    private int _generation;
    private long _queuedFrames;
    private long _position;
    private CancellationTokenSource? _producerCts;
    private Task? _producer;
    private volatile bool _producerDone;
    private int _endSignalled;

    public WaveFormat WaveFormat { get; }

    /// <summary>Frame the audio thread most recently played up to.</summary>
    public long Position => Interlocked.Read(ref _position);

    private long _delivered;
    /// <summary>Total frames of real (non-underrun) audio handed to the device.</summary>
    public long FramesDelivered => Interlocked.Read(ref _delivered);

    /// <summary>Raised (on a worker thread) when a non-looping preview plays past the last clip.</summary>
    public event Action? Ended;
    /// <summary>Raised (on a worker thread) when a clip can't be played.</summary>
    public event Action<string>? Error;

    public EditPreviewStream(int sampleRate, double aheadSeconds = 0.5)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _aheadFrames = (int)(sampleRate * aheadSeconds);
    }

    /// <summary>(Re)starts production of <paramref name="plan"/> at <paramref name="frame"/>.
    /// <paramref name="loop"/> wraps the playhead from its end back to its start.</summary>
    public void Start(EditRenderPlan plan, long frame, (long Start, long End)? loop)
    {
        if (plan.SampleRate != WaveFormat.SampleRate)
            throw new ArgumentException("The plan's sample rate doesn't match the preview stream.", nameof(plan));

        lock (_producerLock)
        {
            StopProducer();
            int generation = Interlocked.Increment(ref _generation);
            if (loop is { } l && (l.End <= l.Start)) loop = null;
            if (loop is { } lp && (frame < lp.Start || frame >= lp.End)) frame = lp.Start;
            Interlocked.Exchange(ref _position, frame);
            DrainQueue();
            _producerDone = false;
            Interlocked.Exchange(ref _endSignalled, 0);

            var cts = new CancellationTokenSource();
            _producerCts = cts;
            _producer = Task.Factory.StartNew(() => Produce(plan, frame, loop, generation, cts.Token),
                cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    /// <summary>Stops producing; the audio thread outputs silence from now on.</summary>
    public void Halt()
    {
        lock (_producerLock)
        {
            StopProducer();
            Interlocked.Increment(ref _generation);
            DrainQueue();
        }
    }

    /// <summary>Drops queued chunks (the audio thread may be dequeuing concurrently; each side
    /// subtracts only what it took, so the ahead-counter stays exact).</summary>
    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var stale)) Interlocked.Add(ref _queuedFrames, -stale.Frames);
    }

    private void StopProducer()
    {
        _producerCts?.Cancel();
        try
        {
            _producer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            Serilog.Log.Debug(ex, "[EditLab] preview producer ended with an error");
        }
        _producerCts?.Dispose();
        _producerCts = null;
        _producer = null;
    }

    private void Produce(EditRenderPlan plan, long frame, (long Start, long End)? loop, int generation, CancellationToken ct)
    {
        EditTimelineRenderer? renderer = null;
        int reportedErrors = 0;
        try
        {
            renderer = new EditTimelineRenderer(plan, frame);
            while (!ct.IsCancellationRequested)
            {
                if (Interlocked.Read(ref _queuedFrames) >= _aheadFrames)
                {
                    Thread.Sleep(4);
                    continue;
                }

                long pos = renderer.Position;
                long limit = loop?.End ?? plan.TotalFrames;
                if (pos >= limit)
                {
                    if (loop is { } l)
                    {
                        renderer.Dispose();
                        renderer = new EditTimelineRenderer(plan, l.Start);
                        continue;
                    }
                    _producerDone = true;
                    return;
                }

                int frames = (int)Math.Min(ChunkFrames, limit - pos);
                var samples = new float[frames * 2];
                renderer.Read(samples, 0, samples.Length);
                if (generation != Volatile.Read(ref _generation)) return;
                _queue.Enqueue(new Chunk(generation, pos, samples, frames));
                Interlocked.Add(ref _queuedFrames, frames);

                lock (renderer.Errors)
                {
                    while (reportedErrors < renderer.Errors.Count) Error?.Invoke(renderer.Errors[reportedErrors++]);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[EditLab] preview render failed");
            Error?.Invoke($"Preview stopped: {ex.Message}");
            _producerDone = true;
        }
        finally
        {
            renderer?.Dispose();
        }
    }

    /// <summary>Audio thread: copies queued chunks out; silence on underrun or after the end.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int wanted = count / 2;
        int written = 0;
        int generation = Volatile.Read(ref _generation);

        while (written < wanted)
        {
            if (_current == null || _currentOffset >= _current.Frames)
            {
                if (!_queue.TryDequeue(out var next)) break;
                Interlocked.Add(ref _queuedFrames, -next.Frames);
                if (next.Generation != generation) { _current = null; continue; }
                _current = next;
                _currentOffset = 0;
            }
            else if (_current.Generation != generation)
            {
                _current = null;
                continue;
            }

            int take = Math.Min(wanted - written, _current.Frames - _currentOffset);
            Array.Copy(_current.Samples, _currentOffset * 2, buffer, offset + written * 2, take * 2);
            _currentOffset += take;
            written += take;
            Interlocked.Exchange(ref _position, _current.StartFrame + _currentOffset);
        }

        if (written > 0) Interlocked.Add(ref _delivered, written);
        if (written < wanted)
        {
            Array.Clear(buffer, offset + written * 2, (wanted - written) * 2);
            if (_producerDone && _queue.IsEmpty && (_current == null || _currentOffset >= _current.Frames)
                && Interlocked.Exchange(ref _endSignalled, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => Ended?.Invoke());
            }
        }
        return wanted * 2;
    }

    public void Dispose()
    {
        lock (_producerLock) StopProducer();
    }
}

/// <summary>
/// Plays an <see cref="EditPreviewStream"/> on its own shared-mode output (the device picked in
/// Settings, like the other preview players), so it never takes over the main player. Volume is a
/// <see cref="VolumeSampleProvider"/> — never <c>IWavePlayer.Volume</c>, which on WASAPI is the
/// Windows master volume.
/// </summary>
public sealed class EditPreviewEngine : IDisposable
{
    private readonly Func<IWavePlayer> _deviceFactory;
    private readonly object _gate = new();
    private IWavePlayer? _device;
    private EditPreviewStream? _stream;
    private VolumeSampleProvider? _volume;
    private float _level = 1f;

    public event Action? Ended;
    public event Action<string>? Error;

    public EditPreviewEngine(Func<IWavePlayer> deviceFactory) => _deviceFactory = deviceFactory;

    public bool IsPlaying { get; private set; }

    public int SampleRate => _stream?.WaveFormat.SampleRate ?? 0;

    public long PositionFrame => _stream?.Position ?? 0;

    public float Volume
    {
        get => _level;
        set
        {
            _level = Math.Clamp(value, 0f, 1.5f);
            if (_volume != null) _volume.Volume = _level;
        }
    }

    /// <summary>Starts (or restarts at <paramref name="frame"/>) playback of <paramref name="plan"/>.</summary>
    public void Play(EditRenderPlan plan, long frame, (long Start, long End)? loop)
    {
        lock (_gate)
        {
            if (_stream == null || _stream.WaveFormat.SampleRate != plan.SampleRate || _device == null)
            {
                CloseDevice();
                _stream = new EditPreviewStream(plan.SampleRate);
                _stream.Ended += OnEnded;
                _stream.Error += OnError;
                _volume = new VolumeSampleProvider(_stream) { Volume = _level };
                _device = _deviceFactory();
                _device.Init(_volume);
                _stream.Start(plan, frame, loop);
                _device.Play();
            }
            else
            {
                _stream.Start(plan, frame, loop);
                if (_device.PlaybackState != PlaybackState.Playing) _device.Play();
            }
            IsPlaying = true;
        }
    }

    /// <summary>Stops playback and releases the output device (blocking work happens off the caller's thread).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            IsPlaying = false;
            _stream?.Halt();
            var device = _device;
            var stream = _stream;
            _device = null;
            _stream = null;
            _volume = null;
            if (stream != null)
            {
                stream.Ended -= OnEnded;
                stream.Error -= OnError;
            }
            if (device != null || stream != null)
                Task.Run(() => DisposeQuietly(device, stream));
        }
    }

    private void CloseDevice()
    {
        var device = _device;
        var stream = _stream;
        _device = null;
        _stream = null;
        if (stream != null)
        {
            stream.Ended -= OnEnded;
            stream.Error -= OnError;
        }
        DisposeQuietly(device, stream);
    }

    private static void DisposeQuietly(IWavePlayer? device, EditPreviewStream? stream)
    {
        try
        {
            device?.Stop();
            device?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            Serilog.Log.Debug(ex, "[EditLab] preview device already closed");
        }
        stream?.Dispose();
    }

    private void OnEnded()
    {
        Stop();
        Ended?.Invoke();
    }

    private void OnError(string message) => Error?.Invoke(message);

    public void Dispose() => Stop();
}
