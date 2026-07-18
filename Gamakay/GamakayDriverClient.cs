using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using InfoPanel.PeripheralBattery.Gamakay.Proto;

namespace InfoPanel.PeripheralBattery.Gamakay;

/// <summary>
/// Reads battery/online status for the Gamakay TK75HE V2 by talking to the
/// SAME local service Gamakay's own software uses: a background process
/// (iot_driver_v&lt;version&gt;.exe) that the Electron app spawns, exposing a
/// gRPC-Web API on http://127.0.0.1:3814. This was found by extracting and
/// reading the app's bundled index.js (it's an Electron app, so the source
/// ships unencrypted in app.asar) rather than by capturing raw USB traffic.
///
/// Caveats, both discovered the same way:
///   - This requires that background process to be running. It's normally
///     started by the Gamakay app itself. If InfoPanel starts before you've
///     ever opened the Gamakay software, or if the process isn't set to
///     launch with Windows, this will just report "not detected" until you
///     open the app once. Worth checking Task Manager for a process with
///     "iot_driver" in the name to confirm it's running standalone.
///   - There is no charging-status field anywhere in this protocol - the
///     app's own UI doesn't show one either. TryGetChargingStatus always
///     returns false (not available), it isn't a bug.
/// </summary>
public sealed class GamakayDriverClient : IDisposable
{
    private const string ServiceUrl = "http://127.0.0.1:3814";
    private const uint TargetVendorId = 0x3151;
    private const uint TargetProductId = 0x5038;

    private GrpcChannel? _channel;
    private CancellationTokenSource? _watchCts;
    private Task? _watchTask;

    private volatile int _lastBatteryPercent = -1;
    private volatile bool _lastIsOnline;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    public string DisplayName => "Gamakay TK75HE V2";

    public bool EnsureConnected()
    {
        if (_watchTask is { IsCompleted: false })
            return true;

        try
        {
            _channel?.Dispose();
            var httpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
            _channel = GrpcChannel.ForAddress(ServiceUrl, new GrpcChannelOptions
            {
                HttpHandler = httpHandler
            });

            _watchCts?.Cancel();
            _watchCts = new CancellationTokenSource();
            var client = new DriverGrpc.DriverGrpcClient(_channel);
            _watchTask = WatchLoopAsync(client, _watchCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WatchLoopAsync(DriverGrpc.DriverGrpcClient client, CancellationToken token)
    {
        try
        {
            using var call = client.WatchDevList(new Empty(), cancellationToken: token);
            await foreach (var deviceList in call.ResponseStream.ReadAllAsync(token))
            {
                foreach (var djDev in deviceList.DevList)
                {
                    switch (djDev.OneofDevCase)
                    {
                        case DJDev.OneofDevOneofCase.Dev:
                        {
                            var dev = djDev.Dev;
                            if (dev.Vid != TargetVendorId || dev.Pid != TargetProductId)
                                continue;

                            _lastBatteryPercent = (int)dev.Battery;
                            _lastIsOnline = dev.IsOnline;
                            _lastUpdateUtc = DateTime.UtcNow;
                            break;
                        }
                        case DJDev.OneofDevOneofCase.DangleCommonDev:
                        {
                            var combo = djDev.DangleCommonDev;
                            if (combo.Vid != TargetVendorId || combo.Pid != TargetProductId)
                                continue;

                            if (combo.Keyboard?.DangleDevCase == DangleDevOrEmpty.DangleDevOneofCase.Status)
                            {
                                _lastBatteryPercent = (int)combo.Keyboard.Status.Battery;
                                _lastIsOnline = combo.Keyboard.Status.IsOnline;
                                _lastUpdateUtc = DateTime.UtcNow;
                            }
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Stream ended/errored (service not running, restarted, etc).
            // EnsureConnected() will restart it on the next poll.
        }
    }

    public bool TryGetBatteryPercent(out int percent)
    {
        percent = _lastBatteryPercent;
        return IsFresh() && _lastIsOnline && percent >= 0;
    }

    /// <summary>Always unavailable - see class remarks. Kept for API symmetry with the Razer devices.</summary>
    public bool TryGetChargingStatus(out bool isCharging)
    {
        isCharging = false;
        return false;
    }

    public bool IsOnline => IsFresh() && _lastIsOnline;

    private bool IsFresh() => DateTime.UtcNow - _lastUpdateUtc < StaleAfter;

    public void Dispose()
    {
        _watchCts?.Cancel();
        _channel?.Dispose();
    }
}
