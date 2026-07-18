using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using InfoPanel.PeripheralBattery.Razer;
using InfoPanel.PeripheralBattery.Gamakay;

namespace InfoPanel.PeripheralBattery;

public class PeripheralBatteryPlugin : BasePlugin, IPluginConfigurable
{
    // --- Devices -------------------------------------------------------

    private readonly RazerHidBatteryDevice _deathAdder =
        new("DeathAdder V4 Pro", productNameContains: "DeathAdder V4 Pro", productIds: [0x00BE, 0x00BF]);

    private readonly RazerHidBatteryDevice _blackshark =
        new("Blackshark V3", productNameContains: "BlackShark V3", productIds: [0x057A]);

    private readonly GamakayDriverClient _gamakay = new();

    // --- UI entries ------------------------------------------------------

    private readonly PluginText _daStatus = new("da-status", "Status", "Not detected");
    private readonly PluginSensor _daBattery = new("da-battery", "Battery", 0f, "%");
    private readonly PluginText _daCharging = new("da-charging", "Charging", "-");
    private readonly PluginText _daMedium = new("da-medium", "Connection", "-");

    private readonly PluginText _bsStatus = new("bs-status", "Status", "Not detected");
    private readonly PluginSensor _bsBattery = new("bs-battery", "Battery", 0f, "%");
    private readonly PluginText _bsCharging = new("bs-charging", "Charging", "-");
    private readonly PluginText _bsMedium = new("bs-medium", "Connection", "-");

    private readonly PluginText _gkStatus = new("gk-status", "Status", "Not implemented yet");
    private readonly PluginSensor _gkBattery = new("gk-battery", "Battery", 0f, "%");
    private readonly PluginText _gkCharging = new("gk-charging", "Charging", "-");
    private readonly PluginText _gkMedium = new("gk-medium", "Connection", "-");

    // --- Config ------------------------------------------------------------

    private bool _deathAdderEnabled = true;
    private bool _blacksharkEnabled = true;
    private bool _gamakayEnabled = true; // now backed by the real gRPC-Web client
    private int _pollSeconds = 20;

    public PeripheralBatteryPlugin()
        : base("peripheral-battery", "Peripheral Battery Monitor",
               "Battery and charging status for DeathAdder V4 Pro, Blackshark V3 and Gamakay TK75HE V2") { }

    public override string? ConfigFilePath => null;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(_pollSeconds);

    public override void Initialize() { }

    public override void Load(List<IPluginContainer> containers)
    {
        var da = new PluginContainer("deathadder-v4-pro", "DeathAdder V4 Pro");
        da.Entries.AddRange([_daStatus, _daBattery, _daCharging, _daMedium]);
        containers.Add(da);

        var bs = new PluginContainer("blackshark-v3", "Blackshark V3");
        bs.Entries.AddRange([_bsStatus, _bsBattery, _bsCharging, _bsMedium]);
        containers.Add(bs);

        var gk = new PluginContainer("gamakay-tk75he-v2", "Gamakay TK75HE V2");
        gk.Entries.AddRange([_gkStatus, _gkBattery, _gkCharging, _gkMedium]);
        containers.Add(gk);
    }

    public override async Task UpdateAsync(CancellationToken cancellationToken)
    {
        if (_deathAdderEnabled)
            UpdateRazerDevice(_deathAdder, _daStatus, _daBattery, _daCharging, _daMedium);
        else
            _daStatus.Value = "Disabled";

        if (_blacksharkEnabled)
            UpdateRazerDevice(_blackshark, _bsStatus, _bsBattery, _bsCharging, _bsMedium);
        else
            _bsStatus.Value = "Disabled";

        if (_gamakayEnabled)
            UpdateGamakayDevice();
        else
            _gkStatus.Value = "Not implemented yet - see Gamakay/GamakayHidDevice.cs";

        await Task.CompletedTask;
    }

    private static void UpdateRazerDevice(
        RazerHidBatteryDevice device,
        PluginText status,
        PluginSensor battery,
        PluginText charging,
        PluginText medium)
    {
        if (!device.EnsureConnected())
        {
            status.Value = "Not detected";
            charging.Value = "-";
            medium.Value = "-";
            return;
        }

        var gotBattery = device.TryGetBatteryPercent(out var percent);
        var gotCharging = device.TryGetChargingStatus(out var isCharging);

        if (!gotBattery && !gotCharging)
        {
            // Handshake succeeded earlier but device has since gone to sleep /
            // out of range. Drop the connection so the next tick retries fresh.
            device.Disconnect();
            status.Value = "Not responding";
            charging.Value = "-";
            medium.Value = "-";
            return;
        }

        status.Value = "Connected";
        medium.Value = device.ConnectionMedium ?? "-";

        if (gotBattery)
            battery.Value = percent;

        charging.Value = gotCharging ? (isCharging ? "Charging" : "Not charging") : "Unknown";
    }

    private void UpdateGamakayDevice()
    {
        if (!_gamakay.EnsureConnected())
        {
            _gkStatus.Value = "Driver service not reachable (127.0.0.1:3814)";
            _gkCharging.Value = "-";
            _gkMedium.Value = "-";
            return;
        }

        if (!_gamakay.IsOnline)
        {
            _gkStatus.Value = "Not detected";
            _gkCharging.Value = "-";
            _gkMedium.Value = "-";
            return;
        }

        _gkStatus.Value = "Connected";
        _gkMedium.Value = "Unknown (not exposed by driver service)";

        if (_gamakay.TryGetBatteryPercent(out var percent))
            _gkBattery.Value = percent;

        // No charging field exists in this protocol - see GamakayDriverClient remarks.
        _gkCharging.Value = "Not available";
    }

    public override void Update() => throw new NotImplementedException();

    public override void Close()
    {
        _deathAdder.Dispose();
        _blackshark.Dispose();
        _gamakay.Dispose();
    }

    // --- IPluginConfigurable ------------------------------------------------

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new PluginConfigProperty
        {
            Key = "deathAdderEnabled",
            DisplayName = "Enable DeathAdder V4 Pro",
            Type = PluginConfigType.Boolean,
            Value = _deathAdderEnabled
        },
        new PluginConfigProperty
        {
            Key = "blacksharkEnabled",
            DisplayName = "Enable Blackshark V3",
            Type = PluginConfigType.Boolean,
            Value = _blacksharkEnabled
        },
        new PluginConfigProperty
        {
            Key = "gamakayEnabled",
            DisplayName = "Enable Gamakay TK75HE V2 (needs code filled in first)",
            Type = PluginConfigType.Boolean,
            Value = _gamakayEnabled
        },
        new PluginConfigProperty
        {
            Key = "pollSeconds",
            DisplayName = "Poll interval (seconds)",
            Type = PluginConfigType.Integer,
            Value = _pollSeconds,
            MinValue = 5,
            MaxValue = 300,
            Step = 5
        }
    ];

    public void ApplyConfig(string key, object? value)
    {
        switch (key)
        {
            case "deathAdderEnabled":
                _deathAdderEnabled = Convert.ToBoolean(value);
                break;
            case "blacksharkEnabled":
                _blacksharkEnabled = Convert.ToBoolean(value);
                break;
            case "gamakayEnabled":
                _gamakayEnabled = Convert.ToBoolean(value);
                break;
            case "pollSeconds":
                _pollSeconds = Convert.ToInt32(value);
                break;
        }
    }
}
