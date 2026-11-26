using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
        private const int DefaultRenderSize = 96;
        private const int RenderTargetBoth = 0;
        private const int RenderState = 0;
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

        private static int ResolveRenderSize(string? infoJson)
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

                        // AJAZZ AKP153E / Mirabox StreamDock 293S / 293SV3 native LCD key size is 85x85.
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

            var savedSettings = runtime.QueueManualPauseToggle();
            runtime.ManualPauseOverride = true;
            SignalRenderWake();
            await SaveSettingsAsync(runtime.Context, savedSettings, cancellationToken);
            await SendRuntimeStateAsync(runtime.Context, cancellationToken);
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

                        // When completely silent and every button has finished decaying to its settled frame,
                        // slow polling to 20 Hz (waking immediately on any settings/appear/key event).
                        if (!hasSignal && AreAllActionsSettled(actions))
                        {
                            nextSlotTicks = 0;
                            WaitForTicks(waitTimer, idleWaitTicks, cancellationToken, _renderWakeEvent, fallbackWaitHandles);
                            continue;
                        }

                        nextSlotTicks += slotPeriodTicks;
                        if (afterTicks - nextSlotTicks > slotPeriodTicks)
                        {
                            // Resync cleanly after sleep/wake or scheduling stall without multi-frame bursts.
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
                    long workingSetMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
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
                runtime.IsVisualActive = active;
                runtime.LastOverallLevel = barCount > 0 ? sum / barCount : 0f;
                return;
            }

            SpectrumMapper.MapInto(snapshot, runtime);

            /* Natychmiastowy skok na nowe uderzenie (Attack) i płynne opadanie (Release) */
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
            runtime.IsVisualActive = anyActive;
            runtime.LastOverallLevel = barCount > 0 ? levelSum / barCount : 0f;
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
                    await engine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);
                    return;
                }

                if (!engine.Matches(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing))
                {
                    await engine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);
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

                await engine.StartAsync(referenceSettings.CaptureDeviceId, referenceSettings.Smoothing, cancellationToken);

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

            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool anyDefault = _actions.Values.Any(x =>
                    string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

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
            Interlocked.Increment(ref _deviceEventCount);

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
            Interlocked.Increment(ref _deviceEventCount);

            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool selectedRemoved = _actions.Values.Any(x =>
                    string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, removedDeviceId, StringComparison.OrdinalIgnoreCase));

                    bool anyDefault = _actions.Values.Any(x =>
                    string.Equals(x.GetSettingsSnapshot().CaptureDeviceId, "default", StringComparison.OrdinalIgnoreCase));

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
            Interlocked.Increment(ref _deviceEventCount);

            if (_disposed)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool tracked = _actions.Values.Any(x =>
                    {
                        var devId = x.GetSettingsSnapshot().CaptureDeviceId;
                        return string.Equals(devId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(devId, "default", StringComparison.OrdinalIgnoreCase);
                    });

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

    internal sealed class HighResWaitTimer : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint WaitObject0 = 0x00000000;

        private IntPtr _handle;
        private bool _disposed;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr lpTimerAttributes,
            string? lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimerEx(
            IntPtr hTimer,
            ref long lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            IntPtr wakeContext,
            uint tolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(
            uint nCount,
            IntPtr[] lpHandles,
            [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
            uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly IntPtr[] _waitHandles = new IntPtr[3];

        private HighResWaitTimer(IntPtr handle)
        {
            _handle = handle;
            _waitHandles[0] = handle;
        }

        public static HighResWaitTimer? TryCreate()
        {
            try
            {
                IntPtr handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (handle != IntPtr.Zero)
                {
                    return new HighResWaitTimer(handle);
                }
            }
            catch
            {
            }

            return null;
        }

        public void Wait(long delay100ns, CancellationToken cancellationToken)
        {
            Wait(delay100ns, cancellationToken, null);
        }

        public void Wait(long delay100ns, CancellationToken cancellationToken, WaitHandle? wakeHandle)
        {
            if (_disposed || _handle == IntPtr.Zero || delay100ns <= 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            long dueTime = -delay100ns;
            if (!SetWaitableTimerEx(_handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                int fallbackMs = Math.Max(1, (int)(delay100ns / 10_000));
                cancellationToken.WaitHandle.WaitOne(fallbackMs);
                return;
            }

            var cancelWaitHandle = cancellationToken.WaitHandle.SafeWaitHandle;
            var wakeSafeHandle = wakeHandle?.SafeWaitHandle;
            bool addedCancelRef = false;
            bool addedWakeRef = false;
            try
            {
                cancelWaitHandle.DangerousAddRef(ref addedCancelRef);
                _waitHandles[1] = cancelWaitHandle.DangerousGetHandle();
                uint count = 2;
                if (wakeSafeHandle != null && !wakeSafeHandle.IsClosed && !wakeSafeHandle.IsInvalid)
                {
                    wakeSafeHandle.DangerousAddRef(ref addedWakeRef);
                    _waitHandles[2] = wakeSafeHandle.DangerousGetHandle();
                    count = 3;
                }

                uint res = WaitForMultipleObjects(count, _waitHandles, false, uint.MaxValue);
                if (res != WaitObject0 && cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            finally
            {
                if (addedWakeRef && wakeSafeHandle != null)
                {
                    wakeSafeHandle.DangerousRelease();
                }

                if (addedCancelRef)
                {
                    cancelWaitHandle.DangerousRelease();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    internal struct BandMapping
    {
        public int StartBin;
        public int EndBin;
        public double TiltDb;
    }

    internal sealed class ActionRuntime
    {
        private readonly object _settingsLock = new object();
        private VisualizerSettings? _pendingSettings;
        private bool _pendingVisualInvalidation;
        private bool _clearAutoPausedPending;
        private volatile VisualizerSettings _latestSettingsSnapshot;

        public ActionRuntime(string context, VisualizerSettings settings)
        {
            Context = context;
            Settings = settings;
            _latestSettingsSnapshot = settings;
            int count = Math.Clamp(settings.BarCount, 2, 32);
            Bars = new float[count];
            Peaks = new float[count];
            MappedScratch = new float[count];

            SilenceStopwatch = new Stopwatch();
            KeyPressStopwatch = new Stopwatch();

            string escapedContext = JsonSerializer.Serialize(context);
            SetImagePrefixUtf8 = Encoding.UTF8.GetBytes("{\"event\":\"setImage\",\"context\":" + escapedContext + ",\"payload\":{\"image\":\"data:image/png;base64,");
        }

        public string Context { get; }
        public byte[] SetImagePrefixUtf8 { get; }
        public VisualizerSettings Settings { get; set; }
        public float[] Bars { get; private set; }
        public float[] Peaks { get; private set; }
        public float[] MappedScratch { get; private set; }

        public Stopwatch SilenceStopwatch { get; }
        public volatile bool AutoPaused;
        public volatile bool ManualPauseOverride;
        public Stopwatch KeyPressStopwatch { get; }
        public int LastVisualSignature { get; set; }
        public bool HasSentInitialFrame { get; set; }
        public bool IsVisualActive { get; set; }
        public volatile float LastOverallLevel;
        public long LastPushTicks { get; set; }
        public long LastAnimTicks { get; set; }
        public int SettingsVersion { get; private set; }

        public bool HasPendingUpdates
        {
            get
            {
                lock (_settingsLock)
                {
                    return _pendingSettings != null || _pendingVisualInvalidation || _clearAutoPausedPending;
                }
            }
        }

        // Cached frequency band mapping (rebuilt only when settings or audio format change)
        public BandMapping[]? CachedBands;
        public int CachedBandsSampleRate;
        public int CachedBandsBinCount;
        public int CachedBandsBarCount;
        public int CachedBandsFreqMin;
        public int CachedBandsFreqMax;
        public double CachedBandsSlope;

        // Cached pre-built PNG header (Signature + IHDR + PLTE) for fast 8-bit indexed PNG encoding
        public byte[]? CachedIndexedPngHeader;
        public int CachedPngHeaderWidth;
        public int CachedPngHeaderHeight;
        public string? CachedPngHeaderMode;
        public int CachedPngHeaderSettingsVersion;

        public VisualizerSettings GetSettingsSnapshot()
        {
            return _latestSettingsSnapshot;
        }

        public void QueueSettingsUpdate(VisualizerSettings settings)
        {
            lock (_settingsLock)
            {
                _pendingSettings = settings;
                _latestSettingsSnapshot = settings;
                _pendingVisualInvalidation = true;
            }
        }

        public VisualizerSettings QueueManualPause(bool paused)
        {
            lock (_settingsLock)
            {
                var next = (_pendingSettings ?? _latestSettingsSnapshot).Clone();
                next.Paused = paused;
                _pendingSettings = next;
                _latestSettingsSnapshot = next;
                _clearAutoPausedPending = true;
                _pendingVisualInvalidation = true;
                return next.Clone();
            }
        }

        public VisualizerSettings QueueManualPauseToggle()
        {
            lock (_settingsLock)
            {
                var next = (_pendingSettings ?? _latestSettingsSnapshot).Clone();
                next.Paused = !next.Paused;
                _pendingSettings = next;
                _latestSettingsSnapshot = next;
                _clearAutoPausedPending = true;
                _pendingVisualInvalidation = true;
                return next.Clone();
            }
        }

        public void QueueVisualInvalidation()
        {
            lock (_settingsLock)
            {
                _pendingVisualInvalidation = true;
            }
        }

        public void ApplyPendingUpdatesOnRenderThread()
        {
            VisualizerSettings? nextSettings;
            bool invalidate;
            bool clearAutoPaused;

            lock (_settingsLock)
            {
                nextSettings = _pendingSettings;
                invalidate = _pendingVisualInvalidation;
                clearAutoPaused = _clearAutoPausedPending;
                _pendingSettings = null;
                _pendingVisualInvalidation = false;
                _clearAutoPausedPending = false;
            }

            if (clearAutoPaused)
            {
                AutoPaused = false;
                SilenceStopwatch.Reset();
            }

            if (nextSettings != null)
            {
                Settings = nextSettings;
                EnsureArrays(Math.Clamp(nextSettings.BarCount, 2, 32));
                SettingsVersion++;
                InvalidateVisualCache();
            }
            else if (invalidate)
            {
                InvalidateVisualCache();
            }
        }

        public void InvalidateVisualCache()
        {
            HasSentInitialFrame = false;
            LastVisualSignature = 0;
            CachedIndexedPngHeader = null;
            CachedBands = null;
        }

        public int ComputeVisualSignature(int renderWidth, int renderHeight)
        {
            var settings = Settings;
            var hash = new HashCode();
            hash.Add(SettingsVersion);
            hash.Add(settings.RenderMode);
            hash.Add(settings.BarCount);
            hash.Add(settings.BgColor);
            hash.Add(settings.PeakHold);
            hash.Add(settings.PeakThickness);
            hash.Add(settings.TopRadius);
            hash.Add(settings.DotSpacing);
            hash.Add(settings.ShowNumericMeter);

            if (settings.RenderMode == "ledSegments")
            {
                hash.Add(settings.LedSegmentMode);
                hash.Add(settings.LedSegmentCount);
                hash.Add(settings.LedSegmentSizePx);
                hash.Add(settings.LedSegmentGapPx);
                hash.Add(settings.LedLowColor);
                hash.Add(settings.LedMidColor);
                hash.Add(settings.LedHighColor);
                hash.Add(settings.LedLowThreshold);
                hash.Add(settings.LedHighThreshold);
            }
            else
            {
                var stops = settings.GradientStops;
                for (int s = 0; s < stops.Count; s++)
                {
                    hash.Add(stops[s].Pos);
                    hash.Add(stops[s].Color);
                }
            }

            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            int actualBars = Math.Min(barCount, Bars.Length);
            float scale = renderHeight / 144f;
            double sum = 0.0;

            if (settings.RenderMode == "vuMeter")
            {
                for (int i = 0; i < actualBars; i++)
                {
                    sum += Math.Clamp(Bars[i], 0f, 1f);
                }
                double avg = actualBars > 0 ? sum / actualBars : 0.0;
                hash.Add((int)Math.Round(avg * 280.0));
            }
            else if (settings.RenderMode == "ledSegments")
            {
                int gapPx = Math.Max(1, (int)MathF.Round(Math.Clamp(settings.LedSegmentGapPx, 1, 4) * scale));
                int totalSegments;
                if (settings.LedSegmentMode == "fixedSize")
                {
                    int segmentSizePx = Math.Max(2, (int)MathF.Round(Math.Clamp(settings.LedSegmentSizePx, 4, 16) * scale));
                    totalSegments = Math.Max(1, (int)(renderHeight / (float)(segmentSizePx + gapPx)));
                }
                else
                {
                    totalSegments = Math.Clamp(settings.LedSegmentCount, 4, 24);
                }

                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    hash.Add((int)Math.Round(b * totalSegments));
                }
            }
            else if (settings.RenderMode == "dots")
            {
                float barWidth = renderWidth / (float)barCount;
                float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
                float drawWidth = Math.Max(2f, barWidth - gap);
                float spacing = Math.Max(1f, Math.Clamp(settings.DotSpacing, 2, 10) * scale);
                float dotSize = Math.Max(2f, drawWidth);
                int maxDots = Math.Max(1, (int)(renderHeight / (dotSize + spacing)));

                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    hash.Add((int)Math.Round(b * maxDots));
                }
            }
            else if (settings.RenderMode == "mirror")
            {
                float centerY = renderHeight * 0.5f;
                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    float halfH = Math.Max(1f, b * centerY);
                    int y0 = Math.Clamp((int)MathF.Round(centerY - halfH), 0, renderHeight - 1);
                    int y1 = Math.Clamp((int)MathF.Round(centerY + halfH), y0 + 1, renderHeight);
                    hash.Add(y0);
                    hash.Add(y1);
                }
            }
            else
            {
                int minPeakY = Math.Max(1, (int)MathF.Round(2f * scale));
                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    int barH = Math.Clamp((int)MathF.Round(Math.Max(2f * scale, b * renderHeight)), 1, renderHeight);
                    hash.Add(barH);

                    if (settings.PeakHold && i < Peaks.Length)
                    {
                        float p = Math.Clamp(Peaks[i], 0f, 1f);
                        int peakY = Math.Clamp((int)MathF.Round(renderHeight - p * renderHeight), minPeakY, renderHeight - 1);
                        int peakColorIdx = Math.Clamp((int)MathF.Round((1f - p) * (renderHeight - 1)), 0, renderHeight - 1);
                        hash.Add(peakY);
                        hash.Add(peakColorIdx);
                    }
                }
            }

            if (settings.ShowNumericMeter)
            {
                double mean = actualBars > 0 ? sum / actualBars : 0.0;
                double safe = Math.Max(mean, 0.0001);
                int dbRounded = (int)Math.Round(Math.Clamp(20.0 * Math.Log10(safe), -60.0, 0.0));
                hash.Add(dbRounded);
            }

            return hash.ToHashCode();
        }

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

            if (MappedScratch.Length != barCount)
            {
                MappedScratch = new float[barCount];
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
        private readonly float[] _hannWindow;
        private float[] _fftInput = Array.Empty<float>();
        private float[] _fftWindow = Array.Empty<float>();
        private Complex[] _fftBuffer = Array.Empty<Complex>();
        private float[] _magnitudes = Array.Empty<float>();
        private float[] _snapshotBuffer = Array.Empty<float>();
        private int _fftWriteIndex;
        private float[] _smoothedMagnitudes = Array.Empty<float>();
        private SpectrumSnapshot _latestSnapshot;
        private volatile bool _hasNewAudio;
        private long _lastFftTicks;
        private long _lastAudioDataTicks;
        private static readonly long MinFftIntervalTicks = Stopwatch.Frequency / 60; // At most 60 FFTs/sec (16.7 ms)
        private static readonly long SilenceStaleTicks = Stopwatch.Frequency / 5; // 200 ms without WASAPI packets -> silence

        public AudioEngine(int fftSize)
        {
            _fftSize = fftSize;
            _hannWindow = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                _hannWindow[i] = (float)FastFourierTransform.HannWindow(i, fftSize);
            }
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
                        CurrentSmoothing = smoothing;
                        LastError = null;

                        var newFftInput = new float[_fftSize];
                        var newFftWindow = new float[_fftSize];
                        var newFftBuffer = new Complex[_fftSize];
                        var newMagnitudes = new float[_fftSize / 2];
                        var newSnapshotBuffer = new float[_fftSize / 2];
                        var newSmoothedMagnitudes = new float[_fftSize / 2];
                        var newSnapshot = new SpectrumSnapshot(newSnapshotBuffer, SampleRate);

                        lock (_sampleSync)
                        {
                            _fftInput = newFftInput;
                            _fftWindow = newFftWindow;
                            _fftBuffer = newFftBuffer;
                            _magnitudes = newMagnitudes;
                            _snapshotBuffer = newSnapshotBuffer;
                            _smoothedMagnitudes = newSmoothedMagnitudes;
                            _fftWriteIndex = 0;
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

                _fftWriteIndex = AudioMath.WriteMonoSamplesToRing(
                    e.Buffer,
                    e.BytesRecorded,
                    capture.WaveFormat,
                    frames,
                    _fftInput,
                    _fftWriteIndex);
                Volatile.Write(ref _lastAudioDataTicks, Stopwatch.GetTimestamp());
                _hasNewAudio = true;
            }
        }

        private bool BuildSpectrumFromLatestWindow()
        {
            var capture = _capture;
            int binCount = _magnitudes.Length;
            if (capture == null ||
                _fftInput.Length != _fftSize ||
                _fftWindow.Length != _fftSize ||
                _fftBuffer.Length != _fftSize ||
                binCount == 0 ||
                _smoothedMagnitudes.Length != binCount)
            {
                return false;
            }

            int tail = _fftSize - _fftWriteIndex;
            Array.Copy(_fftInput, _fftWriteIndex, _fftWindow, 0, tail);
            Array.Copy(_fftInput, 0, _fftWindow, tail, _fftWriteIndex);

            for (int i = 0; i < _fftSize; i++)
            {
                _fftBuffer[i].X = _fftWindow[i] * _hannWindow[i];
                _fftBuffer[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, (int)Math.Log2(_fftSize), _fftBuffer);

            float smoothing = (float)Math.Clamp(CurrentSmoothing, 0.0, 0.85);
            float oneMinusSmoothing = 1f - smoothing;
            bool hasSignal = false;

            for (int i = 0; i < binCount; i++)
            {
                double re = _fftBuffer[i].X;
                double im = _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(re * re + im * im);
                float smoothed = _smoothedMagnitudes[i] + (mag - _smoothedMagnitudes[i]) * oneMinusSmoothing;
                if (mag <= 1e-6f && smoothed < 1e-5f)
                {
                    smoothed = 0f;
                }
                _smoothedMagnitudes[i] = smoothed;
                _magnitudes[i] = smoothed;
                if (smoothed > 1e-5f)
                {
                    hasSignal = true;
                }
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
                            bool hasSignal = BuildSpectrumFromLatestWindow();
                            Array.Copy(_magnitudes, _snapshotBuffer, _magnitudes.Length);
                            _latestSnapshot.HasSignal = hasSignal;
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
                            Array.Clear(_smoothedMagnitudes, 0, _smoothedMagnitudes.Length);
                            Array.Clear(_magnitudes, 0, _magnitudes.Length);
                            Array.Clear(_snapshotBuffer, 0, _snapshotBuffer.Length);
                            _latestSnapshot.HasSignal = false;
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
                _smoothedMagnitudes = Array.Empty<float>();
                _fftWriteIndex = 0;
                _hasNewAudio = false;
                Volatile.Write(ref _lastAudioDataTicks, 0);
                _latestSnapshot = new SpectrumSnapshot(Array.Empty<float>(), SampleRate);
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
        public bool HasSignal { get; set; }

        public SpectrumSnapshot Clone()
        {
            return new SpectrumSnapshot((float[])Magnitudes.Clone(), SampleRate)
            {
                HasSignal = HasSignal
            };
        }
    }

    internal static class SpectrumMapper
    {
        public static void MapInto(SpectrumSnapshot snapshot, ActionRuntime runtime)
        {
            var settings = runtime.Settings;
            var output = runtime.MappedScratch;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            int binCount = snapshot.Magnitudes.Length;
            if (binCount == 0)
            {
                Array.Clear(output, 0, Math.Min(barCount, output.Length));
                return;
            }

            var bands = runtime.CachedBands;
            if (bands == null ||
                bands.Length != barCount ||
                runtime.CachedBandsSampleRate != snapshot.SampleRate ||
                runtime.CachedBandsBinCount != binCount ||
                runtime.CachedBandsBarCount != barCount ||
                runtime.CachedBandsFreqMin != settings.FreqMin ||
                runtime.CachedBandsFreqMax != settings.FreqMax ||
                Math.Abs(runtime.CachedBandsSlope - settings.SlopeDbPerOct) > 1e-6)
            {
                bands = BuildBandMappings(snapshot.SampleRate, binCount, barCount, settings.FreqMin, settings.FreqMax, settings.SlopeDbPerOct);
                runtime.CachedBands = bands;
                runtime.CachedBandsSampleRate = snapshot.SampleRate;
                runtime.CachedBandsBinCount = binCount;
                runtime.CachedBandsBarCount = barCount;
                runtime.CachedBandsFreqMin = settings.FreqMin;
                runtime.CachedBandsFreqMax = settings.FreqMax;
                runtime.CachedBandsSlope = settings.SlopeDbPerOct;
            }

            double minDb = settings.MinDb;
            double maxDb = 0.0;
            double dbRange = Math.Max(6.0, maxDb - minDb);
            double invDbRange = 1.0 / dbRange;
            double gainDb = settings.GainDb;
            double noiseGateDb = settings.NoiseGateDb;
            bool applyGamma = Math.Abs(settings.Gamma - 1.0) > 0.01;
            double invGamma = applyGamma ? 1.0 / Math.Max(0.1, settings.Gamma) : 1.0;
            float[] magnitudes = snapshot.Magnitudes;

            for (int i = 0; i < barCount; i++)
            {
                ref readonly var band = ref bands[i];
                int startBin = band.StartBin;
                int endBin = band.EndBin;

                double sum = 0.0;
                double peak = 0.0;
                int count = endBin - startBin + 1;

                for (int b = startBin; b <= endBin; b++)
                {
                    double value = magnitudes[b];
                    sum += value * value;
                    if (value > peak)
                    {
                        peak = value;
                    }
                }

                double rms = count > 0 ? Math.Sqrt(sum / count) : 0.0;
                double energy = rms * 0.70 + peak * 0.30;

                double safeEnergy = energy > 1e-6 ? energy : 1e-6;
                double db = 20.0 * Math.Log10(safeEnergy) + gainDb + band.TiltDb;

                if (db < noiseGateDb)
                {
                    output[i] = 0f;
                    continue;
                }

                double norm = (db - minDb) * invDbRange;
                norm = Math.Clamp(norm, 0.0, 1.0);

                if (applyGamma)
                {
                    norm = Math.Pow(norm, invGamma);
                }

                output[i] = (float)Math.Clamp(norm, 0.0, 1.0);
            }
        }

        private static BandMapping[] BuildBandMappings(int sampleRate, int binCount, int barCount, int freqMin, int freqMax, double slopeDbPerOct)
        {
            double nyquist = sampleRate / 2.0;
            int minHz = Math.Clamp(freqMin, 20, Math.Max(20, (int)nyquist));
            int maxHz = Math.Clamp(freqMax, 20, Math.Max(20, (int)nyquist));

            if (maxHz <= minHz)
            {
                maxHz = Math.Min(Math.Max(21, (int)nyquist), minHz + 1);
            }

            int rangeStartBin = Math.Max(1, (int)Math.Floor((minHz / nyquist) * binCount));
            int rangeEndBin = Math.Min(binCount - 1, (int)Math.Ceiling((maxHz / nyquist) * binCount));

            if (rangeEndBin <= rangeStartBin)
            {
                rangeEndBin = Math.Min(binCount - 1, rangeStartBin + 1);
            }

            double logMin = Math.Log10(minHz);
            double logSpan = Math.Log10(maxHz) - logMin;
            var bands = new BandMapping[barCount];

            for (int i = 0; i < barCount; i++)
            {
                double startRatio = i / (double)barCount;
                double endRatio = (i + 1) / (double)barCount;

                double bandStartHz = Math.Pow(10.0, logMin + startRatio * logSpan);
                double bandEndHz = Math.Pow(10.0, logMin + endRatio * logSpan);
                double centerHz = Math.Sqrt(bandStartHz * bandEndHz);

                int startBin = Math.Max(rangeStartBin, (int)Math.Floor((bandStartHz / nyquist) * binCount));
                int endBin = Math.Min(rangeEndBin, (int)Math.Ceiling((bandEndHz / nyquist) * binCount));

                if (endBin <= startBin)
                {
                    endBin = Math.Min(rangeEndBin, startBin + 1);
                }

                double octavesFrom100Hz = Math.Log2(Math.Max(20.0, centerHz) / 100.0);
                bands[i] = new BandMapping
                {
                    StartBin = startBin,
                    EndBin = endBin,
                    TiltDb = octavesFrom100Hz * slopeDbPerOct
                };
            }

            return bands;
        }
    }

    internal static class AudioMath
    {
        public static int WriteMonoSamplesToRing(byte[] source, int length, WaveFormat format, int frameCount, float[] ringBuffer, int writeIndex)
        {
            int ringLen = ringBuffer.Length;
            int channels = format.Channels;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                float invChannels = 1f / channels;
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    int baseOffset = i * channels * 4;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = baseOffset + ch * 4;
                        if (offset + 4 > length)
                        {
                            break;
                        }

                        mix += BitConverter.ToSingle(source, offset);
                    }

                    ringBuffer[writeIndex] = mix * invChannels;
                    writeIndex++;
                    if (writeIndex >= ringLen)
                    {
                        writeIndex = 0;
                    }
                }

                return writeIndex;
            }

            if (format.BitsPerSample == 16)
            {
                float invScale = 1f / (32768f * channels);
                for (int i = 0; i < frameCount; i++)
                {
                    float mix = 0f;
                    int baseOffset = i * channels * 2;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = baseOffset + ch * 2;
                        if (offset + 2 > length)
                        {
                            break;
                        }

                        mix += BitConverter.ToInt16(source, offset);
                    }

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
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] ChunkTypeIhdr = { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        private static readonly byte[] ChunkTypePlte = { (byte)'P', (byte)'L', (byte)'T', (byte)'E' };
        private static readonly byte[] ChunkTypeIdat = { (byte)'I', (byte)'D', (byte)'A', (byte)'T' };
        private static readonly byte[] PngIendChunk = { 0x00, 0x00, 0x00, 0x00, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 };
        private static readonly uint[] CrcTable = BuildCrc32Table();

        private sealed class RenderScratch : IDisposable
        {
            public readonly Bitmap Bitmap;
            public readonly Graphics Graphics;
            public readonly MemoryStream Stream;
            public readonly MemoryStream ZlibStreamBuffer;
            public readonly byte[] IndexedScanlines;
            public readonly SolidBrush ReusableBrush;
            public readonly Pen ReusablePen;
            public readonly Pen BlackBorderPen;
            public readonly Pen VuArcPen;
            public readonly SolidBrush OverlayBgBrush;
            public readonly SolidBrush OverlayTextBrush;
            public readonly Font OverlayFont;
            public readonly int Width;
            public readonly int Height;

            public RenderScratch(int width, int height)
            {
                Width = width;
                Height = height;
                float scale = height / 144f;
                Bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                Graphics = Graphics.FromImage(Bitmap);
                Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Graphics.InterpolationMode = InterpolationMode.Low;
                Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                Stream = new MemoryStream(4096);
                ZlibStreamBuffer = new MemoryStream(4096);
                IndexedScanlines = new byte[(width + 1) * height];
                ReusableBrush = new SolidBrush(Color.White);
                ReusablePen = new Pen(Color.White, Math.Max(1.5f, 3.5f * scale));
                BlackBorderPen = new Pen(Color.Black, 1f);
                VuArcPen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1.5f, 3f * scale));
                OverlayBgBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                OverlayTextBrush = new SolidBrush(Color.White);
                OverlayFont = new Font("Segoe UI", Math.Max(7f, 11f * scale), System.Drawing.FontStyle.Bold);
            }

            public void Dispose()
            {
                OverlayFont.Dispose();
                OverlayTextBrush.Dispose();
                OverlayBgBrush.Dispose();
                VuArcPen.Dispose();
                BlackBorderPen.Dispose();
                ReusablePen.Dispose();
                ReusableBrush.Dispose();
                Graphics.Dispose();
                Bitmap.Dispose();
                Stream.Dispose();
                ZlibStreamBuffer.Dispose();
            }
        }

        private static readonly ThreadLocal<RenderScratch?> _scratch = new ThreadLocal<RenderScratch?>(false);

        public static void DisposeThreadScratch()
        {
            if (_scratch.IsValueCreated)
            {
                _scratch.Value?.Dispose();
                _scratch.Value = null;
            }
        }

        private static RenderScratch GetScratch(int width, int height)
        {
            var s = _scratch.Value;
            if (s == null || s.Width != width || s.Height != height)
            {
                s?.Dispose();
                s = new RenderScratch(width, height);
                _scratch.Value = s;
            }
            return s;
        }

        public static ArraySegment<byte> RenderToPngBytes(int width, int height, ActionRuntime runtime)
        {
            var settings = runtime.Settings;
            var scratch = GetScratch(width, height);

            // Fast zero-allocation 8-bit indexed PNG path for bars, mirror, and ledSegments (without numeric overlay)
            if (!settings.ShowNumericMeter &&
                height <= 255 &&
                (settings.RenderMode == "bars" || settings.RenderMode == "mirror" || settings.RenderMode == "ledSegments"))
            {
                byte[] header = GetOrBuildIndexedPngHeader(runtime, width, height);
                byte[] raw = scratch.IndexedScanlines;
                Array.Clear(raw, 0, raw.Length);

                if (settings.RenderMode == "ledSegments")
                {
                    RenderIndexedLedSegments(raw, width, height, settings, runtime.Bars);
                }
                else if (settings.RenderMode == "mirror")
                {
                    RenderIndexedMirrorBars(raw, width, height, settings, runtime.Bars);
                }
                else
                {
                    RenderIndexedBars(raw, width, height, settings, runtime.Bars, runtime.Peaks);
                }

                return EncodeIndexedPng(header, raw, scratch.ZlibStreamBuffer, scratch.Stream);
            }

            return RenderGdiToPngBytes(scratch, width, height, settings, runtime.Bars, runtime.Peaks);
        }

        private static byte[] GetOrBuildIndexedPngHeader(ActionRuntime runtime, int width, int height)
        {
            var settings = runtime.Settings;
            byte[]? cached = runtime.CachedIndexedPngHeader;
            if (cached != null &&
                runtime.CachedPngHeaderWidth == width &&
                runtime.CachedPngHeaderHeight == height &&
                runtime.CachedPngHeaderSettingsVersion == runtime.SettingsVersion &&
                string.Equals(runtime.CachedPngHeaderMode, settings.RenderMode, StringComparison.Ordinal))
            {
                return cached;
            }

            Color bg = ParseColor(settings.BgColor);
            byte[] paletteRgb;

            if (settings.RenderMode == "ledSegments")
            {
                Color low = ParseColor(settings.LedLowColor);
                Color mid = ParseColor(settings.LedMidColor);
                Color high = ParseColor(settings.LedHighColor);
                paletteRgb = new byte[5 * 3];
                WriteRgb(paletteRgb, 0, bg);
                WriteRgb(paletteRgb, 1, low);
                WriteRgb(paletteRgb, 2, mid);
                WriteRgb(paletteRgb, 3, high);
                WriteRgb(paletteRgb, 4, Color.Black);
            }
            else
            {
                int entries = 1 + height;
                paletteRgb = new byte[entries * 3];
                WriteRgb(paletteRgb, 0, bg);
                float denom = Math.Max(1, height - 1);
                for (int y = 0; y < height; y++)
                {
                    float t = 1.0f - (y / denom);
                    Color rowColor = InterpolateColor(settings.GradientStops, t);
                    WriteRgb(paletteRgb, 1 + y, rowColor);
                }
            }

            // Signature (8) + IHDR chunk (4+4+13+4 = 25) + PLTE chunk (4+4+paletteRgb.Length+4)
            byte[] header = new byte[8 + 25 + 12 + paletteRgb.Length];
            int pos = 0;

            Buffer.BlockCopy(PngSignature, 0, header, pos, 8);
            pos += 8;

            // IHDR
            Span<byte> ihdrData = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdrData.Slice(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdrData.Slice(4, 4), height);
            ihdrData[8] = 8;  // Bit depth: 8
            ihdrData[9] = 3;  // Color type: 3 (Indexed)
            ihdrData[10] = 0; // Compression: Deflate
            ihdrData[11] = 0; // Filter: Adaptive
            ihdrData[12] = 0; // Interlace: None
            pos = WritePngChunk(header, pos, ChunkTypeIhdr, ihdrData);

            // PLTE
            WritePngChunk(header, pos, ChunkTypePlte, paletteRgb);

            runtime.CachedIndexedPngHeader = header;
            runtime.CachedPngHeaderWidth = width;
            runtime.CachedPngHeaderHeight = height;
            runtime.CachedPngHeaderMode = settings.RenderMode;
            runtime.CachedPngHeaderSettingsVersion = runtime.SettingsVersion;
            return header;
        }

        private static void WriteRgb(byte[] paletteRgb, int index, Color c)
        {
            int o = index * 3;
            paletteRgb[o] = c.R;
            paletteRgb[o + 1] = c.G;
            paletteRgb[o + 2] = c.B;
        }

        private static int WritePngChunk(byte[] dst, int pos, byte[] chunkType, ReadOnlySpan<byte> data)
        {
            BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(pos, 4), data.Length);
            pos += 4;
            Buffer.BlockCopy(chunkType, 0, dst, pos, 4);
            pos += 4;
            data.CopyTo(dst.AsSpan(pos, data.Length));
            pos += data.Length;

            uint crc = UpdateCrc(0xFFFFFFFFu, chunkType);
            crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(pos, 4), crc);
            pos += 4;
            return pos;
        }

        private static ArraySegment<byte> EncodeIndexedPng(byte[] header, byte[] rawScanlines, MemoryStream zlibBuffer, MemoryStream pngStream)
        {
            zlibBuffer.SetLength(0);
            using (var z = new ZLibStream(zlibBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(rawScanlines, 0, rawScanlines.Length);
            }

            int idatLen = (int)zlibBuffer.Length;
            byte[] idatRaw = zlibBuffer.GetBuffer();
            ReadOnlySpan<byte> idatSpan = idatRaw.AsSpan(0, idatLen);

            pngStream.SetLength(0);
            pngStream.Write(header, 0, header.Length);

            Span<byte> lenAndType = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(lenAndType.Slice(0, 4), idatLen);
            ChunkTypeIdat.CopyTo(lenAndType.Slice(4, 4));
            pngStream.Write(lenAndType);
            pngStream.Write(idatSpan);

            uint crc = UpdateCrc(0xFFFFFFFFu, ChunkTypeIdat);
            crc = UpdateCrc(crc, idatSpan) ^ 0xFFFFFFFFu;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            pngStream.Write(crcBytes);

            pngStream.Write(PngIendChunk, 0, PngIendChunk.Length);
            return new ArraySegment<byte>(pngStream.GetBuffer(), 0, (int)pngStream.Length);
        }

        private static void RenderIndexedBars(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float scale = height / 144f;
            int scaledTopRadius = settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0;
            int scaledPeakThickness = Math.Max(1, (int)MathF.Round(settings.PeakThickness * scale));

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;
                int barH = Math.Clamp((int)MathF.Round(Math.Max(2f * scale, value * height)), 1, height);
                int y0 = height - barH;
                int radius = Math.Min(scaledTopRadius, Math.Min(barW / 2, barH));

                for (int y = y0; y < height; y++)
                {
                    int rx0 = x0;
                    int rx1 = x1;
                    int dy = y - y0;
                    if (radius > 0 && dy < radius)
                    {
                        float cy = radius - dy - 0.5f;
                        float dx = MathF.Sqrt(Math.Max(0f, radius * radius - cy * cy));
                        int inset = Math.Max(0, (int)MathF.Floor(radius - dx));
                        rx0 = Math.Min(x1 - 1, x0 + inset);
                        rx1 = Math.Max(rx0 + 1, x1 - inset);
                    }

                    raw.AsSpan(y * stride + 1 + rx0, rx1 - rx0).Fill((byte)(1 + y));
                }

                if (settings.PeakHold)
                {
                    float peakValue = i < peaks.Length ? Math.Clamp(peaks[i], 0f, 1f) : 0f;
                    int peakY = Math.Clamp((int)MathF.Round(height - peakValue * height), Math.Max(1, (int)MathF.Round(2f * scale)), height - 1);
                    int peakEndY = Math.Min(height, peakY + scaledPeakThickness);
                    byte peakColorIdx = (byte)(1 + Math.Clamp((int)MathF.Round((1f - peakValue) * (height - 1)), 0, height - 1));
                    for (int py = peakY; py < peakEndY; py++)
                    {
                        raw.AsSpan(py * stride + 1 + x0, barW).Fill(peakColorIdx);
                    }
                }
            }
        }

        private static void RenderIndexedMirrorBars(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float centerY = height * 0.5f;
            float scale = height / 144f;
            int scaledTopRadius = settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0;

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;
                float halfH = Math.Max(1f, value * centerY);
                int y0 = Math.Clamp((int)MathF.Round(centerY - halfH), 0, height - 1);
                int y1 = Math.Clamp((int)MathF.Round(centerY + halfH), y0 + 1, height);
                int fullH = y1 - y0;
                int radius = Math.Min(scaledTopRadius, Math.Min(barW / 2, (int)halfH));

                for (int y = y0; y < y1; y++)
                {
                    int rx0 = x0;
                    int rx1 = x1;
                    int dy = y - y0;
                    if (radius > 0 && dy < radius)
                    {
                        float cy = radius - dy - 0.5f;
                        float dx = MathF.Sqrt(Math.Max(0f, radius * radius - cy * cy));
                        int inset = Math.Max(0, (int)MathF.Floor(radius - dx));
                        rx0 = Math.Min(x1 - 1, x0 + inset);
                        rx1 = Math.Max(rx0 + 1, x1 - inset);
                    }

                    raw.AsSpan(y * stride + 1 + rx0, rx1 - rx0).Fill((byte)(1 + y));
                }
            }
        }

        private static void RenderIndexedLedSegments(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float barGap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(2f, barWidth - barGap);
            float scale = height / 144f;

            int gapPx = Math.Max(1, (int)MathF.Round(Math.Clamp(settings.LedSegmentGapPx, 1, 4) * scale));

            int totalSegments;
            float segmentHeight;
            if (settings.LedSegmentMode == "fixedSize")
            {
                int segmentSizePx = Math.Max(2, (int)MathF.Round(Math.Clamp(settings.LedSegmentSizePx, 4, 16) * scale));
                totalSegments = Math.Max(1, (int)(height / (float)(segmentSizePx + gapPx)));
                segmentHeight = segmentSizePx;
            }
            else
            {
                totalSegments = Math.Clamp(settings.LedSegmentCount, 4, 24);
                float totalGap = gapPx * Math.Max(0, totalSegments - 1);
                segmentHeight = Math.Max(2f, (height - totalGap) / totalSegments);
            }

            double lowThreshold = Math.Clamp(settings.LedLowThreshold, 0.0, 1.0);
            double highThreshold = Math.Clamp(settings.LedHighThreshold, lowThreshold, 1.0);

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int litSegments = (int)Math.Round(value * totalSegments);
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + barGap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + barGap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;

                for (int s = 0; s < litSegments; s++)
                {
                    float fy = height - (s + 1) * (segmentHeight + gapPx) + gapPx;
                    if (fy < -segmentHeight)
                    {
                        break;
                    }

                    int y0 = Math.Clamp((int)MathF.Round(fy), 0, height - 1);
                    int y1 = Math.Clamp((int)MathF.Round(fy + segmentHeight), y0 + 1, height);

                    double ratio = (s + 1) / (double)totalSegments;
                    byte colorIdx = ratio > highThreshold ? (byte)3 : (ratio > lowThreshold ? (byte)2 : (byte)1);

                    bool drawBorder = barW >= 4 && (y1 - y0) >= 5;
                    for (int y = y0; y < y1; y++)
                    {
                        var rowSpan = raw.AsSpan(y * stride + 1 + x0, barW);
                        if (drawBorder)
                        {
                            if (y == y0 || y == y1 - 1)
                            {
                                rowSpan.Fill(4); // Black 1px border
                            }
                            else
                            {
                                rowSpan.Fill(colorIdx);
                                rowSpan[0] = 4;
                                rowSpan[barW - 1] = 4;
                            }
                        }
                        else
                        {
                            rowSpan.Fill(colorIdx);
                        }
                    }
                }
            }
        }

        private static ArraySegment<byte> RenderGdiToPngBytes(RenderScratch scratch, int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            var g = scratch.Graphics;
            g.Clear(ParseColor(settings.BgColor));

            switch (settings.RenderMode)
            {
                case "vuMeter":
                    RenderVuMeter(scratch, width, height, settings, bars);
                    break;
                case "mirror":
                    RenderMirrorBars(g, width, height, settings, bars, peaks);
                    break;
                case "dots":
                    RenderDots(scratch, width, height, settings, bars);
                    break;
                case "ledSegments":
                    RenderLedSegments(scratch, width, height, settings, bars);
                    break;
                default:
                    RenderBars(scratch, width, height, settings, bars, peaks);
                    break;
            }

            if (settings.ShowNumericMeter)
            {
                RenderNumericOverlay(scratch, width, height, bars);
            }

            var ms = scratch.Stream;
            ms.SetLength(0);
            scratch.Bitmap.Save(ms, ImageFormat.Png);
            return new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                }
                table[n] = c;
            }
            return table;
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        private static void RenderBars(RenderScratch scratch, int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            var g = scratch.Graphics;
            var peakBrush = scratch.ReusableBrush;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, (float)Math.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float scale = height / 144f;
            float peakThick = Math.Max(1f, settings.PeakThickness * scale);
            float topRad = settings.TopRadius * scale;

            using var gradient = BuildGradientBrush(settings, height);

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? bars[i] : 0f;
                float barHeight = Math.Max(2f * scale, value * height);
                float x = i * barWidth + gap / 2f;
                float y = height - barHeight;
                float radius = Math.Min(topRad, Math.Min(drawWidth / 2f, barHeight));

                if (radius <= 0.1f)
                {
                    g.FillRectangle(gradient, x, y, drawWidth, barHeight);
                }
                else
                {
                    using var path = CreateTopRoundedRect(x, y, drawWidth, barHeight, radius);
                    g.FillPath(gradient, path);
                }

                if (settings.PeakHold)
                {
                    float peakValue = i < peaks.Length ? peaks[i] : 0f;
                    float peakY = Math.Max(2f * scale, height - peakValue * height);
                    peakBrush.Color = InterpolateColor(settings.GradientStops, peakValue);
                    g.FillRectangle(peakBrush, x, peakY, drawWidth, peakThick);
                }
            }
        }

        private static void RenderMirrorBars(Graphics g, int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, (float)Math.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float centerY = height / 2f;
            float scale = height / 144f;
            float topRad = settings.TopRadius * scale;

            using var gradient = BuildGradientBrush(settings, height);

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? bars[i] : 0f;
                float halfHeight = Math.Max(1f, value * (height / 2f));
                float x = i * barWidth + gap / 2f;
                float y = centerY - halfHeight;
                float fullHeight = halfHeight * 2f;
                float radius = Math.Min(topRad, Math.Min(drawWidth / 2f, halfHeight));

                if (radius <= 0.1f)
                {
                    g.FillRectangle(gradient, x, y, drawWidth, fullHeight);
                }
                else
                {
                    using var path = CreateTopRoundedRect(x, y, drawWidth, fullHeight, radius);
                    g.FillPath(gradient, path);
                }
            }
        }

        private static void RenderDots(RenderScratch scratch, int width, int height, VisualizerSettings settings, float[] bars)
        {
            var g = scratch.Graphics;
            var dotBrush = scratch.ReusableBrush;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, (float)Math.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(2f, barWidth - gap);
            float scale = height / 144f;
            float spacing = Math.Max(1f, Math.Clamp(settings.DotSpacing, 2, 10) * scale);
            float dotSize = Math.Max(2f, drawWidth);
            int maxDots = Math.Max(1, (int)(height / (dotSize + spacing)));

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? bars[i] : 0f;
                int litDots = (int)Math.Round(value * maxDots);
                float x = i * barWidth + gap / 2f;

                for (int d = 0; d < litDots; d++)
                {
                    float y = height - (d + 1) * (dotSize + spacing) + spacing;
                    if (y < 0)
                    {
                        break;
                    }

                    float t = (float)d / Math.Max(1, maxDots - 1);
                    dotBrush.Color = InterpolateColor(settings.GradientStops, t);
                    g.FillEllipse(dotBrush, x, y, dotSize, dotSize);
                }
            }
        }

        private static void RenderLedSegments(RenderScratch scratch, int width, int height, VisualizerSettings settings, float[] bars)
        {
            var g = scratch.Graphics;
            var segmentBrush = scratch.ReusableBrush;
            var segmentPen = scratch.BlackBorderPen;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float barGap = Math.Max(1f, (float)Math.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(2f, barWidth - barGap);
            float scale = height / 144f;

            int gapPx = Math.Max(1, (int)MathF.Round(Math.Clamp(settings.LedSegmentGapPx, 1, 4) * scale));

            int totalSegments;
            float segmentHeight;

            if (settings.LedSegmentMode == "fixedSize")
            {
                int segmentSizePx = Math.Max(2, (int)MathF.Round(Math.Clamp(settings.LedSegmentSizePx, 4, 16) * scale));
                totalSegments = Math.Max(1, (int)(height / (float)(segmentSizePx + gapPx)));
                segmentHeight = segmentSizePx;
            }
            else
            {
                totalSegments = Math.Clamp(settings.LedSegmentCount, 4, 24);
                float totalGap = gapPx * Math.Max(0, totalSegments - 1);
                segmentHeight = Math.Max(2f, (height - totalGap) / totalSegments);
            }

            double lowThreshold = Math.Clamp(settings.LedLowThreshold, 0.0, 1.0);
            double highThreshold = Math.Clamp(settings.LedHighThreshold, lowThreshold, 1.0);

            Color lowColor = ParseColor(settings.LedLowColor);
            Color midColor = ParseColor(settings.LedMidColor);
            Color highColor = ParseColor(settings.LedHighColor);
            bool drawBorder = drawWidth >= 4f && segmentHeight >= 5f;

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? bars[i] : 0f;
                int litSegments = (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * totalSegments);
                float x = i * barWidth + barGap / 2f;

                for (int s = 0; s < litSegments; s++)
                {
                    float y = height - (s + 1) * (segmentHeight + gapPx) + gapPx;
                    if (y < -segmentHeight)
                    {
                        break;
                    }

                    double segmentPositionRatio = (s + 1) / (double)totalSegments;
                    segmentBrush.Color = segmentPositionRatio > highThreshold
                        ? highColor
                        : (segmentPositionRatio > lowThreshold ? midColor : lowColor);

                    var rect = new RectangleF(x, y, drawWidth, segmentHeight);
                    g.FillRectangle(segmentBrush, rect);
                    if (drawBorder)
                    {
                        g.DrawRectangle(segmentPen, rect.X, rect.Y, rect.Width, rect.Height);
                    }
                }
            }
        }

        private static void RenderVuMeter(RenderScratch scratch, int width, int height, VisualizerSettings settings, float[] bars)
        {
            var g = scratch.Graphics;
            float sum = 0f;
            for (int i = 0; i < bars.Length; i++)
            {
                sum += bars[i];
            }
            float value = bars.Length > 0 ? sum / bars.Length : 0f;
            float scale = height / 144f;

            float centerX = width / 2f;
            float centerY = height * 0.82f;
            float radius = Math.Min(width, height) * 0.62f;

            var arcRect = new RectangleF(centerX - radius, centerY - radius, radius * 2f, radius * 2f);
            g.DrawArc(scratch.VuArcPen, arcRect, 200f, 140f);

            double minAngleDeg = 200.0;
            double maxAngleDeg = 340.0;
            double angleDeg = minAngleDeg + Math.Clamp(value, 0.0, 1.0) * (maxAngleDeg - minAngleDeg);
            double angleRad = angleDeg * Math.PI / 180.0;

            float needleLength = radius * 0.92f;
            float tipX = centerX + (float)(Math.Cos(angleRad) * needleLength);
            float tipY = centerY + (float)(Math.Sin(angleRad) * needleLength);

            Color needleColor = InterpolateColor(settings.GradientStops, value);
            scratch.ReusablePen.Color = needleColor;
            g.DrawLine(scratch.ReusablePen, centerX, centerY, tipX, tipY);

            scratch.ReusableBrush.Color = needleColor;
            float hubSize = Math.Max(4f, 8f * scale);
            g.FillEllipse(scratch.ReusableBrush, centerX - hubSize / 2f, centerY - hubSize / 2f, hubSize, hubSize);
        }

        private static void RenderNumericOverlay(RenderScratch scratch, int width, int height, float[] bars)
        {
            var g = scratch.Graphics;
            double sum = 0.0;
            for (int i = 0; i < bars.Length; i++)
            {
                sum += bars[i];
            }
            double mean = bars.Length > 0 ? sum / bars.Length : 0.0;
            double safe = Math.Max(mean, 0.0001);
            double db = Math.Clamp(20.0 * Math.Log10(safe), -60.0, 0.0);

            string text = db.ToString("F0", CultureInfo.InvariantCulture) + "dB";
            var textSize = g.MeasureString(text, scratch.OverlayFont);

            g.FillRectangle(scratch.OverlayBgBrush, 2, 2, textSize.Width + 4, textSize.Height + 2);
            g.DrawString(text, scratch.OverlayFont, scratch.OverlayTextBrush, 4, 2);
        }

        private static LinearGradientBrush BuildGradientBrush(VisualizerSettings settings, int height)
        {
            var brush = new LinearGradientBrush(new Point(0, height), new Point(0, 0), Color.White, Color.White);
            var stops = settings.GradientStops;
            int count = stops.Count;
            var positions = new float[count];
            var colors = new Color[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = (float)stops[i].Pos;
                colors[i] = ParseColor(stops[i].Color);
            }
            Array.Sort(positions, colors);

            brush.InterpolationColors = new ColorBlend
            {
                Positions = positions,
                Colors = colors
            };
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
            int count = stops.Count;
            if (count == 0)
            {
                return Color.White;
            }

            GradientStop? bestBefore = null;
            GradientStop? bestAfter = null;
            GradientStop? minStop = null;
            GradientStop? maxStop = null;

            for (int i = 0; i < count; i++)
            {
                var s = stops[i];
                if (minStop == null || s.Pos < minStop.Pos) minStop = s;
                if (maxStop == null || s.Pos > maxStop.Pos) maxStop = s;
                if (s.Pos <= t && (bestBefore == null || s.Pos > bestBefore.Pos)) bestBefore = s;
                if (s.Pos >= t && (bestAfter == null || s.Pos < bestAfter.Pos)) bestAfter = s;
            }

            if (bestBefore == null)
            {
                return ParseColor(minStop!.Color);
            }

            if (bestAfter == null)
            {
                return ParseColor(maxStop!.Color);
            }

            if (bestBefore == bestAfter || Math.Abs(bestAfter.Pos - bestBefore.Pos) < 1e-6)
            {
                return ParseColor(bestBefore.Color);
            }

            float local = (float)((t - bestBefore.Pos) / (bestAfter.Pos - bestBefore.Pos));
            return Blend(ParseColor(bestBefore.Color), ParseColor(bestAfter.Color), local);
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

            settings.GainDb = Math.Clamp(settings.GainDb, -18.0, 30.0);
            settings.MinDb = Math.Clamp(settings.MinDb, -72.0, -12.0);
            settings.SlopeDbPerOct = Math.Clamp(settings.SlopeDbPerOct, 0.0, 8.0);
            settings.Gamma = Math.Clamp(settings.Gamma, 0.3, 4.0);
            settings.NoiseGateDb = Math.Clamp(settings.NoiseGateDb, -90.0, -20.0);

            settings.AttackSpeed = Math.Clamp(settings.AttackSpeed, 0.01, 1.0);
            settings.ReleaseSpeed = Math.Clamp(settings.ReleaseSpeed, 0.01, 1.0);

            settings.Smoothing = Math.Clamp(settings.Smoothing, 0.0, 0.85);
            settings.PeakFallSpeed = Math.Clamp(settings.PeakFallSpeed, 0.005, 0.25);
            settings.PeakThickness = Math.Clamp(settings.PeakThickness, 1, 8);
            settings.TopRadius = Math.Clamp(settings.TopRadius, 0, 12);
            settings.BgColor = NormalizeHex(settings.BgColor, "#08150e");

            settings.RenderMode = NormalizeRenderMode(settings.RenderMode);
            settings.DotSpacing = Math.Clamp(settings.DotSpacing, 2, 10);
            settings.AutoPauseTimeoutSec = Math.Clamp(settings.AutoPauseTimeoutSec, 5, 120);

            settings.LedSegmentMode = settings.LedSegmentMode == "fixedSize" ? "fixedSize" : "fixedCount";
            settings.LedSegmentCount = Math.Clamp(settings.LedSegmentCount, 4, 24);
            settings.LedSegmentSizePx = Math.Clamp(settings.LedSegmentSizePx, 4, 16);
            settings.LedSegmentGapPx = Math.Clamp(settings.LedSegmentGapPx, 1, 4);
            settings.LedLowColor = NormalizeHex(settings.LedLowColor, "#46e661");
            settings.LedMidColor = NormalizeHex(settings.LedMidColor, "#46e661");
            settings.LedHighColor = NormalizeHex(settings.LedHighColor, "#154320");
            settings.LedLowThreshold = Math.Clamp(settings.LedLowThreshold, 0.0, 1.0);
            settings.LedHighThreshold = Math.Clamp(settings.LedHighThreshold, settings.LedLowThreshold, 1.0);

            if (settings.GradientStops == null || settings.GradientStops.Count < 2)
            {
                settings.GradientStops = VisualizerSettings.CreateDefault().GradientStops;
            }

            settings.GradientStops = settings.GradientStops
            .Select(s => new GradientStop
            {
                Pos = Math.Clamp(s.Pos, 0.0, 1.0),
                Color = NormalizeHex(s.Color, "#46e661")
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

        private static string NormalizeRenderMode(string? mode)
        {
            return mode switch
            {
                "bars" => "bars",
                "vuMeter" => "vuMeter",
                "mirror" => "mirror",
                "dots" => "dots",
                "ledSegments" => "ledSegments",
                _ => "bars"
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

        [JsonPropertyName("gainDb")]
        public double GainDb { get; set; } = 6.0;

        [JsonPropertyName("minDb")]
        public double MinDb { get; set; } = -48.0;

        [JsonPropertyName("slopeDbPerOct")]
        public double SlopeDbPerOct { get; set; } = 4.0;

        [JsonPropertyName("gamma")]
        public double Gamma { get; set; } = 1.0;

        [JsonPropertyName("noiseGateDb")]
        public double NoiseGateDb { get; set; } = -65.0;

        [JsonPropertyName("attackSpeed")]
        public double AttackSpeed { get; set; } = 0.65;

        [JsonPropertyName("releaseSpeed")]
        public double ReleaseSpeed { get; set; } = 0.25;

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
        public string BgColor { get; set; } = "#08150e";

        [JsonPropertyName("gradientStops")]
        public List<GradientStop> GradientStops { get; set; } = new()
{
new GradientStop { Pos = 0.0, Color = "#154320" },
new GradientStop { Pos = 0.7, Color = "#46e661" },
new GradientStop { Pos = 1.0, Color = "#46e661" }
};

        [JsonPropertyName("paused")]
        public bool Paused { get; set; } = false;

        [JsonPropertyName("renderMode")]
        public string RenderMode { get; set; } = "bars";

        [JsonPropertyName("dotSpacing")]
        public int DotSpacing { get; set; } = 3;

        [JsonPropertyName("showNumericMeter")]
        public bool ShowNumericMeter { get; set; } = false;

        [JsonPropertyName("autoPauseEnabled")]
        public bool AutoPauseEnabled { get; set; } = false;

        [JsonPropertyName("autoPauseTimeoutSec")]
        public int AutoPauseTimeoutSec { get; set; } = 30;

        [JsonPropertyName("ledSegmentMode")]
        public string LedSegmentMode { get; set; } = "fixedCount";

        [JsonPropertyName("ledSegmentCount")]
        public int LedSegmentCount { get; set; } = 14;

        [JsonPropertyName("ledSegmentSizePx")]
        public int LedSegmentSizePx { get; set; } = 8;

        [JsonPropertyName("ledSegmentGapPx")]
        public int LedSegmentGapPx { get; set; } = 2;

        [JsonPropertyName("ledLowColor")]
        public string LedLowColor { get; set; } = "#46e661";

        [JsonPropertyName("ledMidColor")]
        public string LedMidColor { get; set; } = "#46e661";

        [JsonPropertyName("ledHighColor")]
        public string LedHighColor { get; set; } = "#154320";

        [JsonPropertyName("ledLowThreshold")]
        public double LedLowThreshold { get; set; } = 0.5;

        [JsonPropertyName("ledHighThreshold")]
        public double LedHighThreshold { get; set; } = 0.85;

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
                GainDb = GainDb,
                MinDb = MinDb,
                SlopeDbPerOct = SlopeDbPerOct,
                Gamma = Gamma,
                NoiseGateDb = NoiseGateDb,
                AttackSpeed = AttackSpeed,
                ReleaseSpeed = ReleaseSpeed,
                Smoothing = Smoothing,
                PeakHold = PeakHold,
                PeakFallSpeed = PeakFallSpeed,
                PeakThickness = PeakThickness,
                TopRadius = TopRadius,
                BgColor = BgColor,
                GradientStops = GradientStops.Select(x => x.Clone()).ToList(),
                Paused = Paused,
                RenderMode = RenderMode,
                DotSpacing = DotSpacing,
                ShowNumericMeter = ShowNumericMeter,
                AutoPauseEnabled = AutoPauseEnabled,
                AutoPauseTimeoutSec = AutoPauseTimeoutSec,
                LedSegmentMode = LedSegmentMode,
                LedSegmentCount = LedSegmentCount,
                LedSegmentSizePx = LedSegmentSizePx,
                LedSegmentGapPx = LedSegmentGapPx,
                LedLowColor = LedLowColor,
                LedMidColor = LedMidColor,
                LedHighColor = LedHighColor,
                LedLowThreshold = LedLowThreshold,
                LedHighThreshold = LedHighThreshold
            };
        }
    }

    internal sealed class GradientStop
    {
        [JsonPropertyName("pos")]
        public double Pos { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; } = "#46e661";

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
