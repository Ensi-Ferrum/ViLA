﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Configuration;
using Core;
using Microsoft.Extensions.Logging;
using Virpil.Communicator;

namespace ViLA;

public class Runner : IDisposable
{
    private readonly List<PluginBase.PluginBase> _plugins;
    private readonly VirpilMonitor _monitor;

    // id => action
    private readonly Dictionary<string, List<DeviceAction<BaseTrigger>>> _actions = new();

    private readonly ILogger<Runner> _log;

    private readonly State _state = new();

    public Runner(VirpilMonitor deviceMonitor, IEnumerable<DeviceConfiguration> deviceConfigs, List<PluginBase.PluginBase> plugins, ILogger<Runner> log)
    {
        _log = log;
        _plugins = plugins;
        _monitor = deviceMonitor;

        foreach (var deviceConfig in deviceConfigs)
        {
            if (deviceConfig.Devices is null) continue;

            foreach (var (deviceId, device) in deviceConfig.Devices)
            {
                var deviceShort = ushort.Parse(deviceId, NumberStyles.HexNumber);
                foreach (var (boardType, boardActions) in device)
                {
                    foreach (var (ledNumber, ledActions) in boardActions)
                    {
                        foreach (var action in ledActions)
                        {
                            try
                            {
                                // if the file requires a trigger, then only run these actions when the trigger matches
                                // this isn't the most efficient, but it works functionally for now
                                var trigger = deviceConfig.Trigger is not null
                                    ? new AndTrigger(new List<BaseTrigger> { deviceConfig.Trigger, action.Trigger })
                                    : action.Trigger;
                                SetUpTrigger(trigger, action, ledNumber, boardType, deviceShort);
                            }
                            catch (ArgumentException)
                            {
                                _log.LogError("Unsupported comparator passed for {Id}. Skipping...", action.Trigger.Id);
                            }
                        }
                    }
                }
            }
        }
    }

    private void SetUpTrigger(BaseTrigger trigger, LedAction ledAction, int ledNumber, BoardType boardType, ushort deviceShort)
    {
        foreach (var key in trigger.TriggerStrings)
        {
            if (!_actions.ContainsKey(key))
            {
                _actions[key] = new List<DeviceAction<BaseTrigger>>();
            }

            _actions[key].Add(new DeviceAction<BaseTrigger>(ledAction.Color, trigger, new Target(boardType, ledNumber), deviceShort));
        }
    }

    public async Task Start(ILoggerFactory loggerFactory)
    {
        _log.LogInformation("Starting plugins...");

        foreach (var plugin in _plugins)
        {
            plugin.Send += SendAction;
            plugin.SendTrigger += TriggerAction;
            plugin.LoggerFactory = loggerFactory;
            plugin.ClearState += ClearStateAction;
            plugin.Triggers = _actions.Keys;

            _log.LogDebug("Starting plugin {Plugin}", plugin.GetType().Name);

            var result = await plugin.Start();

            if (result)
            {
                _log.LogDebug("Started successfully");
            }
            else
            {
                _log.LogError("Error encountered during start up. Skipping...");
            }
        }

        _log.LogInformation("Plugins started");
    }

    private void SendAction(string id, dynamic value)
    {
        TriggerActionForValue(_actions, id, value);
    }

    private void TriggerActionForValue(IReadOnlyDictionary<string, List<DeviceAction<BaseTrigger>>> typedActions, string id, dynamic value)
    {
        if (!typedActions.TryGetValue(id, out var actions)) return; // nothing for this id, then leave

        _log.LogTrace("got data {Data} for {Id}", (object) value, id);
        _state[id] = value;

        foreach (var action in actions.Where(action => action.Trigger.ShouldTrigger(_state)))
        {
            if (!_monitor.TryGetDevice(action.Device, out var device)) continue;

            _log.LogDebug("Triggering {Id}", id);
            var (red, green, blue) = action.Color.ToLedPowers();
            device.SendCommand(action.Target.BoardType, action.Target.LedNumber, red, green, blue);
        }
    }

    private void TriggerAction(string id)
    {
        if (!_actions.TryGetValue(id, out var actions)) return; // nothing for this id, then leave

        _log.LogTrace("got trigger for {Id}", id);
        foreach (var action in actions)
        {
            if (!_monitor.TryGetDevice(action.Device, out var device)) continue;

            _log.LogDebug("Triggering {Id}", id);
            var (red, green, blue) = action.Color.ToLedPowers();
            device.SendCommand(action.Target.BoardType, action.Target.LedNumber, red, green, blue);
        }
    }

    private void ClearStateAction()
    {
        _state.Clear();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var plugin in _plugins)
        {
            plugin.Stop();
        }
    }
}
