using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicVisualizerLoopback
{
    internal sealed class AudioEngine : IDisposable
    {
        public const int WaveformPoints = 128;

        private readonly object _sync = new object();
        private readonly object _sampleSync = new object();
        private readonly int _fftSize;

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _device;
        private readonly float[] _hannWindow;
        private float[] _fftInput = Array.Empty<float>();
        private float[] _fftWindow = Array.Empty<float>();
        private Complex[] _fftBuffer = Array.Empty<Complex>();
        private float[] _magnitudes = Array.Empty<float>();
        private float[] _snapshotBuffer = Array.Empty<float>();
        private float[] _waveformBuffer = new float[WaveformPoints];
        private int _fftWriteIndex;
        private float _accumLeftSq;
        private float _accumRightSq;
        private int _accumSamples;
        private SpectrumSnapshot _latestSnapshot;
        private volatile bool _hasNewAudio;
        private long _lastFftTicks;
        private long _lastAudioDataTicks;
        private static readonly long MinFftIntervalTicks = Stopwatch.Frequency / 60; // At most 60 FFTs/sec (16.7 ms)
        private static readonly long SilenceStaleTicks = Stopwatch.Frequency / 5;   // 200 ms without WASAPI packets -> silence

        public AudioEngine(int fftSize)
        {
            _fftSize = fftSize;
            _hannWindow = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                _hannWindow[i] = (float)FastFourierTransform.HannWindow(i, fftSize);
            }
            _latestSnapshot = new SpectrumSnapshot(Array.Empty<float>(), new float[WaveformPoints], 48000);
            CurrentDeviceId = "default";
        }

        public bool IsCapturing { get; private set; }
        public int SampleRate { get; private set; } = 48000;
        public int FftSize => _fftSize;
        public string CurrentDeviceId { get; private set; }
        public string? LastError { get; private set; }

        public bool Matches(string deviceId)
        {
            var normalizedId = string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId;
            return string.Equals(CurrentDeviceId, normalizedId, StringComparison.OrdinalIgnoreCase);
        }

        public async Task StartAsync(string deviceId, CancellationToken cancellationToken)
        {
            string? capturedError = null;

            await Task.Run(() =>
            {
                lock (_sync)
                {
                    StopInternal();

                    try
                    {
                        MMDevice device = AudioDeviceCatalog.GetDeviceOrDefault(deviceId);
                        _device = device;

                        var capture = new WasapiLoopbackCapture(device);
                        _capture = capture;

                        SampleRate = capture.WaveFormat.SampleRate;
                        CurrentDeviceId = string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId;
                        LastError = null;

                        var newFftInput = new float[_fftSize];
                        var newFftWindow = new float[_fftSize];
                        var newFftBuffer = new Complex[_fftSize];
                        var newMagnitudes = new float[_fftSize / 2];
                        var newSnapshotBuffer = new float[_fftSize / 2];
                        var newWaveformBuffer = new float[WaveformPoints];
                        var newSnapshot = new SpectrumSnapshot(newSnapshotBuffer, newWaveformBuffer, SampleRate);

                        lock (_sampleSync)
                        {
                            _fftInput = newFftInput;
                            _fftWindow = newFftWindow;
                            _fftBuffer = newFftBuffer;
                            _magnitudes = newMagnitudes;
                            _snapshotBuffer = newSnapshotBuffer;
                            _waveformBuffer = newWaveformBuffer;
                            _fftWriteIndex = 0;
                            _accumLeftSq = 0f;
                            _accumRightSq = 0f;
                            _accumSamples = 0;
                            _hasNewAudio = false;
                            Volatile.Write(ref _lastAudioDataTicks, 0);
                            _latestSnapshot = newSnapshot;
                        }

                        capture.DataAvailable += CaptureOnDataAvailable;
                        capture.RecordingStopped += CaptureOnRecordingStopped;
                        capture.StartRecording();

                        IsCapturing = true;
                    }
                    catch (Exception ex)
                    {
                        capturedError = ex.Message;
                        IsCapturing = false;
                        StopInternal();
                    }
                }
            }, cancellationToken);

            if (capturedError != null)
            {
                LastError = capturedError;
            }
        }

        private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
        {
            var capture = _capture;
            if (capture == null)
            {
                return;
            }

            int frames = e.BytesRecorded / capture.WaveFormat.BlockAlign;
            if (frames <= 0)
            {
                return;
            }

            lock (_sampleSync)
            {
                if (_fftInput.Length != _fftSize)
                {
                    return;
                }

                _fftWriteIndex = AudioMath.WriteSamplesToRing(
                    e.Buffer,
                    e.BytesRecorded,
                    capture.WaveFormat,
                    frames,
                    _fftInput,
                    _fftWriteIndex,
                    out float leftSq,
                    out float rightSq);
                _accumLeftSq += leftSq;
                _accumRightSq += rightSq;
                _accumSamples += frames;
                Volatile.Write(ref _lastAudioDataTicks, Stopwatch.GetTimestamp());
                _hasNewAudio = true;
            }
        }

        private bool BuildSpectrumFromLatestWindow(out float bassEnergy, out float rmsLeft, out float rmsRight)
        {
            bassEnergy = 0f;
            rmsLeft = 0f;
            rmsRight = 0f;

            var capture = _capture;
            int binCount = _magnitudes.Length;
            if (capture == null ||
                _fftInput.Length != _fftSize ||
                _fftWindow.Length != _fftSize ||
                _fftBuffer.Length != _fftSize ||
                binCount == 0)
            {
                return false;
            }

            if (_accumSamples > 0)
            {
                rmsLeft = MathF.Sqrt(_accumLeftSq / _accumSamples);
                rmsRight = MathF.Sqrt(_accumRightSq / _accumSamples);
                _accumLeftSq = 0f;
                _accumRightSq = 0f;
                _accumSamples = 0;
            }

            int tail = _fftSize - _fftWriteIndex;
            Array.Copy(_fftInput, _fftWriteIndex, _fftWindow, 0, tail);
            Array.Copy(_fftInput, 0, _fftWindow, tail, _fftWriteIndex);

            // Capture a phase-stabilized waveform slice (rising zero-crossing trigger) so oscilloscope flows smoothly
            int waveSpan = 256;
            int searchSpan = Math.Min(256, _fftSize - waveSpan - 1);
            int searchStart = Math.Max(1, _fftSize - waveSpan - searchSpan);
            int triggerPos = _fftSize - waveSpan;
            for (int i = searchStart; i < searchStart + searchSpan; i++)
            {
                if (_fftWindow[i - 1] <= 0f && _fftWindow[i] > 0f)
                {
                    triggerPos = i;
                    break;
                }
            }

            for (int i = 0; i < WaveformPoints; i++)
            {
                int idx = triggerPos + (i * waveSpan) / WaveformPoints;
                _waveformBuffer[i] = idx < _fftSize ? _fftWindow[idx] : 0f;
            }

            for (int i = 0; i < _fftSize; i++)
            {
                _fftBuffer[i].X = _fftWindow[i] * _hannWindow[i];
                _fftBuffer[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, (int)Math.Log2(_fftSize), _fftBuffer);

            bool hasSignal = rmsLeft > 1e-5f || rmsRight > 1e-5f;
            double nyquist = SampleRate * 0.5;
            int bassStartBin = Math.Max(1, (int)Math.Floor((20.0 / nyquist) * binCount));
            int bassEndBin = Math.Min(binCount - 1, (int)Math.Ceiling((120.0 / nyquist) * binCount));
            double bassSumSq = 0.0;
            int bassCount = 0;

            for (int i = 0; i < binCount; i++)
            {
                double re = _fftBuffer[i].X;
                double im = _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(re * re + im * im);
                if (mag < 1e-6f)
                {
                    mag = 0f;
                }
                _magnitudes[i] = mag;
                if (mag > 1e-5f)
                {
                    hasSignal = true;
                }

                if (i >= bassStartBin && i <= bassEndBin)
                {
                    bassSumSq += mag * mag;
                    bassCount++;
                }
            }

            if (bassCount > 0)
            {
                bassEnergy = (float)Math.Sqrt(bassSumSq / bassCount);
            }

            return hasSignal;
        }

        private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            IsCapturing = false;
            if (e.Exception != null)
            {
                LastError = e.Exception.Message;
            }
        }

        public SpectrumSnapshot GetSnapshot()
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (_hasNewAudio)
            {
                if ((nowTicks - _lastFftTicks) >= MinFftIntervalTicks)
                {
                    lock (_sampleSync)
                    {
                        if (_hasNewAudio && _magnitudes.Length == _snapshotBuffer.Length && _magnitudes.Length > 0)
                        {
                            _hasNewAudio = false;
                            _lastFftTicks = nowTicks;
                            bool hasSignal = BuildSpectrumFromLatestWindow(out float bassEnergy, out float rmsLeft, out float rmsRight);
                            Array.Copy(_magnitudes, _snapshotBuffer, _magnitudes.Length);
                            _latestSnapshot.HasSignal = hasSignal;
                            _latestSnapshot.BassEnergy = bassEnergy;
                            _latestSnapshot.RmsLeft = rmsLeft;
                            _latestSnapshot.RmsRight = rmsRight;
                            _latestSnapshot.Version++;
                        }
                    }
                }
            }
            else
            {
                long lastAudioTicks = Volatile.Read(ref _lastAudioDataTicks);
                if (_latestSnapshot.HasSignal && lastAudioTicks > 0 && (nowTicks - lastAudioTicks) > SilenceStaleTicks)
                {
                    lock (_sampleSync)
                    {
                        lastAudioTicks = Volatile.Read(ref _lastAudioDataTicks);
                        if (!_hasNewAudio && lastAudioTicks > 0 && (nowTicks - lastAudioTicks) > SilenceStaleTicks)
                        {
                            Array.Clear(_magnitudes, 0, _magnitudes.Length);
                            Array.Clear(_snapshotBuffer, 0, _snapshotBuffer.Length);
                            Array.Clear(_waveformBuffer, 0, _waveformBuffer.Length);
                            _latestSnapshot.HasSignal = false;
                            _latestSnapshot.BassEnergy = 0f;
                            _latestSnapshot.RmsLeft = 0f;
                            _latestSnapshot.RmsRight = 0f;
                            _latestSnapshot.Version++;
                        }
                    }
                }
            }

            return _latestSnapshot;
        }

        private void StopInternal()
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= CaptureOnDataAvailable;
                _capture.RecordingStopped -= CaptureOnRecordingStopped;

                try
                {
                    _capture.StopRecording();
                }
                catch
                {
                }

                _capture.Dispose();
                _capture = null;
            }

            if (_device != null)
            {
                _device.Dispose();
                _device = null;
            }

            lock (_sampleSync)
            {
                _fftInput = Array.Empty<float>();
                _fftWindow = Array.Empty<float>();
                _fftBuffer = Array.Empty<Complex>();
                _magnitudes = Array.Empty<float>();
                _snapshotBuffer = Array.Empty<float>();
                Array.Clear(_waveformBuffer, 0, _waveformBuffer.Length);
                _fftWriteIndex = 0;
                _accumLeftSq = 0f;
                _accumRightSq = 0f;
                _accumSamples = 0;
                _hasNewAudio = false;
                Volatile.Write(ref _lastAudioDataTicks, 0);
                _latestSnapshot = new SpectrumSnapshot(Array.Empty<float>(), _waveformBuffer, SampleRate);
            }

            IsCapturing = false;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                StopInternal();
            }
        }
    }

    internal sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;

        public AudioDeviceWatcher()
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public event Action<string>? DefaultRenderDeviceChanged;
        public event Action<string>? DeviceAdded;
        public event Action<string>? DeviceRemoved;
        public event Action<string, DeviceState>? DeviceStateChanged;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                DefaultRenderDeviceChanged?.Invoke(defaultDeviceId);
            }
        }

        public void OnDeviceAdded(string deviceId)
        {
            DeviceAdded?.Invoke(deviceId);
        }

        public void OnDeviceRemoved(string deviceId)
        {
            DeviceRemoved?.Invoke(deviceId);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            DeviceStateChanged?.Invoke(deviceId, newState);
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }

        public void Dispose()
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
            }

            _enumerator.Dispose();
        }
    }

    internal static class AudioDeviceCatalog
    {
        public static MMDevice GetDeviceOrDefault(string? deviceId)
        {
            using var enumerator = new MMDeviceEnumerator();

            if (string.IsNullOrWhiteSpace(deviceId) || string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase))
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device != null && device.State == DeviceState.Active)
                {
                    return device;
                }
            }
            catch
            {
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public static List<AudioDeviceInfo> GetRenderDevices()
        {
            var result = new List<AudioDeviceInfo>();

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                if (device is MMDevice mmDevice)
                {
                    try
                    {
                        result.Add(new AudioDeviceInfo
                        {
                            Id = mmDevice.ID,
                            Name = mmDevice.FriendlyName
                        });
                    }
                    finally
                    {
                        mmDevice.Dispose();
                    }
                }
            }

            return result;
        }
    }

    internal sealed class AudioDeviceInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    internal sealed class SpectrumSnapshot
    {
        public SpectrumSnapshot(float[] magnitudes, float[] waveform, int sampleRate)
        {
            Magnitudes = magnitudes;
            Waveform = waveform;
            SampleRate = sampleRate;
        }

        public float[] Magnitudes { get; }
        public float[] Waveform { get; }
        public int SampleRate { get; }
        public bool HasSignal { get; set; }
        public float BassEnergy { get; set; }
        public float RmsLeft { get; set; }
        public float RmsRight { get; set; }
        public long Version { get; set; }
    }

    internal static class AudioMath
    {
        private static readonly Guid SubtypeIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private static bool IsFloat32(WaveFormat format)
        {
            if (format.BitsPerSample != 32)
            {
                return false;
            }

            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return true;
            }

            if (format is WaveFormatExtensible ext)
            {
                return ext.SubFormat == SubtypeIeeeFloat;
            }

            return format.Encoding == WaveFormatEncoding.Extensible;
        }

        private static bool IsPcm16(WaveFormat format)
        {
            if (format.BitsPerSample != 16)
            {
                return false;
            }

            if (format.Encoding == WaveFormatEncoding.Pcm)
            {
                return true;
            }

            if (format is WaveFormatExtensible ext)
            {
                return ext.SubFormat == SubtypePcm;
            }

            return true;
        }

        public static int WriteSamplesToRing(
            byte[] source,
            int length,
            WaveFormat format,
            int frameCount,
            float[] ringBuffer,
            int writeIndex,
            out float leftSqSum,
            out float rightSqSum)
        {
            leftSqSum = 0f;
            rightSqSum = 0f;
            int ringLen = ringBuffer.Length;
            int channels = Math.Max(1, format.Channels);

            if (IsFloat32(format))
            {
                float invChannels = 1f / channels;
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    float l = 0f;
                    float r = 0f;
                    int baseOffset = i * channels * 4;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = baseOffset + ch * 4;
                        if (offset + 4 > length)
                        {
                            break;
                        }

                        float sample = BitConverter.ToSingle(source, offset);
                        mix += sample;
                        if (ch == 0) l = sample;
                        else if (ch == 1) r = sample;
                    }

                    if (channels == 1) r = l;
                    leftSqSum += l * l;
                    rightSqSum += r * r;

                    ringBuffer[writeIndex] = mix * invChannels;
                    writeIndex++;
                    if (writeIndex >= ringLen)
                    {
                        writeIndex = 0;
                    }
                }

                return writeIndex;
            }

            if (IsPcm16(format))
            {
                float invScale = 1f / (32768f * channels);
                const float inv16 = 1f / 32768f;
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    float l = 0f;
                    float r = 0f;
                    int baseOffset = i * channels * 2;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = baseOffset + ch * 2;
                        if (offset + 2 > length)
                        {
                            break;
                        }

                        short raw = BitConverter.ToInt16(source, offset);
                        mix += raw;
                        if (ch == 0) l = raw * inv16;
                        else if (ch == 1) r = raw * inv16;
                    }

                    if (channels == 1) r = l;
                    leftSqSum += l * l;
                    rightSqSum += r * r;

                    ringBuffer[writeIndex] = mix * invScale;
                    writeIndex++;
                    if (writeIndex >= ringLen)
                    {
                        writeIndex = 0;
                    }
                }

                return writeIndex;
            }

            for (int i = 0; i < frameCount; i++)
            {
                ringBuffer[writeIndex] = 0f;
                writeIndex++;
                if (writeIndex >= ringLen)
                {
                    writeIndex = 0;
                }
            }

            return writeIndex;
        }
    }
}
