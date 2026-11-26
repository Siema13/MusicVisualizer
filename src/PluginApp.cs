using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace MusicVisualizerLoopback
{
    internal sealed class PluginApp : IDisposable
    {
        private const string ActionUuid = "com.musicvisualizer.loopback.visualizer";
        private const int DefaultRenderSize = 96;
        private const int DefaultFftSize = 2048;
        private const double TargetKeyFps = 30.0;
        private const double MaxTotalFps = 120.0;
        private const int IdlePollIntervalMs = 50;
        private const int DiagnosticsIntervalMs = 30000;
        private const long DiagnosticsLogMaxBytes = 5 * 1024 * 1024;
        private const int ReconnectDelayMs = 500;
        private const int MaxReconnectDelayMs = 8000;
        private const int MaxRecentErrors = 5;
        private const int LongPressThresholdMs = 600;
        private static readonly byte[] SetImageSuffixUtf8 = Encoding.UTF8.GetBytes("\",\"target\":0,\"state\":0}}");

        private readonly int _port;
        private readonly string _pluginUuid;
        private readonly string _registerEvent;
        private readonly string? _infoJson;
        private readonly int _renderWidth;
        private readonly int _renderHeight;

        private ClientWebSocket _socket;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly CancellationTokenSource _cts;
        private readonly AutoResetEvent _renderWakeEvent;
        private readonly SemaphoreSlim _sendLock;
        private readonly SemaphoreSlim _audioRestartLock;
        private readonly SemaphoreSlim _socketLifecycleLock;
        private readonly ConcurrentDictionary<string, ActionRuntime> _actions;
        private volatile ActionRuntime[] _actionsCache = Array.Empty<ActionRuntime>();
        private readonly AudioDeviceWatcher _deviceWatcher;
        private readonly string _diagnosticsLogPath;
        private readonly object _diagnosticsFileLock = new object();
        private readonly object _recentErrorsLock = new object();
        private readonly LinkedList<string> _recentErrors = new LinkedList<string>();
        private byte[] _sendFrameBuffer = new byte[16384];

        private volatile AudioEngine? _audioEngine;
        private Task? _receiveTask;
        private Task? _renderTask;
        private Task? _diagnosticsTask;
        private volatile bool _disposed;
        private volatile bool _socketHealthy;
        private volatile string _timerMode = "Starting";

        private long _deviceEventCount;
        private long _audioRestartCount;
        private int _audioRestartQueueDepth;
        private long _pushImageTotalTicks;
        private long _pushImageSampleCount;
        private long _pushImageMaxTicks;
        private long _pushPayloadTotalBytes;
        private long _frameGapMaxTicks;
        private long _socketReconnectCount;
        private long _sendFailureCount;
        private int _renderRoundRobinIndex;
        private long _nextGlobalPushAllowedTicks;
        private readonly Stopwatch _uptimeWatch = new Stopwatch();

        private PluginApp(int port, string pluginUuid, string registerEvent, string? infoJson)
        {
            _port = port;
            _pluginUuid = pluginUuid;
            _registerEvent = registerEvent;
            _infoJson = infoJson;
            int resolvedSize = ResolveRenderSize(infoJson);
            _renderWidth = resolvedSize;
            _renderHeight = resolvedSize;

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _renderWakeEvent = new AutoResetEvent(false);
            _sendLock = new SemaphoreSlim(1, 1);
            _audioRestartLock = new SemaphoreSlim(1, 1);
            _socketLifecycleLock = new SemaphoreSlim(1, 1);
            _actions = new ConcurrentDictionary<string, ActionRuntime>(StringComparer.Ordinal);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            _diagnosticsLogPath = Path.Combine(AppContext.BaseDirectory, "diagnostics.log");

            _deviceWatcher = new AudioDeviceWatcher();
            _deviceWatcher.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;
            _deviceWatcher.DeviceAdded += OnAudioDeviceTopologyChanged;
            _deviceWatcher.DeviceRemoved += OnAudioDeviceRemoved;
            _deviceWatcher.DeviceStateChanged += OnAudioDeviceStateChanged;
        }

        internal static int ResolveRenderSize(string? infoJson)
        {
            if (string.IsNullOrWhiteSpace(infoJson))
            {
                return DefaultRenderSize;
            }

            try
            {
                using var doc = JsonDocument.Parse(infoJson);
                if (doc.RootElement.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var device in devices.EnumerateArray())
                    {
                        var name = JsonHelper.GetString(device, "name");
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        if (name.Contains("AKP153", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("APK153", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("293S", StringComparison.OrdinalIgnoreCase))
                        {
                            return 85;
                        }

                        if (name.Contains("293", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("N3", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("N4", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("M18", StringComparison.OrdinalIgnoreCase))
                        {
                            return 100;
                        }
                    }
                }
            }
            catch
            {
            }

            return DefaultRenderSize;
        }

        private void RebuildActionsCache()
        {
            _actionsCache = _actions.Values.ToArray();
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
            _uptimeWatch.Start();
            WriteDiagnosticsToFile("=== PluginApp started, pid=" + Environment.ProcessId + " renderSize=" + _renderWidth + "x" + _renderHeight + " targetKeyFps=" + TargetKeyFps.ToString("F0", CultureInfo.InvariantCulture) + " ===");
            await ConnectWithRetryAsync(_cts.Token);
            _receiveTask = ReceiveLoopWithReconnectAsync(_cts.Token);
            _renderTask = Task.Factory.StartNew(
                () => RenderLoopSync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _diagnosticsTask = DiagnosticsLoopAsync(_cts.Token);
            await Task.WhenAll(_receiveTask, _renderTask, _diagnosticsTask);
        }

        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
        {
            int delay = ReconnectDelayMs;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _socketLifecycleLock.WaitAsync(cancellationToken);
                    try
                    {
                        if (_socket.State != WebSocketState.None)
                        {
                            _socket.Dispose();
                            _socket = new ClientWebSocket();
                        }

                        var uri = new Uri($"ws://127.0.0.1:{_port}");
                        await _socket.ConnectAsync(uri, cancellationToken);

                        _socketHealthy = true;

                        bool registered = await SendRawAsync(new
                        {
                            @event = _registerEvent,
                            uuid = _pluginUuid
                        }, cancellationToken);

                        if (!registered)
                        {
                            _socketHealthy = false;
                            throw new InvalidOperationException("Failed to send registration event after connect.");
                        }

                        WriteDiagnosticsToFile("WebSocket connected/reconnected successfully.");
                        foreach (var action in _actions.Values)
                        {
                            action.QueueVisualInvalidation();
                        }
                        SignalRenderWake();
                        await BroadcastConnectionStatusAsync(cancellationToken);
                        return;
                    }
                    finally
                    {
                        _socketLifecycleLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _socketHealthy = false;
                    RecordRecentError("Connect failed: " + ex.Message);
                    WriteDiagnosticsToFile("Connect attempt failed: " + ex.Message + " - retrying in " + delay + "ms");

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }

                    delay = Math.Min(MaxReconnectDelayMs, delay * 2);
                }
            }
        }

        private async Task ReceiveLoopWithReconnectAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ReceiveLoopAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RecordRecentError("Receive loop crashed: " + ex.Message);
                    WriteDiagnosticsToFile("Receive loop crashed: " + ex.Message);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _socketHealthy = false;
                Interlocked.Increment(ref _socketReconnectCount);
                WriteDiagnosticsToFile("Connection lost, attempting reconnect. Total reconnects=" + Interlocked.Read(ref _socketReconnectCount));

                try
                {
                    await ConnectWithRetryAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
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
                        WriteDiagnosticsToFile("WebSocket received Close frame from host.");
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
                    RecordRecentError("JSON parse error: " + ex.Message);
                    WriteDiagnosticsToFile("JSON parse error: " + ex.Message);
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
                    await HandleWillDisappearAsync(root, cancellationToken);
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
                case "keyUp":
                    await HandleKeyUpAsync(root, cancellationToken);
                    break;
                case "systemDidWakeUp":
                    await RestartAudioForCurrentSettingsAsync(cancellationToken);
                    break;
            }
        }

        private void SignalRenderWake()
        {
            if (!_disposed)
            {
                try
                {
                    _renderWakeEvent.Set();
                }
                catch (ObjectDisposedException)
                {
                }
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
            runtime.QueueSettingsUpdate(settings);
            RebuildActionsCache();

            await EnsureAudioAsync(cancellationToken);
            SignalRenderWake();
        }

        private async Task HandleWillDisappearAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (!string.IsNullOrWhiteSpace(context))
            {
                _actions.TryRemove(context, out _);
                RebuildActionsCache();
            }

            if (_actions.IsEmpty)
            {
                await _audioRestartLock.WaitAsync(cancellationToken);
                try
                {
                    if (_actions.IsEmpty && _audioEngine != null)
                    {
                        var engine = _audioEngine;
                        _audioEngine = null;
                        engine.Dispose();
                    }
                }
                finally
                {
                    _audioRestartLock.Release();
                }
            }
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
            runtime.QueueSettingsUpdate(settings);
            RebuildActionsCache();
            SignalRenderWake();

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
            await SendConnectionStatusAsync(context, cancellationToken);
        }

        private async Task HandleSendToPluginAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
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

            bool added = false;
            var runtime = _actions.GetOrAdd(context, ctx =>
            {
                added = true;
                return new ActionRuntime(ctx, VisualizerSettings.CreateDefault());
            });
            if (added)
            {
                RebuildActionsCache();
            }

            switch (command)
            {
                case "piReady":
                    await SendCaptureDevicesAsync(context, cancellationToken);
                    await SendRuntimeStateAsync(context, cancellationToken);
                    await SendConnectionStatusAsync(context, cancellationToken);
                    break;

                case "getCaptureDevices":
                    await SendCaptureDevicesAsync(context, cancellationToken);
                    break;

                case "setPaused":
                    if (payload.TryGetProperty("paused", out var pausedProp) &&
                        (pausedProp.ValueKind == JsonValueKind.True || pausedProp.ValueKind == JsonValueKind.False))
                    {
                        var savedSettings = runtime.QueueManualPause(pausedProp.GetBoolean());
                        runtime.ManualPauseOverride = true;
                        SignalRenderWake();
                        await SaveSettingsAsync(runtime.Context, savedSettings, cancellationToken);
                        await SendRuntimeStateAsync(runtime.Context, cancellationToken);
                    }
                    break;

                case "previewSettings":
                    if (payload.TryGetProperty("settings", out var previewSettings))
                    {
                        var preview = DeserializeSettings(previewSettings, runtime.GetSettingsSnapshot().Clone());
                        SettingsNormalizer.Normalize(preview);
                        runtime.QueueSettingsUpdate(preview);
                        SignalRenderWake();
                        await EnsureAudioAsync(cancellationToken);
                    }
                    break;

                case "resetDefaults":
                    {
                        var defaults = VisualizerSettings.CreateDefault();
                        runtime.QueueSettingsUpdate(defaults);
                        SignalRenderWake();
                        await SaveSettingsAsync(runtime.Context, defaults.Clone(), cancellationToken);
                        await SendToPropertyInspectorAsync(runtime.Context, new
                        {
                            @event = "defaultsLoaded",
                            settings = defaults
                        }, cancellationToken);
                        await EnsureAudioAsync(cancellationToken);
                    }
                    break;

                case "resetCalibration":
                    break;

                case "requestExport":
                    await SendToPropertyInspectorAsync(runtime.Context, new
                    {
                        @event = "exportSettings",
                        settings = runtime.GetSettingsSnapshot().Clone()
                    }, cancellationToken);
                    break;

                case "importSettings":
                    if (payload.TryGetProperty("settings", out var importedSettings))
                    {
                        var imported = DeserializeSettings(importedSettings, VisualizerSettings.CreateDefault());
                        SettingsNormalizer.Normalize(imported);
                        runtime.QueueSettingsUpdate(imported);
                        SignalRenderWake();
                        await SaveSettingsAsync(runtime.Context, imported.Clone(), cancellationToken);
                        await SendToPropertyInspectorAsync(runtime.Context, new
                        {
                            @event = "defaultsLoaded",
                            settings = imported
                        }, cancellationToken);
                        await EnsureAudioAsync(cancellationToken);
                    }
                    break;

                case "syncStyleToAll":
                    if (payload.TryGetProperty("settings", out var styleSourceElem))
                    {
                        var src = DeserializeSettings(styleSourceElem, runtime.GetSettingsSnapshot().Clone());
                        SettingsNormalizer.Normalize(src);
                        int syncedCount = 0;

                        foreach (var targetRuntime in _actions.Values.ToArray())
                        {
                            var updated = targetRuntime.QueueLongPressTransform(dst =>
                            {
                                dst.RenderMode = src.RenderMode;
                                dst.Orientation = src.Orientation;
                                dst.AutoGainEnabled = src.AutoGainEnabled;
                                dst.BgColor = src.BgColor;
                                dst.GradientStops = src.GradientStops.Select(g => g.Clone()).ToList();
                                dst.TopRadius = src.TopRadius;
                                dst.PeakHold = src.PeakHold;
                                dst.PeakFallSpeed = src.PeakFallSpeed;
                                dst.PeakThickness = src.PeakThickness;
                                dst.DotSpacing = src.DotSpacing;
                                dst.ShowNumericMeter = src.ShowNumericMeter;
                                dst.BeatPulseEnabled = src.BeatPulseEnabled;
                                dst.IdleIconEnabled = src.IdleIconEnabled;
                                dst.AttackSpeed = src.AttackSpeed;
                                dst.ReleaseSpeed = src.ReleaseSpeed;
                                dst.Smoothing = src.Smoothing;
                                dst.LedSegmentMode = src.LedSegmentMode;
                                dst.LedSegmentCount = src.LedSegmentCount;
                                dst.LedSegmentSizePx = src.LedSegmentSizePx;
                                dst.LedSegmentGapPx = src.LedSegmentGapPx;
                                dst.LedLowColor = src.LedLowColor;
                                dst.LedMidColor = src.LedMidColor;
                                dst.LedHighColor = src.LedHighColor;
                                dst.LedLowThreshold = src.LedLowThreshold;
                                dst.LedHighThreshold = src.LedHighThreshold;
                            });
                            await SaveSettingsAsync(targetRuntime.Context, updated, cancellationToken);
                            syncedCount++;
                        }

                        SignalRenderWake();
                        await SendToPropertyInspectorAsync(runtime.Context, new
                        {
                            @event = "styleSynced",
                            count = syncedCount
                        }, cancellationToken);
                    }
                    break;
            }
        }

        private Task HandleKeyDownAsync(JsonElement root, CancellationToken cancellationToken)
        {
            var context = JsonHelper.GetString(root, "context");
            if (string.IsNullOrWhiteSpace(context))
            {
                return Task.CompletedTask;
            }

            if (_actions.TryGetValue(context, out var runtime))
            {
                runtime.KeyPressStopwatch.Restart();
            }

            return Task.CompletedTask;
        }

        private async Task HandleKeyUpAsync(JsonElement root, CancellationToken cancellationToken)
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

            long elapsedMs = runtime.KeyPressStopwatch.IsRunning || runtime.KeyPressStopwatch.ElapsedMilliseconds > 0
                ? runtime.KeyPressStopwatch.ElapsedMilliseconds
                : 0;
            runtime.KeyPressStopwatch.Reset();

            var updatedTargets = new List<(ActionRuntime Target, VisualizerSettings Saved)>();
            var currentSnap = runtime.GetSettingsSnapshot();

            if (elapsedMs >= LongPressThresholdMs)
            {
                string longAction = currentSnap.LongPressAction;
                bool syncMultiKey = currentSnap.SpanCount > 1;
                var targets = syncMultiKey
                    ? _actions.Values.Where(a => a.GetSettingsSnapshot().SpanCount > 1).ToArray()
                    : new[] { runtime };

                if (longAction == "cycleRenderMode")
                {
                    string nextMode = BuiltInPresets.NextRenderMode(currentSnap.RenderMode);
                    foreach (var target in targets)
                    {
                        var saved = target.QueueLongPressTransform(s => s.RenderMode = nextMode);
                        updatedTargets.Add((target, saved));
                    }
                }
                else if (longAction == "cycleGradientPreset")
                {
                    var nextStops = BuiltInPresets.NextGradientPreset(currentSnap.GradientStops);
                    foreach (var target in targets)
                    {
                        var saved = target.QueueLongPressTransform(s =>
                        {
                            s.GradientStops = nextStops.Select(g => g.Clone()).ToList();
                        });
                        updatedTargets.Add((target, saved));
                    }
                }
                else if (longAction == "togglePause")
                {
                    bool nextPaused = !currentSnap.Paused;
                    foreach (var target in targets)
                    {
                        var saved = target.QueueManualPause(nextPaused);
                        target.ManualPauseOverride = true;
                        updatedTargets.Add((target, saved));
                    }
                }
            }
            else
            {
                var saved = runtime.QueueManualPauseToggle();
                runtime.ManualPauseOverride = true;
                updatedTargets.Add((runtime, saved));
            }

            if (updatedTargets.Count > 0)
            {
                SignalRenderWake();
                foreach (var (target, saved) in updatedTargets)
                {
                    await SaveSettingsAsync(target.Context, saved, cancellationToken);
                }
                var primarySaved = updatedTargets.FirstOrDefault(x => x.Target.Context == runtime.Context).Saved ?? updatedTargets[0].Saved;
                await SendToPropertyInspectorAsync(runtime.Context, new
                {
                    @event = "defaultsLoaded",
                    settings = primarySaved
                }, cancellationToken);
                await SendRuntimeStateAsync(runtime.Context, cancellationToken);
            }
        }

        private void RenderLoopSync(CancellationToken cancellationToken)
        {
            using var waitTimer = HighResWaitTimer.TryCreate();
            _timerMode = waitTimer != null ? "HiRes" : "Fallback";
            WriteDiagnosticsToFile("RenderLoop started with timerMode=" + _timerMode);

            long minSlotTicks = (long)Math.Round(Stopwatch.Frequency / MaxTotalFps);
            double keyPeriodTicks = Stopwatch.Frequency / TargetKeyFps;
            long idleWaitTicks = IdlePollIntervalMs * Stopwatch.Frequency / 1000;
            long nextSlotTicks = 0;
            WaitHandle[] fallbackWaitHandles = { cancellationToken.WaitHandle, _renderWakeEvent };

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!_socketHealthy)
                        {
                            nextSlotTicks = 0;
                            WaitForTicks(waitTimer, idleWaitTicks, cancellationToken, _renderWakeEvent, fallbackWaitHandles);
                            continue;
                        }

                        var actions = _actionsCache;
                        int actionCount = actions.Length;
                        if (actionCount == 0)
                        {
                            nextSlotTicks = 0;
                            WaitForTicks(waitTimer, idleWaitTicks, cancellationToken, _renderWakeEvent, fallbackWaitHandles);
                            continue;
                        }

                        long slotPeriodTicks = Math.Max(minSlotTicks, (long)Math.Round(keyPeriodTicks / actionCount));
                        long nowTicks = Stopwatch.GetTimestamp();

                        if (nextSlotTicks == 0)
                        {
                            nextSlotTicks = nowTicks;
                        }

                        SpectrumSnapshot? snapshot = _audioEngine?.GetSnapshot();
                        bool hasSignal = snapshot != null && snapshot.HasSignal;

                        int idx = _renderRoundRobinIndex % actionCount;
                        _renderRoundRobinIndex = (idx + 1) % actionCount;
                        var runtime = actions[idx];

                        runtime.ApplyPendingUpdatesOnRenderThread();
                        ApplyAutoPause(runtime, hasSignal);
                        UpdateAnimation(runtime, snapshot, nowTicks);

                        if (nowTicks >= _nextGlobalPushAllowedTicks)
                        {
                            PushImageAsync(runtime, cancellationToken).GetAwaiter().GetResult();
                        }

                        long afterTicks = Stopwatch.GetTimestamp();

                        if (!hasSignal && AreAllActionsSettled(actions))
                        {
                            nextSlotTicks = 0;
                            WaitForTicks(waitTimer, idleWaitTicks, cancellationToken, _renderWakeEvent, fallbackWaitHandles);
                            continue;
                        }

                        nextSlotTicks += slotPeriodTicks;
                        if (afterTicks - nextSlotTicks > slotPeriodTicks)
                        {
                            nextSlotTicks = afterTicks;
                        }

                        long remainingTicks = nextSlotTicks - afterTicks;
                        if (remainingTicks > 0)
                        {
                            WaitForTicks(waitTimer, remainingTicks, cancellationToken, null, null);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        RecordRecentError("Render loop error: " + ex.Message);
                        WriteDiagnosticsToFile("Render loop error: " + ex);
                        nextSlotTicks = 0;
                        WaitForTicks(waitTimer, 100 * Stopwatch.Frequency / 1000, cancellationToken, null, null);
                    }
                }
            }
            finally
            {
                Renderer.DisposeThreadScratch();
            }
        }

        private static bool AreAllActionsSettled(ActionRuntime[] actions)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i];
                if (!action.HasSentInitialFrame || action.IsVisualActive || action.HasPendingUpdates)
                {
                    return false;
                }
            }

            return true;
        }

        private static void WaitForTicks(
            HighResWaitTimer? waitTimer,
            long ticks,
            CancellationToken cancellationToken,
            WaitHandle? wakeHandle,
            WaitHandle[]? fallbackWaitHandles)
        {
            if (ticks <= 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (waitTimer != null)
            {
                long delay100ns = (long)Math.Round(ticks * 10_000_000.0 / Stopwatch.Frequency);
                if (delay100ns > 0)
                {
                    waitTimer.Wait(delay100ns, cancellationToken, wakeHandle);
                }
                return;
            }

            int delayMs = Math.Max(1, (int)Math.Round(ticks * 1000.0 / Stopwatch.Frequency));
            if (fallbackWaitHandles != null)
            {
                WaitHandle.WaitAny(fallbackWaitHandles, delayMs);
            }
            else
            {
                cancellationToken.WaitHandle.WaitOne(delayMs);
            }
        }

        private void ApplyAutoPause(ActionRuntime runtime, bool hasSignal)
        {
            var settings = runtime.Settings;
            if (!settings.AutoPauseEnabled)
            {
                runtime.SilenceStopwatch.Reset();
                return;
            }

            if (hasSignal)
            {
                runtime.SilenceStopwatch.Reset();
                if (runtime.AutoPaused)
                {
                    runtime.AutoPaused = false;
                    settings.Paused = false;
                }
                return;
            }

            if (!runtime.SilenceStopwatch.IsRunning)
            {
                runtime.SilenceStopwatch.Restart();
            }

            int timeoutSec = Math.Clamp(settings.AutoPauseTimeoutSec, 5, 120);
            if (runtime.SilenceStopwatch.Elapsed.TotalSeconds >= timeoutSec && !settings.Paused)
            {
                settings.Paused = true;
                runtime.AutoPaused = true;
            }
        }

        private async Task DiagnosticsLoopAsync(CancellationToken cancellationToken)
        {
            var intervalWatch = Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DiagnosticsIntervalMs, cancellationToken);
                    double intervalSec = Math.Max(0.1, intervalWatch.Elapsed.TotalSeconds);
                    intervalWatch.Restart();

                    long sampleCount = Interlocked.Exchange(ref _pushImageSampleCount, 0);
                    long totalTicks = Interlocked.Exchange(ref _pushImageTotalTicks, 0);
                    long maxTicks = Interlocked.Exchange(ref _pushImageMaxTicks, 0);
                    long payloadBytesTotal = Interlocked.Exchange(ref _pushPayloadTotalBytes, 0);
                    long frameGapMaxTicks = Interlocked.Exchange(ref _frameGapMaxTicks, 0);

                    double avgMs = sampleCount > 0
                        ? (totalTicks / (double)sampleCount) * 1000.0 / Stopwatch.Frequency
                        : 0.0;
                    double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
                    double frameGapMaxMs = frameGapMaxTicks * 1000.0 / Stopwatch.Frequency;
                    long avgPayloadBytes = sampleCount > 0 ? payloadBytesTotal / sampleCount : 0;

                    long deviceEvents = Interlocked.Read(ref _deviceEventCount);
                    long audioRestarts = Interlocked.Read(ref _audioRestartCount);
                    int actionCount = _actions.Count;
                    double fpsPerKey = actionCount > 0 ? (sampleCount / intervalSec) / actionCount : 0.0;
                    long workingSetMb;
                    using (var currentProcess = Process.GetCurrentProcess())
                    {
                        workingSetMb = currentProcess.WorkingSet64 / (1024 * 1024);
                    }
                    int gen0 = GC.CollectionCount(0);
                    int gen1 = GC.CollectionCount(1);
                    int gen2 = GC.CollectionCount(2);
                    long socketReconnects = Interlocked.Read(ref _socketReconnectCount);
                    long sendFailures = Interlocked.Read(ref _sendFailureCount);

                    string message = string.Format(
                        CultureInfo.InvariantCulture,
                        "[{0:yyyy-MM-dd HH:mm:ss}] uptimeSec={1} actions={2} fpsPerKey={3:F1} frameGapMaxMs={4:F1} avgPayloadBytes={5} renderSize={6}x{7} timerMode={8} pushImgAvgMs={9:F2} pushImgMaxMs={10:F2} pushImgSamples={11} deviceEventsTotal={12} audioRestartsTotal={13} workingSetMb={14} gen0={15} gen1={16} gen2={17} socketHealthy={18} socketReconnects={19} sendFailures={20}",
                        DateTime.Now,
                        (long)_uptimeWatch.Elapsed.TotalSeconds,
                        actionCount,
                        fpsPerKey,
                        frameGapMaxMs,
                        avgPayloadBytes,
                        _renderWidth,
                        _renderHeight,
                        _timerMode,
                        avgMs,
                        maxMs,
                        sampleCount,
                        deviceEvents,
                        audioRestarts,
                        workingSetMb,
                        gen0,
                        gen1,
                        gen2,
                        _socketHealthy,
                        socketReconnects,
                        sendFailures);

                    WriteDiagnosticsToFile(message);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    WriteDiagnosticsToFile("Diagnostics loop error: " + ex);
                }
            }
        }

        private void WriteDiagnosticsToFile(string line)
        {
            try
            {
                lock (_diagnosticsFileLock)
                {
                    var info = new FileInfo(_diagnosticsLogPath);
                    if (info.Exists && info.Length > DiagnosticsLogMaxBytes)
                    {
                        File.Delete(_diagnosticsLogPath);
                    }

                    File.AppendAllText(_diagnosticsLogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private void RecordRecentError(string message)
        {
            lock (_recentErrorsLock)
            {
                _recentErrors.AddLast(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " - " + message);
                while (_recentErrors.Count > MaxRecentErrors)
                {
                    _recentErrors.RemoveFirst();
                }
            }
        }

        private List<string> GetRecentErrorsSnapshot()
        {
            lock (_recentErrorsLock)
            {
                return _recentErrors.ToList();
            }
        }

        /* Czysty, natychmiastowy silnik balistyczny (Zero-lag analog ballistics) */
        private void UpdateAnimation(ActionRuntime runtime, SpectrumSnapshot? snapshot, long nowTicks)
        {
            double dtSeconds = runtime.LastAnimTicks > 0
                ? Math.Clamp((nowTicks - runtime.LastAnimTicks) / (double)Stopwatch.Frequency, 0.005, 0.100)
                : (1.0 / TargetKeyFps);
            runtime.LastAnimTicks = nowTicks;

            var settings = runtime.Settings;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            runtime.EnsureArrays(barCount);

            float stepRatio = (float)(dtSeconds / 0.033333);
            float peakFallStep = (float)settings.PeakFallSpeed * stepRatio;

            if (settings.Paused || snapshot is null || snapshot.Magnitudes.Length == 0)
            {
                float pauseDecay = 0.10f * stepRatio;
                bool active = false;
                float sum = 0f;
                for (int i = 0; i < barCount; i++)
                {
                    float b = Math.Max(0f, runtime.Bars[i] - pauseDecay);
                    if (b < 0.0005f) b = 0f;
                    runtime.Bars[i] = b;
                    sum += b;

                    float p = settings.PeakHold
                        ? Math.Max(0f, runtime.Peaks[i] - peakFallStep)
                        : 0f;
                    if (p < 0.0005f) p = 0f;
                    runtime.Peaks[i] = p;

                    if (b > 0f || p > 0f)
                    {
                        active = true;
                    }
                }

                runtime.StereoLeft = Math.Max(0f, runtime.StereoLeft - pauseDecay);
                runtime.StereoRight = Math.Max(0f, runtime.StereoRight - pauseDecay);
                runtime.StereoPeakLeft = settings.PeakHold ? Math.Max(0f, runtime.StereoPeakLeft - peakFallStep) : 0f;
                runtime.StereoPeakRight = settings.PeakHold ? Math.Max(0f, runtime.StereoPeakRight - peakFallStep) : 0f;
                runtime.BeatPulseLevel = Math.Max(0f, runtime.BeatPulseLevel - 0.15f * stepRatio);
                if (runtime.StereoLeft > 0.0005f || runtime.StereoRight > 0.0005f || runtime.StereoPeakLeft > 0.0005f || runtime.StereoPeakRight > 0.0005f || runtime.BeatPulseLevel > 0.0005f)
                {
                    active = true;
                }
                else
                {
                    runtime.StereoLeft = 0f;
                    runtime.StereoRight = 0f;
                    runtime.StereoPeakLeft = 0f;
                    runtime.StereoPeakRight = 0f;
                    runtime.BeatPulseLevel = 0f;
                }

                Array.Clear(runtime.Waveform, 0, runtime.Waveform.Length);
                runtime.IsVisualActive = active;
                runtime.IsIdleState = !active;
                runtime.LastOverallLevel = barCount > 0 ? sum / barCount : 0f;
                return;
            }

            SpectrumMapper.MapInto(snapshot, runtime);

            float baseRise = (float)Math.Clamp(0.40 + settings.AttackSpeed * 0.60, 0.20, 1.0);
            float baseFall = (float)Math.Clamp(0.04 + settings.ReleaseSpeed * 0.60, 0.01, 0.9);
            float rise = baseRise >= 0.999f ? 1f : 1f - MathF.Pow(1f - baseRise, stepRatio);
            float fall = 1f - MathF.Pow(1f - baseFall, stepRatio);

            bool anyActive = false;
            float levelSum = 0f;
            for (int i = 0; i < barCount; i++)
            {
                float raw = runtime.MappedScratch[i];
                float prev = runtime.Bars[i];

                float smooth = raw > prev
                    ? prev + (raw - prev) * rise
                    : prev + (raw - prev) * fall;

                smooth = Math.Clamp(smooth, 0f, 1f);
                if (raw <= 0f && smooth < 0.0005f)
                {
                    smooth = 0f;
                }
                runtime.Bars[i] = smooth;
                levelSum += smooth;

                float peak = settings.PeakHold
                    ? Math.Max(smooth, Math.Max(0f, runtime.Peaks[i] - peakFallStep))
                    : 0f;
                if (peak < 0.0005f)
                {
                    peak = 0f;
                }
                runtime.Peaks[i] = peak;

                if (smooth > 0f || peak > 0f)
                {
                    anyActive = true;
                }
            }

            // Update stereo L/R levels (time-domain RMS is full-band, ~16 dB hotter than single FFT bins)
            double minDb = settings.MinDb;
            double invDbRange = 1.0 / Math.Max(6.0, -minDb);
            double timeDomainGainDb = settings.GainDb - 16.0;
            float rawLeft = MapLinearLevelToNorm(snapshot.RmsLeft, timeDomainGainDb, settings.NoiseGateDb, minDb, invDbRange, settings.Gamma);
            float rawRight = MapLinearLevelToNorm(snapshot.RmsRight, timeDomainGainDb, settings.NoiseGateDb, minDb, invDbRange, settings.Gamma);
            runtime.StereoLeft = ApplyBallistic(runtime.StereoLeft, rawLeft, rise, fall);
            runtime.StereoRight = ApplyBallistic(runtime.StereoRight, rawRight, rise, fall);
            runtime.StereoPeakLeft = settings.PeakHold ? Math.Max(runtime.StereoLeft, Math.Max(0f, runtime.StereoPeakLeft - peakFallStep)) : 0f;
            runtime.StereoPeakRight = settings.PeakHold ? Math.Max(runtime.StereoRight, Math.Max(0f, runtime.StereoPeakRight - peakFallStep)) : 0f;
            if (runtime.StereoPeakLeft < 0.0005f) runtime.StereoPeakLeft = 0f;
            if (runtime.StereoPeakRight < 0.0005f) runtime.StereoPeakRight = 0f;

            // Update waveform slice for oscilloscope mode (calibrated to time-domain amplitude + gentle temporal smoothing)
            if (settings.RenderMode == "waveform")
            {
                float gainLin = (float)Math.Clamp(Math.Pow(10.0, timeDomainGainDb / 20.0) * 1.4, 0.5, 4.0);
                var srcWave = snapshot.Waveform;
                int wLen = Math.Min(runtime.Waveform.Length, srcWave.Length);
                for (int i = 0; i < wLen; i++)
                {
                    float target = Math.Clamp(srcWave[i] * gainLin, -1f, 1f);
                    float blended = runtime.Waveform[i] + (target - runtime.Waveform[i]) * 0.65f;
                    if (MathF.Abs(target) < 0.002f && MathF.Abs(blended) < 0.003f)
                    {
                        blended = 0f;
                    }
                    runtime.Waveform[i] = blended;
                    if (MathF.Abs(blended) > 0.005f)
                    {
                        anyActive = true;
                    }
                }
            }

            // Update Beat Pulse (20-120 Hz bass transient detector for rhythmic pulsing background)
            if (settings.BeatPulseEnabled)
            {
                float rawBass = MapLinearLevelToNorm(snapshot.BassEnergy, settings.GainDb, settings.NoiseGateDb, minDb, invDbRange, settings.Gamma);
                float baseTrack = 1f - MathF.Pow(1f - 0.08f, stepRatio);
                runtime.BeatBassBaseline += (rawBass - runtime.BeatBassBaseline) * baseTrack;
                float transient = Math.Clamp((rawBass - runtime.BeatBassBaseline) * 2.6f + rawBass * 0.28f, 0f, 1f);
                float pulseRise = 1f - MathF.Pow(1f - 0.78f, stepRatio);
                float pulseFall = 1f - MathF.Pow(1f - 0.18f, stepRatio);
                float nextPulse = transient > runtime.BeatPulseLevel
                    ? runtime.BeatPulseLevel + (transient - runtime.BeatPulseLevel) * pulseRise
                    : runtime.BeatPulseLevel + (transient - runtime.BeatPulseLevel) * pulseFall;
                if (rawBass <= 0f && nextPulse < 0.005f) nextPulse = 0f;
                runtime.BeatPulseLevel = Math.Clamp(nextPulse, 0f, 1f);
                if (runtime.BeatPulseLevel > 0.05f)
                {
                    anyActive = true;
                }
            }
            else
            {
                runtime.BeatPulseLevel = 0f;
                runtime.BeatBassBaseline = 0f;
            }

            if (settings.RenderMode == "waterfall" && (anyActive || snapshot.HasSignal))
            {
                runtime.WaterfallTick++;
                anyActive = true;
            }

            if (settings.RenderMode == "stereo" && (runtime.StereoLeft > 0f || runtime.StereoRight > 0f || runtime.StereoPeakLeft > 0f || runtime.StereoPeakRight > 0f))
            {
                anyActive = true;
            }

            runtime.IsVisualActive = anyActive;
            runtime.IsIdleState = !anyActive && !snapshot.HasSignal;
            runtime.LastOverallLevel = settings.RenderMode == "stereo"
                ? (runtime.StereoLeft + runtime.StereoRight) * 0.5f
                : (barCount > 0 ? levelSum / barCount : 0f);
        }

        private static float MapLinearLevelToNorm(float linear, double gainDb, double noiseGateDb, double minDb, double invDbRange, double gamma)
        {
            double safe = linear > 1e-6f ? linear : 1e-6;
            double db = 20.0 * Math.Log10(safe) + gainDb;
            if (db < noiseGateDb)
            {
                return 0f;
            }

            double norm = Math.Clamp((db - minDb) * invDbRange, 0.0, 1.0);
            if (Math.Abs(gamma - 1.0) > 0.01)
            {
                norm = Math.Pow(norm, 1.0 / Math.Max(0.1, gamma));
            }
            return (float)Math.Clamp(norm, 0.0, 1.0);
        }

        private static float ApplyBallistic(float prev, float raw, float rise, float fall)
        {
            float smooth = raw > prev
                ? prev + (raw - prev) * rise
                : prev + (raw - prev) * fall;
            smooth = Math.Clamp(smooth, 0f, 1f);
            return (raw <= 0f && smooth < 0.0005f) ? 0f : smooth;
        }

        private async Task<bool> PushImageAsync(ActionRuntime runtime, CancellationToken cancellationToken)
        {
            int visualSignature = runtime.ComputeVisualSignature(_renderWidth, _renderHeight);
            if (runtime.HasSentInitialFrame && visualSignature == runtime.LastVisualSignature)
            {
                return false;
            }

            long startTicks = Stopwatch.GetTimestamp();

            ArraySegment<byte> pngBytes = Renderer.RenderToPngBytes(_renderWidth, _renderHeight, runtime);
            if (pngBytes.Count == 0)
            {
                return false;
            }

            bool sent = await SendImageFrameAsync(runtime, pngBytes, cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return false;
            }

            long finishTicks = Stopwatch.GetTimestamp();
            long prevPushTicks = runtime.LastPushTicks;
            if (runtime.HasSentInitialFrame && prevPushTicks > 0)
            {
                long gapTicks = finishTicks - prevPushTicks;
                if (gapTicks > 0 && gapTicks < Stopwatch.Frequency / 2)
                {
                    UpdateMax(ref _frameGapMaxTicks, gapTicks);
                }
            }

            runtime.LastVisualSignature = visualSignature;
            runtime.HasSentInitialFrame = true;
            runtime.LastPushTicks = finishTicks;

            long elapsedTicks = finishTicks - startTicks;
            double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs > 15.0)
            {
                long backoffMs = (long)Math.Min(150.0, elapsedMs * 2.0);
                _nextGlobalPushAllowedTicks = finishTicks + (backoffMs * Stopwatch.Frequency / 1000);
            }

            Interlocked.Add(ref _pushImageTotalTicks, elapsedTicks);
            Interlocked.Increment(ref _pushImageSampleCount);
            Interlocked.Add(ref _pushPayloadTotalBytes, pngBytes.Count);
            UpdateMax(ref _pushImageMaxTicks, elapsedTicks);

            return true;
        }

        private static void UpdateMax(ref long location, long value)
        {
            long currentMax;
            do
            {
                currentMax = Interlocked.Read(ref location);
                if (value <= currentMax)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref location, value, currentMax) != currentMax);
        }

        private async Task EnsureAudioAsync(CancellationToken cancellationToken)
        {
            await _audioRestartLock.WaitAsync(cancellationToken);
            try
            {
                if (_actions.IsEmpty)
                {
                    return;
                }

                var referenceSettings = _actions.Values.FirstOrDefault()?.GetSettingsSnapshot() ?? VisualizerSettings.CreateDefault();
                var engine = _audioEngine;

                if (engine == null)
                {
                    engine = new AudioEngine(DefaultFftSize);
                    _audioEngine = engine;
                    await engine.StartAsync(referenceSettings.CaptureDeviceId, cancellationToken);
                    return;
                }

                if (!engine.Matches(referenceSettings.CaptureDeviceId))
                {
                    await engine.StartAsync(referenceSettings.CaptureDeviceId, cancellationToken);
                }
            }
            finally
            {
                _audioRestartLock.Release();
            }
        }

        private async Task RestartAudioForCurrentSettingsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _audioRestartQueueDepth);
            try
            {
                await _audioRestartLock.WaitAsync(cancellationToken);
            }
            catch
            {
                Interlocked.Decrement(ref _audioRestartQueueDepth);
                throw;
            }

            Interlocked.Decrement(ref _audioRestartQueueDepth);
            try
            {
                if (_actions.IsEmpty)
                {
                    return;
                }

                Interlocked.Increment(ref _audioRestartCount);
                WriteDiagnosticsToFile("Audio restart triggered, totalRestarts=" + Interlocked.Read(ref _audioRestartCount));

                var referenceSettings = _actions.Values.FirstOrDefault()?.GetSettingsSnapshot() ?? VisualizerSettings.CreateDefault();
                var engine = _audioEngine;

                if (engine == null)
                {
                    engine = new AudioEngine(DefaultFftSize);
                    _audioEngine = engine;
                }

                await engine.StartAsync(referenceSettings.CaptureDeviceId, cancellationToken);

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
            Interlocked.Increment(ref _deviceEventCount);
            if (_disposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool anyDefault = _actions.Values.Any(x =>
                        string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

                    if (!anyDefault) return;

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
            Interlocked.Increment(ref _deviceEventCount);
            if (_disposed) return;

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
            Interlocked.Increment(ref _deviceEventCount);
            if (_disposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool affected = _actions.Values.Any(x =>
                        string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, removedDeviceId, StringComparison.OrdinalIgnoreCase));

                    if (affected)
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
            Interlocked.Increment(ref _deviceEventCount);
            if (_disposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool affected = _actions.Values.Any(x =>
                        string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

                    if (!affected) return;

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
                ? runtime.GetSettingsSnapshot().CaptureDeviceId
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
            var engine = _audioEngine;
            if (_actions.TryGetValue(context, out var runtime))
            {
                double overallDb = ComputeOverallDb(runtime);

                await SendToPropertyInspectorAsync(context, new
                {
                    @event = "runtimeState",
                    isCapturing = engine?.IsCapturing ?? false,
                    isRendering = true,
                    sampleRate = engine?.SampleRate ?? 0,
                    fftSize = engine?.FftSize ?? DefaultFftSize,
                    lastError = engine?.LastError,
                    overallDb,
                    autoPaused = runtime.AutoPaused,
                    recentErrors = GetRecentErrorsSnapshot()
                }, cancellationToken);

                return;
            }

            await SendToPropertyInspectorAsync(context, new
            {
                @event = "runtimeState",
                isCapturing = engine?.IsCapturing ?? false,
                isRendering = true,
                sampleRate = engine?.SampleRate ?? 0,
                fftSize = engine?.FftSize ?? DefaultFftSize,
                lastError = engine?.LastError,
                recentErrors = GetRecentErrorsSnapshot()
            }, cancellationToken);
        }

        private static double ComputeOverallDb(ActionRuntime runtime)
        {
            double mean = runtime.LastOverallLevel;
            double safe = Math.Max(mean, 0.0001);
            double db = 20.0 * Math.Log10(safe);
            return Math.Round(Math.Clamp(db, -60.0, 0.0), 1);
        }

        private async Task SendConnectionStatusAsync(string context, CancellationToken cancellationToken)
        {
            await SendToPropertyInspectorAsync(context, new
            {
                @event = "connectionStatus",
                healthy = _socketHealthy,
                reconnectCount = Interlocked.Read(ref _socketReconnectCount),
                sendFailures = Interlocked.Read(ref _sendFailureCount)
            }, cancellationToken);
        }

        private async Task BroadcastConnectionStatusAsync(CancellationToken cancellationToken)
        {
            foreach (var context in _actions.Keys.ToArray())
            {
                try
                {
                    await SendConnectionStatusAsync(context, cancellationToken);
                }
                catch
                {
                }
            }
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

        private async Task<bool> SendImageFrameAsync(ActionRuntime runtime, ArraySegment<byte> pngBytes, CancellationToken cancellationToken)
        {
            if (!_socketHealthy || pngBytes.Array == null || pngBytes.Count == 0)
            {
                return false;
            }

            byte[] prefix = runtime.SetImagePrefixUtf8;
            int maxBase64Len = Base64.GetMaxEncodedToUtf8Length(pngBytes.Count);
            int neededLen = prefix.Length + maxBase64Len + SetImageSuffixUtf8.Length;

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    _socketHealthy = false;
                    return false;
                }

                if (_sendFrameBuffer.Length < neededLen)
                {
                    _sendFrameBuffer = new byte[Math.Max(neededLen, _sendFrameBuffer.Length * 2)];
                }

                Buffer.BlockCopy(prefix, 0, _sendFrameBuffer, 0, prefix.Length);
                var status = Base64.EncodeToUtf8(
                    pngBytes.AsSpan(),
                    _sendFrameBuffer.AsSpan(prefix.Length, maxBase64Len),
                    out _,
                    out int bytesWritten);
                if (status != System.Buffers.OperationStatus.Done)
                {
                    return false;
                }

                int suffixOffset = prefix.Length + bytesWritten;
                Buffer.BlockCopy(SetImageSuffixUtf8, 0, _sendFrameBuffer, suffixOffset, SetImageSuffixUtf8.Length);
                int totalLen = suffixOffset + SetImageSuffixUtf8.Length;

                await _socket.SendAsync(new ArraySegment<byte>(_sendFrameBuffer, 0, totalLen), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebSocketException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send image failed (WebSocketException): " + ex.Message);
                WriteDiagnosticsToFile("SendImageFrameAsync WebSocketException (socket marked unhealthy): " + ex.Message);
                return false;
            }
            catch (ObjectDisposedException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send image failed (ObjectDisposedException): " + ex.Message);
                WriteDiagnosticsToFile("SendImageFrameAsync ObjectDisposedException (socket marked unhealthy): " + ex.Message);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send image failed (InvalidOperationException): " + ex.Message);
                WriteDiagnosticsToFile("SendImageFrameAsync InvalidOperationException (socket marked unhealthy): " + ex.Message);
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<bool> SendRawAsync(object payload, CancellationToken cancellationToken)
        {
            if (!_socketHealthy)
            {
                return false;
            }

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    _socketHealthy = false;
                    return false;
                }

                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebSocketException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send failed (WebSocketException): " + ex.Message);
                WriteDiagnosticsToFile("SendRawAsync WebSocketException (socket marked unhealthy): " + ex.Message);
                return false;
            }
            catch (ObjectDisposedException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send failed (ObjectDisposedException): " + ex.Message);
                WriteDiagnosticsToFile("SendRawAsync ObjectDisposedException (socket marked unhealthy): " + ex.Message);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _socketHealthy = false;
                Interlocked.Increment(ref _sendFailureCount);
                RecordRecentError("Send failed (InvalidOperationException): " + ex.Message);
                WriteDiagnosticsToFile("SendRawAsync InvalidOperationException (socket marked unhealthy): " + ex.Message);
                return false;
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

            WriteDiagnosticsToFile("=== PluginApp disposing ===");

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
            _socketLifecycleLock.Dispose();
            _renderWakeEvent.Dispose();
            _cts.Dispose();
        }
    }
}
