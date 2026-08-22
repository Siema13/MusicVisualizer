using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicVisualizerLoopback
{
    internal static class Program
    {
        [STAThread]
        private static async Task Main(string[] args)
        {
            using var app = PluginApp.FromArgs(args);
            await app.RunAsync();
        }
    }

    internal sealed class PluginApp : IDisposable
    {
        private const string ActionUuid = "com.musicvisualizer.loopback.visualizer";
        private const int RenderWidth = 144;
        private const int RenderHeight = 144;
        private const int RenderTargetBoth = 0;
        private const int RenderState = 0;
        private const int DefaultFftSize = 2048;
        private const int RenderIntervalMs = 16;

        private readonly int _port;
        private readonly string _pluginUuid;
        private readonly string _registerEvent;
        private readonly string? _infoJson;

        private readonly ClientWebSocket _socket;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock;
        private readonly SemaphoreSlim _audioRestartLock;
        private readonly ConcurrentDictionary<string, ActionRuntime> _actions;
        private readonly AudioDeviceWatcher _deviceWatcher;

        private AudioEngine? _audioEngine;
        private Task? _receiveTask;
        private Task? _renderTask;
        private volatile bool _disposed;

        private PluginApp(int port, string pluginUuid, string registerEvent, string? infoJson)
        {
            _port = port;
            _pluginUuid = pluginUuid;
            _registerEvent = registerEvent;
            _infoJson = infoJson;

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _sendLock = new SemaphoreSlim(1, 1);
            _audioRestartLock = new SemaphoreSlim(1, 1);
            _actions = new ConcurrentDictionary<string, ActionRuntime>(StringComparer.Ordinal);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            _deviceWatcher = new AudioDeviceWatcher();
            _deviceWatcher.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;
            _deviceWatcher.DeviceAdded += OnAudioDeviceTopologyChanged;
            _deviceWatcher.DeviceRemoved += OnAudioDeviceRemoved;
            _deviceWatcher.DeviceStateChanged += OnAudioDeviceStateChanged;
        }

        public static PluginApp FromArgs(string[] args)
        {
            var dict = ParseArgs(args);

            if (!dict.TryGetValue("-port", out var portText) && !dict.TryGetValue("--port", out portText))
            {
                throw new InvalidOperationException("Missing -port argument.");
            }

            if (!dict.TryGetValue("-pluginUUID", out var pluginUuid) && !dict.TryGetValue("--pluginUUID", out pluginUuid))
            {
                throw new InvalidOperationException("Missing -pluginUUID argument.");
            }

            if (!dict.TryGetValue("-registerEvent", out var registerEvent) && !dict.TryGetValue("--registerEvent", out registerEvent))
            {
                throw new InvalidOperationException("Missing -registerEvent argument.");
            }

            dict.TryGetValue("-info", out var infoJson);
            if (string.IsNullOrWhiteSpace(infoJson))
            {
                dict.TryGetValue("--info", out infoJson);
            }

            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                throw new InvalidOperationException("Invalid -port argument.");
            }

            return new PluginApp(port, pluginUuid, registerEvent, infoJson);
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (string.IsNullOrEmpty(key) || key[0] != '-')
                {
                    continue;
                }

                var value = string.Empty;
                if (i + 1 < args.Length)
                {
                    var next = args[i + 1];
                    if (!string.IsNullOrEmpty(next) && next[0] != '-')
                    {
                        value = next;
                        i++;
                    }
                }

                result[key] = value;
            }

            return result;
        }

        public async Task RunAsync()
        {
            await ConnectAsync(_cts.Token);
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            _renderTask = RenderLoopAsync(_cts.Token);
            await Task.WhenAll(_receiveTask, _renderTask);
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var uri = new Uri($"ws://127.0.0.1:{_port}");
            await _socket.ConnectAsync(uri, cancellationToken);

            await SendRawAsync(new
            {
                @event = _registerEvent,
                uuid = _pluginUuid
            }, cancellationToken);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];

            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _cts.Cancel();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    await HandleIncomingEventAsync(doc.RootElement, cancellationToken);
                }
                catch (Exception ex)
                {
                    await SendLogMessageAsync("JSON parse error: " + ex.Message, cancellationToken);
                }
            }
        }

        private async Task HandleIncomingEventAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var eventName = JsonHelper.GetString(root, "event");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            switch (eventName)
            {
                case "willAppear":
                    await HandleWillAppearAsync(root, cancellationToken);
                    break;
                case "willDisappear":
                    await HandleWillDisappearAsync(root);
                    break;
                case "didReceiveSettings":
                    await HandleDidReceiveSettingsAsync(root, cancellationToken);
                    break;
                case "propertyInspectorDidAppear":
                    await HandlePropertyInspectorDidAppearAsync(root, cancellationToken);
                    break;
                case "propertyInspectorDidDisappear":
                    break;
                case "sendToPlugin":
                    await HandleSendToPluginAsync(root, cancellationToken);
                    break;
                case "keyDown":
                    await HandleKeyDownAsync(root, cancellationToken);
                    break;
                case "systemDidWakeUp":
                    await RestartAudioForCurrentSettingsAsync(cancellationToken);
                    break;
            }
        }

        private async Task HandleWillAppearAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var action = JsonHelper.GetString(root, "action");
            if (!string.Equals(action, ActionUuid, StringComparison.Ordinal))
            {
                return;
            }

            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            var settings = ExtractSettingsFromEvent(root);
            var runtime = _actions.GetOrAdd(context, _ => new ActionRuntime(context, settings));
            runtime.Settings = settings;
            runtime.EnsureArrays(settings.BarCount);

            await EnsureAudioAsync(cancellationToken);
            await PushImageAsync(runtime, cancellationToken);
        }

        private Task HandleWillDisappearAsync(JsonElement root)
        {
            var context = JsonHelper.GetString(root, "context");
            if (!string.IsNullOrWhiteSpace(context))
            {
                _actions.TryRemove(context, out _);
            }

            if (_actions.IsEmpty)
            {
                _audioEngine?.Dispose();
                _audioEngine = null;
                SpectrumMapper.ResetAll();
            }

            return Task.CompletedTask;
        }

        private async Task HandleDidReceiveSettingsAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var action = JsonHelper.GetString(root, "action");
            if (!string.Equals(action, ActionUuid, StringComparison.Ordinal))
            {
                return;
            }

            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            var settings = ExtractSettingsFromEvent(root);
            var runtime = _actions.GetOrAdd(context, _ => new ActionRuntime(context, settings));
            runtime.Settings = settings;
            runtime.EnsureArrays(settings.BarCount);

            await EnsureAudioAsync(cancellationToken);
            await SendRuntimeStateAsync(context, cancellationToken);
        }

        private async Task HandlePropertyInspectorDidAppearAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            await SendCaptureDevicesAsync(context, cancellationToken);
            await SendRuntimeStateAsync(context, cancellationToken);
        }

        private async Task HandleSendToPluginAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            if (!_actions.TryGetValue(context, out var runtime))
            {
                return;
            }

            if (!root.TryGetProperty("payload", out var payload))
            {
                return;
            }

            var command = JsonHelper.GetString(payload, "event");
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            switch (command)
            {
                case "piReady":
                    await SendCaptureDevicesAsync(context, cancellationToken);
                    await SendRuntimeStateAsync(context, cancellationToken);
                    break;

                case "getCaptureDevices":
                    await SendCaptureDevicesAsync(context, cancellationToken);
                    break;

                case "setPaused":
                    if (payload.TryGetProperty("paused", out var pausedProp) &&
                        (pausedProp.ValueKind == JsonValueKind.True || pausedProp.ValueKind == JsonValueKind.False))
                    {
                        runtime.Settings.Paused = pausedProp.GetBoolean();
                        await SaveSettingsAsync(runtime.Context, runtime.Settings, cancellationToken);
                        await SendRuntimeStateAsync(runtime.Context, cancellationToken);
                    }
                    break;

                case "previewSettings":
                    if (payload.TryGetProperty("settings", out var previewSettings))
                    {
                        var preview = DeserializeSettings(previewSettings, runtime.Settings.Clone());
                        SettingsNormalizer.Normalize(preview);
                        runtime.Settings = preview;
                        runtime.EnsureArrays(preview.BarCount);
                        await EnsureAudioAsync(cancellationToken);
                    }
                    break;

                case "resetDefaults":
                    runtime.Settings = VisualizerSettings.CreateDefault();
                    runtime.EnsureArrays(runtime.Settings.BarCount);
                    await SaveSettingsAsync(runtime.Context, runtime.Settings, cancellationToken);
                    await SendToPropertyInspectorAsync(runtime.Context, new
                    {
                        @event = "defaultsLoaded",
                        settings = runtime.Settings
                    }, cancellationToken);
                    await EnsureAudioAsync(cancellationToken);
                    break;
            }
        }

        private async Task HandleKeyDownAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return;
            }

            if (_actions.TryGetValue(context, out var runtime))
            {
                runtime.Settings.Paused = !runtime.Settings.Paused;
                await SaveSettingsAsync(runtime.Context, runtime.Settings, cancellationToken);
                await SendRuntimeStateAsync(runtime.Context, cancellationToken);
            }
        }

        private async Task RenderLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SpectrumSnapshot? snapshot = _audioEngine?.GetSnapshot();

                    foreach (var pair in _actions.ToArray())
                    {
                        var runtime = pair.Value;
                        UpdateAnimation(runtime, snapshot);
                        await PushImageAsync(runtime, cancellationToken);
                    }

                    await Task.Delay(RenderIntervalMs, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await SendLogMessageAsync("Render loop error: " + ex.Message, cancellationToken);
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        private void UpdateAnimation(ActionRuntime runtime, SpectrumSnapshot? snapshot)
        {
            var settings = runtime.Settings;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            runtime.EnsureArrays(barCount);

            if (settings.Paused || snapshot is null || snapshot.Magnitudes.Length == 0)
            {
                for (int i = 0; i < barCount; i++)
                {
                    runtime.Bars[i] = Math.Max(0f, runtime.Bars[i] - 0.11f);
                    runtime.Peaks[i] = settings.PeakHold
                        ? Math.Max(0f, runtime.Peaks[i] - (float)settings.PeakFallSpeed)
                        : 0f;
                }
                return;
            }

            var mapped = SpectrumMapper.Map(snapshot, settings);

            float rise = (float)(0.30 + settings.MotionSpeed * 0.95);
            float fall = (float)(0.14 + settings.MotionSpeed * 0.78);

            for (int i = 0; i < barCount; i++)
            {
                float raw = mapped[i];
                float prev = runtime.Bars[i];

                float smooth = raw > prev
                    ? prev + (raw - prev) * rise
                    : prev + (raw - prev) * fall;

                smooth = Math.Clamp(smooth, 0f, 1f);
                runtime.Bars[i] = smooth;

                runtime.Peaks[i] = settings.PeakHold
                    ? Math.Max(smooth, Math.Max(0f, runtime.Peaks[i] - (float)settings.PeakFallSpeed))
                    : 0f;
            }
        }

        private async Task PushImageAsync(ActionRuntime runtime, CancellationToken cancellationToken)
        {
            var pngDataUrl = Renderer.RenderToPngDataUrl(RenderWidth, RenderHeight, runtime.Settings, runtime.Bars, runtime.Peaks);

            await SendRawAsync(new
            {
                @event = "setImage",
                context = runtime.Context,
                payload = new
                {
                    image = pngDataUrl,
                    target = RenderTargetBoth,
                    state = RenderState
                }
            }, cancellationToken);
        }

        private async Task EnsureAudioAsync(CancellationToken cancellationToken)
        {
            var referenceSettings = _actions.Values.FirstOrDefault()?.Settings ?? VisualizerSettings.CreateDefault();

            if (_audioEngine == null)
            {
                _audioEngine = new AudioEngine(DefaultFftSize);
                await _audioEngine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);
                SpectrumMapper.ResetAll();
                return;
            }

            if (!_audioEngine.Matches(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing))
            {
                await _audioEngine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);
                SpectrumMapper.ResetAll();
            }
        }

        private async Task RestartAudioForCurrentSettingsAsync(CancellationToken cancellationToken)
        {
            await _audioRestartLock.WaitAsync(cancellationToken);
            try
            {
                var referenceSettings = _actions.Values.FirstOrDefault()?.Settings ?? VisualizerSettings.CreateDefault();

                if (_audioEngine == null)
                {
                    _audioEngine = new AudioEngine(DefaultFftSize);
                }

                await _audioEngine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);
                SpectrumMapper.ResetAll();

                foreach (var context in _actions.Keys)
                {
                    await SendRuntimeStateAsync(context, cancellationToken);
                }
            }
            finally
            {
                _audioRestartLock.Release();
            }
        }

        private void OnDefaultRenderDeviceChanged(string newDeviceId)
        {
            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool anyDefault = _actions.Values.Any(x =>
                        string.Equals(x.Settings.CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

                    if (!anyDefault)
                    {
                        return;
                    }

                    await RestartAudioForCurrentSettingsAsync(_cts.Token);

                    foreach (var context in _actions.Keys)
                    {
                        await SendCaptureDevicesAsync(context, _cts.Token);
                        await SendRuntimeStateAsync(context, _cts.Token);
                    }
                }
                catch
                {
                }
            });
        }

        private void OnAudioDeviceTopologyChanged(string deviceId)
        {
            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var context in _actions.Keys)
                    {
                        await SendCaptureDevicesAsync(context, _cts.Token);
                    }
                }
                catch
                {
                }
            });
        }

        private void OnAudioDeviceRemoved(string removedDeviceId)
        {
            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool selectedRemoved = _actions.Values.Any(x =>
                        string.Equals(x.Settings.CaptureDeviceId, removedDeviceId, StringComparison.OrdinalIgnoreCase));

                    bool anyDefault = _actions.Values.Any(x =>
                        string.Equals(x.Settings.CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

                    if (selectedRemoved || anyDefault)
                    {
                        await RestartAudioForCurrentSettingsAsync(_cts.Token);
                    }

                    foreach (var context in _actions.Keys)
                    {
                        await SendCaptureDevicesAsync(context, _cts.Token);
                        await SendRuntimeStateAsync(context, _cts.Token);
                    }
                }
                catch
                {
                }
            });
        }

        private void OnAudioDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool tracked = _actions.Values.Any(x =>
                        string.Equals(x.Settings.CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Settings.CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

                    if (!tracked)
                    {
                        return;
                    }

                    if (newState != DeviceState.Active)
                    {
                        await RestartAudioForCurrentSettingsAsync(_cts.Token);
                    }

                    foreach (var context in _actions.Keys)
                    {
                        await SendCaptureDevicesAsync(context, _cts.Token);
                        await SendRuntimeStateAsync(context, _cts.Token);
                    }
                }
                catch
                {
                }
            });
        }

        private async Task SaveSettingsAsync(string context, VisualizerSettings settings, CancellationToken cancellationToken)
        {
            SettingsNormalizer.Normalize(settings);

            await SendRawAsync(new
            {
                @event = "setSettings",
                context,
                payload = settings
            }, cancellationToken);
        }

        private async Task SendCaptureDevicesAsync(string context, CancellationToken cancellationToken)
        {
            var devices = AudioDeviceCatalog.GetRenderDevices()
                .Select(d => new
                {
                    id = d.Id,
                    name = d.Name
                })
                .ToList();

            devices.Insert(0, new
            {
                id = "default",
                name = "Domyślne urządzenie systemowe"
            });

            var selectedId = _actions.TryGetValue(context, out var runtime)
                ? runtime.Settings.CaptureDeviceId
                : "default";

            await SendToPropertyInspectorAsync(context, new
            {
                @event = "captureDevices",
                devices,
                selectedId
            }, cancellationToken);
        }

        private async Task SendRuntimeStateAsync(string context, CancellationToken cancellationToken)
        {
            if (_actions.TryGetValue(context, out var runtime))
            {
                var stats = SpectrumMapper.GetStats(runtime.Settings.CaptureDeviceId, runtime.Settings.FreqMin, runtime.Settings.FreqMax);

                await SendToPropertyInspectorAsync(context, new
                {
                    @event = "runtimeState",
                    isCapturing = _audioEngine?.IsCapturing ?? false,
                    isRendering = true,
                    sampleRate = _audioEngine?.SampleRate ?? 0,
                    fftSize = _audioEngine?.FftSize ?? DefaultFftSize,
                    lastError = _audioEngine?.LastError,
                    autoGain = stats.AutoGain,
                    peakGuard = stats.PeakGuard,
                    tilt = stats.RangeBalance
                }, cancellationToken);

                return;
            }

            await SendToPropertyInspectorAsync(context, new
            {
                @event = "runtimeState",
                isCapturing = _audioEngine?.IsCapturing ?? false,
                isRendering = true,
                sampleRate = _audioEngine?.SampleRate ?? 0,
                fftSize = _audioEngine?.FftSize ?? DefaultFftSize,
                lastError = _audioEngine?.LastError
            }, cancellationToken);
        }

        private async Task SendToPropertyInspectorAsync(string context, object payload, CancellationToken cancellationToken)
        {
            await SendRawAsync(new
            {
                action = ActionUuid,
                @event = "sendToPropertyInspector",
                context,
                payload
            }, cancellationToken);
        }

        private async Task SendLogMessageAsync(string message, CancellationToken cancellationToken)
        {
            await SendRawAsync(new
            {
                @event = "logMessage",
                payload = new
                {
                    message
                }
            }, cancellationToken);
        }

        private async Task SendRawAsync(object payload, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private VisualizerSettings ExtractSettingsFromEvent(JsonElement root)
        {
            var settings = VisualizerSettings.CreateDefault();

            if (root.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("settings", out var settingsElement))
            {
                settings = DeserializeSettings(settingsElement, settings);
            }

            SettingsNormalizer.Normalize(settings);
            return settings;
        }

        private VisualizerSettings DeserializeSettings(JsonElement element, VisualizerSettings fallback)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<VisualizerSettings>(element.GetRawText(), _jsonOptions);
                return parsed ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();

            try
            {
                _deviceWatcher.Dispose();
            }
            catch
            {
            }

            try
            {
                _audioEngine?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch
            {
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _audioRestartLock.Dispose();
            _cts.Dispose();
        }
    }

    internal sealed class ActionRuntime
    {
        public ActionRuntime(string context, VisualizerSettings settings)
        {
            Context = context;
            Settings = settings;
            Bars = new float[Math.Clamp(settings.BarCount, 2, 32)];
            Peaks = new float[Math.Clamp(settings.BarCount, 2, 32)];
        }

        public string Context { get; }
        public VisualizerSettings Settings { get; set; }
        public float[] Bars { get; private set; }
        public float[] Peaks { get; private set; }

        public void EnsureArrays(int barCount)
        {
            if (Bars.Length != barCount)
            {
                Bars = new float[barCount];
            }

            if (Peaks.Length != barCount)
            {
                Peaks = new float[barCount];
            }
        }
    }

    internal sealed class AudioEngine : IDisposable
    {
        private readonly object _sync = new object();
        private readonly object _sampleSync = new object();
        private readonly int _fftSize;

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _device;
        private float[] _fftInput = Array.Empty<float>();
        private int _fftWriteIndex;
        private float[] _smoothedMagnitudes = Array.Empty<float>();
        private SpectrumSnapshot _latestSnapshot;

        public AudioEngine(int fftSize)
        {
            _fftSize = fftSize;
            _latestSnapshot = new SpectrumSnapshot(Array.Empty<float>(), 48000);
            CurrentDeviceId = "default";
            CurrentSmoothing = 0.22;
        }

        public bool IsCapturing { get; private set; }
        public int SampleRate { get; private set; } = 48000;
        public int FftSize => _fftSize;
        public string CurrentDeviceId { get; private set; }
        public double CurrentSmoothing { get; private set; }
        public string? LastError { get; private set; }

        public bool Matches(string deviceId, double smoothing)
        {
            var normalizedId = string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId;
            return string.Equals(CurrentDeviceId, normalizedId, StringComparison.OrdinalIgnoreCase)
                   && Math.Abs(CurrentSmoothing - smoothing) < 0.0001;
        }

        public async Task StartAsync(string deviceId, double smoothing, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                lock (_sync)
                {
                    StopInternal();

                    try
                    {
                        _device = AudioDeviceCatalog.GetDeviceOrDefault(deviceId);
                        _capture = new WasapiLoopbackCapture(_device);

                        SampleRate = _capture.WaveFormat.SampleRate;
                        CurrentDeviceId = string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId;
                        CurrentSmoothing = smoothing;
                        LastError = null;

                        _fftInput = new float[_fftSize];
                        _smoothedMagnitudes = new float[_fftSize / 2];
                        _fftWriteIndex = 0;
                        _latestSnapshot = new SpectrumSnapshot(Array.Empty<float>(), SampleRate);

                        _capture.DataAvailable += CaptureOnDataAvailable;
                        _capture.RecordingStopped += CaptureOnRecordingStopped;
                        _capture.StartRecording();

                        IsCapturing = true;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        IsCapturing = false;
                        StopInternal();
                    }
                }
            }, cancellationToken);
        }

        private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture == null)
            {
                return;
            }

            int frames = e.BytesRecorded / _capture.WaveFormat.BlockAlign;
            if (frames <= 0)
            {
                return;
            }

            float[] mono = AudioMath.ReadMonoSamples(e.Buffer, e.BytesRecorded, _capture.WaveFormat, frames);

            lock (_sampleSync)
            {
                for (int i = 0; i < mono.Length; i++)
                {
                    _fftInput[_fftWriteIndex] = mono[i];
                    _fftWriteIndex = (_fftWriteIndex + 1) % _fftSize;
                }

                BuildSpectrumFromLatestWindow();
            }
        }

        private void BuildSpectrumFromLatestWindow()
        {
            if (_capture == null || _fftInput.Length != _fftSize)
            {
                return;
            }

            var window = new float[_fftSize];
            int tail = _fftSize - _fftWriteIndex;

            Array.Copy(_fftInput, _fftWriteIndex, window, 0, tail);
            Array.Copy(_fftInput, 0, window, tail, _fftWriteIndex);

            AudioMath.ApplyHannWindow(window);

            var fftBuffer = new Complex[_fftSize];
            for (int i = 0; i < _fftSize; i++)
            {
                fftBuffer[i].X = window[i];
                fftBuffer[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, (int)Math.Log2(_fftSize), fftBuffer);

            float smoothing = (float)Math.Clamp(CurrentSmoothing, 0.0, 0.95);
            float[] magnitudes = new float[_fftSize / 2];

            for (int i = 0; i < magnitudes.Length; i++)
            {
                double re = fftBuffer[i].X;
                double im = fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(re * re + im * im);

                _smoothedMagnitudes[i] = _smoothedMagnitudes[i] + (mag - _smoothedMagnitudes[i]) * (1f - smoothing);
                magnitudes[i] = _smoothedMagnitudes[i];
            }

            lock (_sync)
            {
                _latestSnapshot = new SpectrumSnapshot(magnitudes, SampleRate);
            }
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
            lock (_sync)
            {
                return _latestSnapshot.Clone();
            }
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

            _fftInput = Array.Empty<float>();
            _smoothedMagnitudes = Array.Empty<float>();
            _fftWriteIndex = 0;
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

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
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

            return enumerator.GetDevice(deviceId);
        }

        public static List<AudioDeviceInfo> GetRenderDevices()
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            var result = new List<AudioDeviceInfo>();

            foreach (var device in collection)
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
        public SpectrumSnapshot(float[] magnitudes, int sampleRate)
        {
            Magnitudes = magnitudes;
            SampleRate = sampleRate;
        }

        public float[] Magnitudes { get; }
        public int SampleRate { get; }

        public SpectrumSnapshot Clone()
        {
            return new SpectrumSnapshot((float[])Magnitudes.Clone(), SampleRate);
        }
    }

    internal static class SpectrumMapper
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, RangeAdaptiveState> States = new(StringComparer.OrdinalIgnoreCase);

        public static void ResetAll()
        {
            lock (Sync)
            {
                States.Clear();
            }
        }

        public static RangeAdaptiveStats GetStats(string captureDeviceId, int freqMin, int freqMax)
        {
            lock (Sync)
            {
                var key = BuildKey(captureDeviceId, freqMin, freqMax);
                if (States.TryGetValue(key, out var state))
                {
                    return new RangeAdaptiveStats
                    {
                        AutoGain = state.AutoGain,
                        PeakGuard = state.LastPeakGuard,
                        RangeBalance = state.LastRangeBalance
                    };
                }

                return new RangeAdaptiveStats
                {
                    AutoGain = 1.0,
                    PeakGuard = 1.0,
                    RangeBalance = 1.0
                };
            }
        }

        public static float[] Map(SpectrumSnapshot snapshot, VisualizerSettings settings)
        {
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            var output = new float[barCount];

            if (snapshot.Magnitudes.Length == 0)
            {
                return output;
            }

            double nyquist = snapshot.SampleRate / 2.0;
            int binCount = snapshot.Magnitudes.Length;

            int minHz = Math.Clamp(settings.FreqMin, 20, Math.Max(20, (int)nyquist - 1));
            int maxHz = Math.Clamp(settings.FreqMax, minHz + 1, (int)nyquist);

            int rangeStartBin = Math.Max(1, (int)Math.Floor((minHz / nyquist) * binCount));
            int rangeEndBin = Math.Min(binCount - 1, (int)Math.Ceiling((maxHz / nyquist) * binCount));

            if (rangeEndBin <= rangeStartBin)
            {
                rangeEndBin = Math.Min(binCount - 1, rangeStartBin + 1);
            }

            double globalAvgEnergy = 0.0;
            double globalPeak = 0.0;
            int globalCount = 0;

            double lowEnergy = 0.0;
            double midEnergy = 0.0;
            double highEnergy = 0.0;
            int lowCount = 0;
            int midCount = 0;
            int highCount = 0;

            for (int i = rangeStartBin; i <= rangeEndBin; i++)
            {
                double freq = (i / (double)binCount) * nyquist;
                double value = Math.Max(0.0, snapshot.Magnitudes[i]);

                globalAvgEnergy += value;
                globalPeak = Math.Max(globalPeak, value);
                globalCount++;

                if (freq < 250.0)
                {
                    lowEnergy += value;
                    lowCount++;
                }
                else if (freq < 4000.0)
                {
                    midEnergy += value;
                    midCount++;
                }
                else
                {
                    highEnergy += value;
                    highCount++;
                }
            }

            globalAvgEnergy = globalCount > 0 ? globalAvgEnergy / globalCount : 0.0;
            lowEnergy = lowCount > 0 ? lowEnergy / lowCount : globalAvgEnergy;
            midEnergy = midCount > 0 ? midEnergy / midCount : globalAvgEnergy;
            highEnergy = highCount > 0 ? highEnergy / highCount : globalAvgEnergy;

            double safeGlobalEnergy = Math.Max(globalAvgEnergy, 1e-6);
            double targetEnergy = settings.AutoGainMode switch
            {
                "off" => safeGlobalEnergy,
                "soft" => 0.0090,
                "balanced" => 0.0105,
                "strict" => 0.0120,
                _ => 0.0105
            };

            double minGain = settings.AutoGainMode switch
            {
                "off" => 1.0,
                "soft" => 0.88,
                "balanced" => 0.78,
                "strict" => 0.70,
                _ => 0.78
            };

            double maxGain = settings.AutoGainMode switch
            {
                "off" => 1.0,
                "soft" => 1.25,
                "balanced" => 1.55,
                "strict" => 1.80,
                _ => 1.55
            };

            double targetGain = settings.AutoGainMode == "off"
                ? 1.0
                : Math.Clamp(targetEnergy / safeGlobalEnergy, minGain, maxGain);

            double peakLimit = settings.AutoGainMode switch
            {
                "off" => 1.0,
                "soft" => 0.14,
                "balanced" => 0.11,
                "strict" => 0.09,
                _ => 0.11
            };

            double peakGuard = globalPeak > peakLimit ? peakLimit / globalPeak : 1.0;
            peakGuard = settings.AutoGainMode == "off"
                ? 1.0
                : Math.Clamp(peakGuard, 0.55, 1.0);

            double rangeCenterHz = Math.Sqrt(minHz * maxHz);
            double rangeBalance = Math.Pow(rangeCenterHz / 1000.0, 0.10);
            rangeBalance = Math.Clamp(rangeBalance, 0.82, 1.18);

            RangeAdaptiveState adaptiveState;
            lock (Sync)
            {
                var key = BuildKey(settings.CaptureDeviceId, minHz, maxHz);
                if (!States.TryGetValue(key, out adaptiveState!))
                {
                    adaptiveState = new RangeAdaptiveState
                    {
                        AutoGain = 1.0,
                        LastPeakGuard = 1.0,
                        LastRangeBalance = rangeBalance
                    };
                    States[key] = adaptiveState;
                }

                double gainLerp = settings.AutoGainMode switch
                {
                    "off" => 1.0,
                    "soft" => 0.020,
                    "balanced" => 0.035,
                    "strict" => 0.055,
                    _ => 0.035
                };

                adaptiveState.AutoGain += (targetGain - adaptiveState.AutoGain) * gainLerp;
                adaptiveState.LastPeakGuard = peakGuard;
                adaptiveState.LastRangeBalance = rangeBalance;
            }

            double autoGain = adaptiveState.AutoGain;
            double trimLinear = Math.Pow(10.0, settings.DeviceTrimDb / 20.0);

            double logMin = Math.Log10(minHz);
            double logMax = Math.Log10(maxHz);

            for (int i = 0; i < barCount; i++)
            {
                double startRatio = i / (double)barCount;
                double endRatio = (i + 1) / (double)barCount;

                double bandStartHz = Math.Pow(10, logMin + startRatio * (logMax - logMin));
                double bandEndHz = Math.Pow(10, logMin + endRatio * (logMax - logMin));
                double bandCenterHz = Math.Sqrt(bandStartHz * bandEndHz);

                int startBin = Math.Max(rangeStartBin, (int)Math.Floor((bandStartHz / nyquist) * binCount));
                int endBin = Math.Min(rangeEndBin, (int)Math.Ceiling((bandEndHz / nyquist) * binCount));

                if (endBin <= startBin)
                {
                    endBin = Math.Min(rangeEndBin, startBin + 1);
                }

                double sum = 0.0;
                double peak = 0.0;
                int count = 0;

                for (int b = startBin; b <= endBin; b++)
                {
                    double value = Math.Max(0.0, snapshot.Magnitudes[b]);
                    sum += value * value;
                    peak = Math.Max(peak, value);
                    count++;
                }

                double rms = count > 0 ? Math.Sqrt(sum / count) : 0.0;
                double mixed = rms * 0.80 + peak * 0.20;

                double compressed = settings.SpectrumMode switch
                {
                    "raw" => Math.Clamp(mixed * 20.0, 0.0, 1.0),
                    "equalEnergy" => Math.Log10(1.0 + mixed * 34.0) / Math.Log10(35.0),
                    _ => Math.Log10(1.0 + mixed * 42.0) / Math.Log10(43.0)
                };

                double tiltWeight = ComputeTiltWeight(bandCenterHz, settings.TiltAmountDbOct, settings.SpectrumMode);
                double bassWeight = ComputeBassWeight(bandCenterHz, settings.BassTaming);
                double highWeight = ComputeHighWeight(bandCenterHz, settings.HighBoost);

                double subBandEnergy = bandCenterHz < 250.0
                    ? lowEnergy
                    : bandCenterHz < 4000.0
                        ? midEnergy
                        : highEnergy;

                double equalizeWeight = 1.0;
                if (settings.SpectrumMode == "equalEnergy" || settings.SpectrumMode == "perceptual")
                {
                    double reference = Math.Max(midEnergy, 1e-6);
                    equalizeWeight = Math.Sqrt(reference / Math.Max(subBandEnergy, 1e-6));
                    equalizeWeight = Math.Clamp(equalizeWeight, 0.72, 1.60);
                }

                double normalized =
                    compressed *
                    settings.Sensitivity *
                    trimLinear *
                    autoGain *
                    peakGuard *
                    rangeBalance *
                    tiltWeight *
                    bassWeight *
                    highWeight *
                    equalizeWeight;

                normalized = ApplyRangeCompensation(normalized, settings.RangeCompensation);
                output[i] = (float)Math.Clamp(normalized, 0.0, 1.0);
            }

            return output;
        }

        private static double ComputeTiltWeight(double bandCenterHz, double tiltAmountDbOct, string spectrumMode)
        {
            double octavesFrom1k = Math.Log(Math.Max(1.0, bandCenterHz) / 1000.0, 2.0);
            double dbPerOct = spectrumMode switch
            {
                "raw" => 0.0,
                "equalEnergy" => tiltAmountDbOct * 0.45,
                _ => tiltAmountDbOct
            };

            double db = octavesFrom1k * dbPerOct;
            double linear = Math.Pow(10.0, db / 20.0);
            return Math.Clamp(linear, 0.45, 2.20);
        }

        private static double ComputeBassWeight(double bandCenterHz, double bassTaming)
        {
            if (bandCenterHz >= 250.0)
            {
                return 1.0;
            }

            double t = 1.0 - (bandCenterHz / 250.0);
            double reduction = 1.0 - (0.38 * bassTaming * t);
            return Math.Clamp(reduction, 0.35, 1.0);
        }

        private static double ComputeHighWeight(double bandCenterHz, double highBoost)
        {
            if (bandCenterHz <= 4000.0)
            {
                return 1.0;
            }

            double t = Math.Min(1.0, (bandCenterHz - 4000.0) / 12000.0);
            double boost = 1.0 + (0.55 * highBoost * t);
            return Math.Clamp(boost, 1.0, 2.10);
        }

        private static double ApplyRangeCompensation(double value, double rangeCompensation)
        {
            value = Math.Max(0.0, value);

            double gamma = 1.0 / Math.Clamp(1.0 + rangeCompensation * 0.85, 1.0, 3.125);
            double shaped = Math.Pow(Math.Min(1.0, value), gamma);

            double overflow = value > 1.0
                ? 1.0 - (1.0 / (1.0 + (value - 1.0) * 2.0))
                : 0.0;

            return Math.Clamp(shaped + overflow * 0.18, 0.0, 1.0);
        }

        private static string BuildKey(string captureDeviceId, int freqMin, int freqMax)
        {
            string device = string.IsNullOrWhiteSpace(captureDeviceId) ? "default" : captureDeviceId;
            return device + "|" + freqMin.ToString(CultureInfo.InvariantCulture) + "|" + freqMax.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class RangeAdaptiveState
    {
        public double AutoGain { get; set; } = 1.0;
        public double LastPeakGuard { get; set; } = 1.0;
        public double LastRangeBalance { get; set; } = 1.0;
    }

    internal sealed class RangeAdaptiveStats
    {
        public double AutoGain { get; set; }
        public double PeakGuard { get; set; }
        public double RangeBalance { get; set; }
    }

    internal static class AudioMath
    {
        public static float[] ReadMonoSamples(byte[] source, int length, WaveFormat format, int frameCount)
        {
            var destination = new float[frameCount];
            int channels = format.Channels;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = (i * channels + ch) * 4;
                        if (offset + 4 > length)
                        {
                            break;
                        }

                        mix += BitConverter.ToSingle(source, offset);
                    }

                    destination[i] = mix / channels;
                }

                return destination;
            }

            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = (i * channels + ch) * 2;
                        if (offset + 2 > length)
                        {
                            break;
                        }

                        mix += BitConverter.ToInt16(source, offset) / 32768f;
                    }

                    destination[i] = mix / channels;
                }

                return destination;
            }

            Array.Clear(destination, 0, destination.Length);
            return destination;
        }

        public static void ApplyHannWindow(float[] samples)
        {
            int n = samples.Length;
            if (n <= 1)
            {
                return;
            }

            for (int i = 0; i < n; i++)
            {
                samples[i] *= (float)FastFourierTransform.HannWindow(i, n);
            }
        }
    }

    internal static class Renderer
    {
        public static string RenderToPngDataUrl(int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            using var bmp = new Bitmap(width, height);
            using var g = Graphics.FromImage(bmp);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(ParseColor(settings.BgColor));

            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, (float)Math.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);

            using var gradient = BuildGradientBrush(settings, height);

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? bars[i] : 0f;
                float barHeight = Math.Max(2f, value * height);
                float x = i * barWidth + gap / 2f;
                float y = height - barHeight;
                float radius = Math.Min(settings.TopRadius, Math.Min(drawWidth / 2f, barHeight));

                using var path = CreateTopRoundedRect(x, y, drawWidth, barHeight, radius);
                g.FillPath(gradient, path);

                if (settings.PeakHold)
                {
                    float peakValue = i < peaks.Length ? peaks[i] : 0f;
                    float peakY = Math.Max(2f, height - peakValue * height);
                    using var peakBrush = new SolidBrush(InterpolateColor(settings.GradientStops, peakValue));
                    g.FillRectangle(peakBrush, x, peakY, drawWidth, settings.PeakThickness);
                }
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            string base64 = Convert.ToBase64String(ms.ToArray());
            return "data:image/png;base64," + base64;
        }

        private static LinearGradientBrush BuildGradientBrush(VisualizerSettings settings, int height)
        {
            var brush = new LinearGradientBrush(new Point(0, height), new Point(0, 0), Color.White, Color.White);
            var stops = settings.GradientStops.OrderBy(s => s.Pos).ToList();

            var blend = new ColorBlend
            {
                Positions = stops.Select(s => (float)s.Pos).ToArray(),
                Colors = stops.Select(s => ParseColor(s.Color)).ToArray()
            };

            brush.InterpolationColors = blend;
            return brush;
        }

        private static GraphicsPath CreateTopRoundedRect(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();

            if (radius <= 0.1f)
            {
                path.AddRectangle(new RectangleF(x, y, width, height));
                return path;
            }

            float d = radius * 2f;
            float bottom = y + height;

            path.StartFigure();
            path.AddLine(x, bottom, x, y + radius);
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddLine(x + width, y + radius, x + width, bottom);
            path.CloseFigure();

            return path;
        }

        private static Color ParseColor(string hex)
        {
            return ColorTranslator.FromHtml(string.IsNullOrWhiteSpace(hex) ? "#000000" : hex);
        }

        private static Color InterpolateColor(IReadOnlyList<GradientStop> stops, float t)
        {
            if (stops.Count == 0)
            {
                return Color.White;
            }

            var ordered = stops.OrderBy(s => s.Pos).ToList();

            if (t <= ordered[0].Pos)
            {
                return ParseColor(ordered[0].Color);
            }

            if (t >= ordered[^1].Pos)
            {
                return ParseColor(ordered[^1].Color);
            }

            for (int i = 0; i < ordered.Count - 1; i++)
            {
                var a = ordered[i];
                var b = ordered[i + 1];

                if (t >= a.Pos && t <= b.Pos)
                {
                    float local = (float)((t - a.Pos) / (b.Pos - a.Pos));
                    return Blend(ParseColor(a.Color), ParseColor(b.Color), local);
                }
            }

            return ParseColor(ordered[^1].Color);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
        }
    }

    internal static class SettingsNormalizer
    {
        public static void Normalize(VisualizerSettings settings)
        {
            settings.CaptureDeviceId = string.IsNullOrWhiteSpace(settings.CaptureDeviceId) ? "default" : settings.CaptureDeviceId;
            settings.FreqPreset = NormalizePreset(settings.FreqPreset);
            settings.FreqMin = Math.Clamp(settings.FreqMin, 20, 20000);
            settings.FreqMax = Math.Clamp(settings.FreqMax, 20, 20000);

            if (settings.FreqMax < settings.FreqMin)
            {
                (settings.FreqMin, settings.FreqMax) = (settings.FreqMax, settings.FreqMin);
            }

            settings.BarCount = Math.Clamp(settings.BarCount, 2, 32);
            settings.Sensitivity = Math.Clamp(settings.Sensitivity, 0.3, 12.0);
            settings.MotionSpeed = Math.Clamp(settings.MotionSpeed, 0.05, 1.0);
            settings.Smoothing = Math.Clamp(settings.Smoothing, 0.0, 0.95);
            settings.PeakFallSpeed = Math.Clamp(settings.PeakFallSpeed, 0.005, 0.25);
            settings.PeakThickness = Math.Clamp(settings.PeakThickness, 1, 8);
            settings.TopRadius = Math.Clamp(settings.TopRadius, 0, 12);
            settings.BgColor = NormalizeHex(settings.BgColor, "#07070f");

            settings.DeviceTrimDb = Math.Clamp(settings.DeviceTrimDb, -12.0, 12.0);
            settings.TiltAmountDbOct = Math.Clamp(settings.TiltAmountDbOct, 0.0, 6.0);
            settings.RangeCompensation = Math.Clamp(settings.RangeCompensation, 0.0, 2.5);
            settings.BassTaming = Math.Clamp(settings.BassTaming, 0.0, 2.0);
            settings.HighBoost = Math.Clamp(settings.HighBoost, 0.0, 2.0);

            settings.AutoGainMode = settings.AutoGainMode?.ToLowerInvariant() switch
            {
                "off" => "off",
                "soft" => "soft",
                "balanced" => "balanced",
                "strict" => "strict",
                _ => "balanced"
            };

            settings.SpectrumMode = settings.SpectrumMode?.ToLowerInvariant() switch
            {
                "raw" => "raw",
                "equalenergy" => "equalEnergy",
                "perceptual" => "perceptual",
                _ => "perceptual"
            };

            if (settings.GradientStops == null || settings.GradientStops.Count < 2)
            {
                settings.GradientStops = VisualizerSettings.CreateDefault().GradientStops;
            }

            settings.GradientStops = settings.GradientStops
                .Select(s => new GradientStop
                {
                    Pos = Math.Clamp(s.Pos, 0.0, 1.0),
                    Color = NormalizeHex(s.Color, "#ffffff")
                })
                .OrderBy(s => s.Pos)
                .Take(6)
                .ToList();

            if (settings.GradientStops.Count < 2)
            {
                settings.GradientStops = VisualizerSettings.CreateDefault().GradientStops;
            }

            settings.GradientStops[0].Pos = 0.0;
            settings.GradientStops[^1].Pos = 1.0;
        }

        private static string NormalizePreset(string? preset)
        {
            return preset?.ToLowerInvariant() switch
            {
                "low" => "low",
                "mid" => "mid",
                "high" => "high",
                "full" => "full",
                "custom" => "custom",
                _ => "full"
            };
        }

        private static string NormalizeHex(string? hex, string fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }

            string value = hex.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal))
            {
                value = "#" + value;
            }

            if (value.Length == 7 && int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return value.ToLowerInvariant();
            }

            return fallback;
        }
    }

    internal static class JsonHelper
    {
        public static string? GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
    }

    internal sealed class VisualizerSettings
    {
        [JsonPropertyName("captureDeviceId")]
        public string CaptureDeviceId { get; set; } = "default";

        [JsonPropertyName("freqPreset")]
        public string FreqPreset { get; set; } = "full";

        [JsonPropertyName("freqMin")]
        public int FreqMin { get; set; } = 20;

        [JsonPropertyName("freqMax")]
        public int FreqMax { get; set; } = 20000;

        [JsonPropertyName("barCount")]
        public int BarCount { get; set; } = 16;

        [JsonPropertyName("sensitivity")]
        public double Sensitivity { get; set; } = 1.8;

        [JsonPropertyName("motionSpeed")]
        public double MotionSpeed { get; set; } = 0.7;

        [JsonPropertyName("smoothing")]
        public double Smoothing { get; set; } = 0.22;

        [JsonPropertyName("peakHold")]
        public bool PeakHold { get; set; } = true;

        [JsonPropertyName("peakFallSpeed")]
        public double PeakFallSpeed { get; set; } = 0.08;

        [JsonPropertyName("peakThickness")]
        public int PeakThickness { get; set; } = 2;

        [JsonPropertyName("topRadius")]
        public int TopRadius { get; set; } = 2;

        [JsonPropertyName("bgColor")]
        public string BgColor { get; set; } = "#07070f";

        [JsonPropertyName("gradientStops")]
        public List<GradientStop> GradientStops { get; set; } = new()
        {
            new GradientStop { Pos = 0.0, Color = "#00e5ff" },
            new GradientStop { Pos = 0.5, Color = "#00ff88" },
            new GradientStop { Pos = 1.0, Color = "#ff1744" }
        };

        [JsonPropertyName("paused")]
        public bool Paused { get; set; } = false;

        [JsonPropertyName("deviceTrimDb")]
        public double DeviceTrimDb { get; set; } = 0.0;

        [JsonPropertyName("autoGainMode")]
        public string AutoGainMode { get; set; } = "balanced";

        [JsonPropertyName("spectrumMode")]
        public string SpectrumMode { get; set; } = "perceptual";

        [JsonPropertyName("tiltAmountDbOct")]
        public double TiltAmountDbOct { get; set; } = 4.5;

        [JsonPropertyName("rangeCompensation")]
        public double RangeCompensation { get; set; } = 1.4;

        [JsonPropertyName("bassTaming")]
        public double BassTaming { get; set; } = 1.0;

        [JsonPropertyName("highBoost")]
        public double HighBoost { get; set; } = 0.9;

        public static VisualizerSettings CreateDefault()
        {
            return new VisualizerSettings();
        }

        public VisualizerSettings Clone()
        {
            return new VisualizerSettings
            {
                CaptureDeviceId = CaptureDeviceId,
                FreqPreset = FreqPreset,
                FreqMin = FreqMin,
                FreqMax = FreqMax,
                BarCount = BarCount,
                Sensitivity = Sensitivity,
                MotionSpeed = MotionSpeed,
                Smoothing = Smoothing,
                PeakHold = PeakHold,
                PeakFallSpeed = PeakFallSpeed,
                PeakThickness = PeakThickness,
                TopRadius = TopRadius,
                BgColor = BgColor,
                GradientStops = GradientStops.Select(x => x.Clone()).ToList(),
                Paused = Paused,
                DeviceTrimDb = DeviceTrimDb,
                AutoGainMode = AutoGainMode,
                SpectrumMode = SpectrumMode,
                TiltAmountDbOct = TiltAmountDbOct,
                RangeCompensation = RangeCompensation,
                BassTaming = BassTaming,
                HighBoost = HighBoost
            };
        }
    }

    internal sealed class GradientStop
    {
        [JsonPropertyName("pos")]
        public double Pos { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; } = "#ffffff";

        public GradientStop Clone()
        {
            return new GradientStop
            {
                Pos = Pos,
                Color = Color
            };
        }
    }
}