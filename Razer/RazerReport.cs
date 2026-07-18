using System;

namespace InfoPanel.PeripheralBattery.Razer;

/// <summary>
/// The 90-byte Razer "control report" used over a HID feature report to talk to
/// mice, keyboards and (apparently) some headsets.
///
/// Layout (matches openrazer's `struct razer_report` and the community
/// spozer/razer-battery-checker Python port):
///
///   byte 0      status
///   byte 1      transaction id
///   byte 2-3    remaining packets (big endian)
///   byte 4      protocol type (always 0x00)
///   byte 5      data size
///   byte 6      command class
///   byte 7      command id
///   byte 8-87   arguments (80 bytes)
///   byte 88     crc (xor of bytes 2..87 inclusive)
///   byte 89     reserved
///
/// On Windows, HidSharp/hidapi expect the HID *report id* prepended as an
/// extra byte in front of all 90 of these when calling Get/SetFeature, so the
/// buffer that actually goes over the wire is 91 bytes. RazerHidBatteryDevice
/// handles that extra byte - this class only knows about the 90-byte payload.
/// </summary>
public sealed class RazerReport
{
    public const int ArgumentsLength = 80;
    public const int PacketLength = 90; // total size of the struct above, no report-id byte

    public const byte StatusNewCommand = 0x00;
    public const byte StatusBusy = 0x01;
    public const byte StatusSuccessful = 0x02;
    public const byte StatusFailure = 0x03;
    public const byte StatusNoResponse = 0x04;
    public const byte StatusNotSupported = 0x05;

    public byte Status;
    public byte TransactionId;
    public ushort RemainingPackets;
    public byte ProtocolType;
    public byte DataSize;
    public byte CommandClass;
    public byte CommandId;
    public byte[] Arguments = new byte[ArgumentsLength];
    public byte Crc;
    public byte Reserved;

    public static RazerReport CreateCommand(byte transactionId, byte commandClass, byte commandId, byte dataSize)
    {
        return new RazerReport
        {
            Status = StatusNewCommand,
            TransactionId = transactionId,
            RemainingPackets = 0,
            ProtocolType = 0,
            CommandClass = commandClass,
            CommandId = commandId,
            DataSize = dataSize
        };
    }

    public byte[] Pack()
    {
        var data = new byte[PacketLength];
        data[0] = Status;
        data[1] = TransactionId;
        data[2] = (byte)((RemainingPackets >> 8) & 0xFF);
        data[3] = (byte)(RemainingPackets & 0xFF);
        data[4] = ProtocolType;
        data[5] = DataSize;
        data[6] = CommandClass;
        data[7] = CommandId;
        Array.Copy(Arguments, 0, data, 8, ArgumentsLength);
        data[88] = Crc;
        data[89] = Reserved;
        return data;
    }

    public static RazerReport FromBytes(byte[] data, int offset = 0)
    {
        if (data.Length - offset < PacketLength)
            throw new ArgumentException($"Expected at least {PacketLength} bytes at offset {offset}, got {data.Length - offset}");

        var report = new RazerReport
        {
            Status = data[offset + 0],
            TransactionId = data[offset + 1],
            RemainingPackets = (ushort)((data[offset + 2] << 8) | data[offset + 3]),
            ProtocolType = data[offset + 4],
            DataSize = data[offset + 5],
            CommandClass = data[offset + 6],
            CommandId = data[offset + 7],
            Crc = data[offset + 88],
            Reserved = data[offset + 89]
        };
        report.Arguments = new byte[ArgumentsLength];
        Array.Copy(data, offset + 8, report.Arguments, 0, ArgumentsLength);
        return report;
    }

    public byte CalculateCrc()
    {
        var data = Pack();
        byte crc = 0;
        // XOR bytes 2 through 87 inclusive (transaction id / status / crc / reserved excluded)
        for (int i = 2; i < 88; i++)
            crc ^= data[i];
        return crc;
    }

    public bool HasValidCrc() => CalculateCrc() == Crc;
}
