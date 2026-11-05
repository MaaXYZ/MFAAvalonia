using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// 多实例设备独占分配：一个 ADB serial 只允许绑定到一个实例，避免多个 Tab 抢同一台模拟器。
/// </summary>
public static class DeviceAssignmentRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, string> SerialToInstance = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> InstanceToSerial = new(StringComparer.OrdinalIgnoreCase);

    public static void Assign(string instanceId, string? serial)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        lock (SyncRoot)
        {
            if (InstanceToSerial.TryGetValue(instanceId, out var previous))
            {
                InstanceToSerial.Remove(instanceId);
                if (SerialToInstance.TryGetValue(previous, out var owner) && owner == instanceId)
                    SerialToInstance.Remove(previous);
            }

            if (string.IsNullOrWhiteSpace(serial))
                return;

            InstanceToSerial[instanceId] = serial;
            SerialToInstance[serial] = instanceId;
        }
    }

    public static void Release(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        lock (SyncRoot)
        {
            if (!InstanceToSerial.TryGetValue(instanceId, out var serial))
                return;

            InstanceToSerial.Remove(instanceId);
            if (SerialToInstance.TryGetValue(serial, out var owner) && owner == instanceId)
                SerialToInstance.Remove(serial);
        }
    }

    public static bool IsAssignedToOther(string? serial, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return false;

        lock (SyncRoot)
        {
            return SerialToInstance.TryGetValue(serial, out var owner)
                && !string.Equals(owner, instanceId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string? GetAssignedSerial(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return null;

        lock (SyncRoot)
        {
            return InstanceToSerial.TryGetValue(instanceId, out var serial) ? serial : null;
        }
    }
}
