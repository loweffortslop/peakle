using System.Collections.Generic;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace Peakle;

/// <summary>A gameplay setting: in someone else's lobby, the host's value counts, not your own config.</summary>
internal class HostSetting
{
    public readonly ConfigEntry<float> Entry;
    public readonly int Index;

    public HostSetting(ConfigEntry<float> entry, int index)
    {
        Entry = entry;
        Index = index;
    }

    public float Value => HostSettings.Get(this);
}

/// <summary>
/// Gameplay settings (feel, slides, dragging scouts) are the host's: the master client puts its values in the Photon
/// room's properties, and everyone else uses those while in the room. Only looks (HUD, visuals) and controls stay
/// your own. Without this, anyone could give themselves easier climbing from their own config.
/// </summary>
internal static class HostSettings
{
    private const string RoomKey = "peakle.settings";

    private static readonly List<HostSetting> all = new();
    private static float[] host;     // the host's values, while in someone else's room
    private static float[] published; // what this client last published as master
    private static float nextCheck;

    public static HostSetting Add(ConfigEntry<float> entry)
    {
        HostSetting setting = new(entry, all.Count);
        all.Add(setting);
        return setting;
    }

    public static float Get(HostSetting setting)
    {
        float[] values = host;
        return values != null && setting.Index < values.Length ? values[setting.Index] : setting.Entry.Value;
    }

    /// <summary>Every frame: publish (as master) or pick up (as a client) the room's settings.</summary>
    public static void Update()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null)
        {
            host = null;
            published = null;
            return;
        }
        if (Time.unscaledTime < nextCheck)
        {
            return;
        }
        nextCheck = Time.unscaledTime + 0.5f;
        if (PhotonNetwork.IsMasterClient)
        {
            host = null;
            float[] mine = new float[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                mine[i] = all[i].Entry.Value;
            }
            object current = PhotonNetwork.CurrentRoom.CustomProperties[RoomKey];
            if (published == null || !Same(mine, published) || current is not float[])
            {
                PhotonNetwork.CurrentRoom.SetCustomProperties(new PhotonHashtable { { RoomKey, mine } });
                published = mine;
                Plugin.Log.LogInfo("Host settings published to the lobby");
            }
        }
        else
        {
            published = null;
            float[] values = PhotonNetwork.CurrentRoom.CustomProperties[RoomKey] as float[];
            if (values != null && (host == null || !Same(values, host)))
            {
                Plugin.Log.LogInfo("Using the host's gameplay settings");
            }
            host = values;
        }
    }

    private static bool Same(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (!Mathf.Approximately(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
