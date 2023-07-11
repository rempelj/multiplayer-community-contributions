using System;
using System.Runtime.InteropServices;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

public class FacepunchConnectionManager : IConnectionManager
{
    private ConnectionManager connectionManager;
    private readonly FacepunchTransport facepunchTransport;
    private ulong clientId;
    private LogLevel LogLevel => NetworkManager.Singleton.LogLevel;

    public Connection Connection => connectionManager.Connection;
    public SteamId SteamId { get; }

    public FacepunchConnectionManager(FacepunchTransport facepunchTransport, SteamId steamId)
    {
        this.facepunchTransport = facepunchTransport;
        this.SteamId = steamId;
    }

    void IConnectionManager.OnConnecting(ConnectionInfo info)
    {
        if (LogLevel <= LogLevel.Developer)
        {
            Debug.Log($"[{nameof(FacepunchTransport)}] - Connecting with Steam user {info.Identity.SteamId}.");
        }
    }

    void IConnectionManager.OnConnected(ConnectionInfo info)
    {
        facepunchTransport.PublicInvokeOnTransportEvent(NetworkEvent.Connect, clientId, default,
            Time.realtimeSinceStartup);

        if (LogLevel <= LogLevel.Developer)
        {
            Debug.Log($"[{nameof(FacepunchTransport)}] - Connected with Steam user {info.Identity.SteamId}.");
        }
    }

    void IConnectionManager.OnDisconnected(ConnectionInfo info)
    {
        facepunchTransport.PublicInvokeOnTransportEvent(NetworkEvent.Disconnect, clientId, default,
            Time.realtimeSinceStartup);

        facepunchTransport.DisconnectRemoteClient(clientId);


        if (LogLevel <= LogLevel.Developer)
        {
            Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnected Steam user {info.Identity.SteamId}.");
        }
    }

    void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        var payload = new byte[size];
        Marshal.Copy(data, payload, 0, size);
        // PABC - Change "size - 1" to "size" to fix error: "[Netcode] Received a message that claimed a size larger than the packet, ending early!"
        facepunchTransport.PublicInvokeOnTransportEvent(NetworkEvent.Data, clientId,
            new ArraySegment<byte>(payload, 0, size), Time.realtimeSinceStartup);
    }

    public void Close()
    {
        connectionManager?.Close();
    }

    public void Receive()
    {
        connectionManager?.Receive();
    }

    public void Connect()
    {
        connectionManager = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(SteamId);
        connectionManager.Interface = this;
        clientId = connectionManager.Connection.Id;
    }
}