using System;
using System.Linq;
using System.Threading;
using HidSharp;

namespace InfoPanel.PeripheralBattery.Razer;

/// <summary>
/// Talks to a Razer device's HID "control channel" to read battery % and
/// charging status, bypassing Synapse entirely.
///
/// Two things about this protocol are NOT publicly documented per-device and
/// have to be guessed/confirmed:
///   1) the "transaction id" byte (varies by device family - known values are
///      0x1F for newer devices incl. DeathAdder V4 Pro, 0x3F for older ones)
///   2) the HID report id the control channel lives on (0x00 for basically
///      every mouse/keyboard, but reverse-engineering of some Razer audio
///      devices - Nommo V2 X, Leviathan V2 X - found them using 0x07 instead)
///
/// Rather than hardcode a guess and silently fail, this class just tries the
/// known combinations against every matching HID interface and keeps
/// whichever one round-trips a valid, CRC-correct response. This is why
/// hardware doesn't need a confirmed PID either - matching is done by
/// product name substring plus "does the protocol handshake actually work".
/// </summary>
public sealed class RazerHidBatteryDevice : IDisposable
{
    private const int RazerVendorId = 0x1532;
    private static readonly byte[] TransactionIdCandidates = { 0x1F, 0x3F };
    private static readonly byte[] ReportIdCandidates = { 0x00, 0x07 };
    private const int FeatureBufferLength = RazerReport.PacketLength + 1; // +1 for the report-id byte

    private readonly string _productNameContains;
    private readonly int[]? _productIds;

    private HidStream? _stream;
    private byte _transactionId;
    private byte _reportId;
    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(15);

    public string DisplayName { get; }
    public bool IsConnected => _stream is not null;
    public string? ConnectionMedium { get; private set; } // "USB / 2.4GHz", "Bluetooth", or null

    /// <param name="productNameContains">Fallback matcher, used when productIds is null/empty or matches nothing.</param>
    /// <param name="productIds">
    /// Confirmed USB Product ID(s), e.g. new[] { 0x057A } for Blackshark V3.
    /// When supplied, this takes priority over name matching - it's faster and
    /// more reliable, since HidSharp's GetProductName() can be flaky/empty on
    /// some Windows configurations.
    /// </param>
    public RazerHidBatteryDevice(string displayName, string productNameContains, int[]? productIds = null)
    {
        DisplayName = displayName;
        _productNameContains = productNameContains;
        _productIds = productIds;
    }

    public bool EnsureConnected()
    {
        if (_stream is not null)
            return true;

        if (DateTime.UtcNow - _lastConnectAttemptUtc < ReconnectBackoff)
            return false;

        _lastConnectAttemptUtc = DateTime.UtcNow;

        var candidates = DeviceList.Local
            .GetHidDevices(vendorID: RazerVendorId)
            .Where(d =>
            {
                try
                {
                    if (d.GetMaxFeatureReportLength() != FeatureBufferLength)
                        return false;

                    if (_productIds is { Length: > 0 })
                        return _productIds.Contains(d.ProductID);

                    return d.GetProductName()?.Contains(_productNameContains, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

        foreach (var device in candidates)
        {
            if (!device.TryOpen(out var stream))
                continue;

            foreach (var reportId in ReportIdCandidates)
            {
                foreach (var transactionId in TransactionIdCandidates)
                {
                    if (TryHandshake(stream, reportId, transactionId))
                    {
                        _stream = stream;
                        _reportId = reportId;
                        _transactionId = transactionId;
                        ConnectionMedium = device.DevicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                            ? "Bluetooth"
                            : "USB / 2.4GHz";
                        return true;
                    }
                }
            }

            stream.Dispose();
        }

        return false;
    }

    private static bool TryHandshake(HidStream stream, byte reportId, byte transactionId)
    {
        // Getting battery level is a harmless, side-effect-free command - good for probing.
        var request = RazerReport.CreateCommand(transactionId, 0x07, 0x80, 0x02);
        return TrySendReceive(stream, reportId, request, out _, tries: 1);
    }

    public bool TryGetBatteryPercent(out int percent)
    {
        percent = -1;
        if (!EnsureConnected())
            return false;

        var request = RazerReport.CreateCommand(_transactionId, 0x07, 0x80, 0x02);
        if (!TrySendReceive(_stream!, _reportId, request, out var response))
            return false;

        percent = (int)Math.Round(response!.Arguments[1] / 255.0 * 100.0);
        return true;
    }

    public bool TryGetChargingStatus(out bool isCharging)
    {
        isCharging = false;
        if (!EnsureConnected())
            return false;

        var request = RazerReport.CreateCommand(_transactionId, 0x07, 0x84, 0x02);
        if (!TrySendReceive(_stream!, _reportId, request, out var response))
            return false;

        isCharging = response!.Arguments[1] != 0;
        return true;
    }

    private static bool TrySendReceive(HidStream stream, byte reportId, RazerReport request, out RazerReport? response, int tries = 5)
    {
        response = null;
        request.Crc = request.CalculateCrc();
        var requestPacked = request.Pack();

        for (int attempt = 0; attempt < tries; attempt++)
        {
            try
            {
                var sendBuffer = new byte[FeatureBufferLength];
                sendBuffer[0] = reportId;
                Array.Copy(requestPacked, 0, sendBuffer, 1, RazerReport.PacketLength);
                stream.SetFeature(sendBuffer);

                Thread.Sleep(60); // devices need a beat to prepare the response

                var recvBuffer = new byte[FeatureBufferLength];
                recvBuffer[0] = reportId;
                stream.GetFeature(recvBuffer);

                var candidate = RazerReport.FromBytes(recvBuffer, offset: 1);

                if (!candidate.HasValidCrc())
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (candidate.CommandClass != request.CommandClass || candidate.CommandId != request.CommandId)
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (candidate.Status == RazerReport.StatusSuccessful)
                {
                    response = candidate;
                    return true;
                }

                // busy / no-response / etc - worth a retry
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        ConnectionMedium = null;
    }

    public void Dispose() => Disconnect();
}
