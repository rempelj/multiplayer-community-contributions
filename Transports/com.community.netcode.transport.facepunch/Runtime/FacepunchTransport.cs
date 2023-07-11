using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

namespace Netcode.Transports.Facepunch
{
    using SocketConnection = Connection;

    public class FacepunchTransport : NetworkTransport, IConnectionManager, ISocketManager
    {
        private class Client
        {
            public SteamId steamId;
            public SocketConnection connection;
        }

        private SocketManager socketManager;
        private Dictionary<ulong, FacepunchConnectionManager> connectionManagers;
        private Dictionary<ulong, Client> connectedClients;

        [Space]
        [Tooltip(
            "The Steam App ID of your game. Technically you're not allowed to use 480, but Valve doesn't do anything about it so it's fine for testing purposes.")]
        [SerializeField]
        private uint steamAppId = 480;

        [Tooltip("The Steam ID of the user targeted when joining as a client.")] [SerializeField]
        public ulong targetSteamId;

        [Header("Info")] [ReadOnly] [Tooltip("When in play mode, this will display your Steam ID.")] [SerializeField]
        private ulong userSteamId;

        private LogLevel LogLevel => NetworkManager.Singleton.LogLevel;

        #region Utility Methods

        private IEnumerator InitSteamworks()
        {
            yield return new WaitUntil(() => SteamClient.IsValid);

            SteamNetworkingUtils.InitRelayNetworkAccess();

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Initialized access to Steam Relay Network.");
            }

            userSteamId = SteamClient.SteamId;

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Fetched user Steam ID.");
            }
        }

        #endregion

        #region MonoBehaviour Messages

        private void Awake()
        {
            try
            {
                SteamClient.Init(steamAppId, false);
            }
            catch (Exception e)
            {
                if (LogLevel <= LogLevel.Error)
                {
                    Debug.LogError(
                        $"[{nameof(FacepunchTransport)}] - Caught an exeption during initialization of Steam client: {e}");
                }
            }
            finally
            {
                StartCoroutine(InitSteamworks());
            }
        }

        private void Update()
        {
            SteamClient.RunCallbacks();
        }

        private void OnDestroy()
        {
            SteamClient.Shutdown();
        }

        #endregion

        #region NetworkTransport Overrides

        public override ulong ServerClientId => 0;

        public override void DisconnectLocalClient()
        {
            foreach (var connectionManager in connectionManagers.Values)
            {
                connectionManager.Connection.Close();
            }

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnecting local client.");
            }
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (connectedClients.TryGetValue(clientId, out var user))
            {
                user.connection.Close();
                connectedClients.Remove(clientId);

                if (LogLevel <= LogLevel.Developer)
                {
                    Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnecting remote client with ID {clientId}.");
                }
            }
            else if (LogLevel <= LogLevel.Normal)
            {
                Debug.LogWarning(
                    $"[{nameof(FacepunchTransport)}] - Failed to disconnect remote client with ID {clientId}, client not connected.");
            }
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            return 0;
        }

        public override void Initialize()
        {
            connectionManagers = new Dictionary<ulong, FacepunchConnectionManager>();
            connectedClients = new Dictionary<ulong, Client>();
        }

        private SendType NetworkDeliveryToSendType(NetworkDelivery delivery)
        {
            return delivery switch
            {
                NetworkDelivery.Reliable => SendType.Reliable,
                NetworkDelivery.ReliableFragmentedSequenced => SendType.Reliable,
                NetworkDelivery.ReliableSequenced => SendType.Reliable,
                NetworkDelivery.Unreliable => SendType.Unreliable,
                NetworkDelivery.UnreliableSequenced => SendType.Unreliable,
                _ => SendType.Reliable
            };
        }

        public override void Shutdown()
        {
            try
            {
                if (LogLevel <= LogLevel.Developer)
                {
                    Debug.Log($"[{nameof(FacepunchTransport)}] - Shutting down.");
                }

                foreach (var connectionManager in connectionManagers.Values)
                {
                    connectionManager.Close();
                }

                connectionManagers.Clear();
                socketManager?.Close();
            }
            catch (Exception e)
            {
                if (LogLevel <= LogLevel.Error)
                {
                    Debug.LogError($"[{nameof(FacepunchTransport)}] - Caught an exception while shutting down: {e}");
                }
            }
        }

        public override unsafe void Send(ulong clientId, ArraySegment<byte> data, NetworkDelivery delivery)
        {
            var sendType = NetworkDeliveryToSendType(delivery);

            var buffer = stackalloc byte[data.Count];
            fixed (byte* pointer = data.Array)
            {
                Buffer.MemoryCopy(pointer + data.Offset, buffer, data.Count, data.Count);
            }

            if (clientId == ServerClientId)
            {
                connectionManagers[ServerClientId]?.Connection.SendMessage((IntPtr) buffer, data.Count, sendType);
            }
            else if (connectedClients.TryGetValue(clientId, out var client))
            {
                client.connection.SendMessage((IntPtr) buffer, data.Count, sendType);
            }
            else if (connectionManagers.TryGetValue(clientId, out var transport))
            {
                transport.Connection.SendMessage((IntPtr) buffer, data.Count, sendType);
            }
            else if (LogLevel <= LogLevel.Normal)
            {
                Debug.LogWarning(
                    $"[{nameof(FacepunchTransport)}] - Failed to send packet to remote client with ID {clientId}, client not connected.");
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload,
            out float receiveTime)
        {
            foreach (var connectionManager in connectionManagers.Values)
            {
                connectionManager.Receive();
            }

            socketManager?.Receive();

            clientId = 0;
            receiveTime = Time.realtimeSinceStartup;
            payload = default;
            return NetworkEvent.Nothing;
        }

        public override bool StartClient()
        {
            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Starting as client.");
            }

            var conn = new FacepunchConnectionManager(this, targetSteamId);
            connectionManagers.Add(ServerClientId, conn);
            conn.Connect();

            return true;
        }

        public override bool StartServer()
        {
            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Starting as server.");
            }

            socketManager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>();
            socketManager.Interface = this;
            return true;
        }

        #endregion

        #region ConnectionManager Implementation

        void IConnectionManager.OnConnecting(ConnectionInfo info)
        {
            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Connecting with Steam user {info.Identity.SteamId}.");
            }
        }

        void IConnectionManager.OnConnected(ConnectionInfo info)
        {
            InvokeOnTransportEvent(NetworkEvent.Connect, GetClientId(info.Identity.SteamId), default,
                Time.realtimeSinceStartup);

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Connected with Steam user {info.Identity.SteamId}.");
            }
        }

        void IConnectionManager.OnDisconnected(ConnectionInfo info)
        {
            InvokeOnTransportEvent(NetworkEvent.Disconnect, GetClientId(info.Identity.SteamId), default,
                Time.realtimeSinceStartup);

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnected Steam user {info.Identity.SteamId}.");
            }
        }

        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Debug.Log("IConnectionManager.OnMessage");
            var payload = new byte[size];
            Marshal.Copy(data, payload, 0, size);
            // Rempelj - Change "size - 1" to "size" to fix error: "[Netcode] Received a message that claimed a size larger than the packet, ending early!"
            InvokeOnTransportEvent(NetworkEvent.Data, ServerClientId, new ArraySegment<byte>(payload, 0, size),
                Time.realtimeSinceStartup);
        }

        public void PublicInvokeOnTransportEvent(NetworkEvent eventType, ulong clientId, ArraySegment<byte> payload,
            float receiveTime)
        {
            InvokeOnTransportEvent(eventType, clientId, payload, receiveTime);
        }

        #endregion

        #region SocketManager Implementation

        void ISocketManager.OnConnecting(SocketConnection connection, ConnectionInfo info)
        {
            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log(
                    $"[{nameof(FacepunchTransport)}] - Accepting connection from Steam user {info.Identity.SteamId}.");
            }

            connection.Accept();
        }

        void ISocketManager.OnConnected(SocketConnection connection, ConnectionInfo info)
        {
            if (!connectedClients.ContainsKey(connection.Id))
            {
                connectedClients.Add(connection.Id, new Client
                {
                    connection = connection,
                    steamId = info.Identity.SteamId
                });

                InvokeOnTransportEvent(NetworkEvent.Connect, connection.Id, default, Time.realtimeSinceStartup);

                if (LogLevel <= LogLevel.Developer)
                {
                    Debug.Log($"[{nameof(FacepunchTransport)}] - Connected with Steam user {info.Identity.SteamId}.");
                }
            }
            else if (LogLevel <= LogLevel.Normal)
            {
                Debug.LogWarning(
                    $"[{nameof(FacepunchTransport)}] - Failed to connect client with ID {connection.Id}, client already connected.");
            }
        }

        void ISocketManager.OnDisconnected(SocketConnection connection, ConnectionInfo info)
        {
            connectedClients.Remove(connection.Id);

            InvokeOnTransportEvent(NetworkEvent.Disconnect, connection.Id, default, Time.realtimeSinceStartup);

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnected Steam user {info.Identity.SteamId}");
            }
        }

        void ISocketManager.OnMessage(SocketConnection connection, NetIdentity identity, IntPtr data, int size,
            long messageNum, long recvTime, int channel)
        {
            var payload = new byte[size];
            Marshal.Copy(data, payload, 0, size);
            InvokeOnTransportEvent(NetworkEvent.Data, connection.Id, new ArraySegment<byte>(payload, 0, size),
                Time.realtimeSinceStartup);
        }

        #endregion

        #region Hostless Peer

        public ulong ConnectHostlessPeer(SteamId steamId)
        {
            var hostlessConnection = new FacepunchConnectionManager(this, steamId);
            hostlessConnection.Connect();
            var connection = hostlessConnection.Connection;
            ulong transportId = connection.Id;

            connectionManagers.Add(transportId, hostlessConnection);
            /*
            connectedClients.Add(clientId, new Client
            {
                connection = connection,
                steamId = steamId
            });
            */

            Debug.Log($"FacepunchTransport.ConnectHostlessPeer - steamId: {steamId}, transportId: {transportId}");

            return transportId;
        }

        public void SetSteamAppID(uint id)
        {
            steamAppId = id;
        }

        public SteamId GetSteamId(ulong clientId)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId)
            {
                return userSteamId;
            }

            if (clientId == ServerClientId)
            {
                return targetSteamId;
            }

            if (connectedClients.TryGetValue(clientId, out var client))
            {
                return client.steamId;
            }

            if (connectionManagers.TryGetValue(clientId, out var transport))
            {
                return transport.SteamId;
            }

            return new SteamId();
        }

        public ulong GetClientId(SteamId steamId)
        {
            if (!steamId.IsValid)
            {
                Debug.LogError("Invalid SteamId");
                return ulong.MaxValue;
            }

            if (steamId == userSteamId)
            {
                return NetworkManager.Singleton.LocalClientId;
            }

            if (steamId == targetSteamId)
            {
                return ServerClientId;
            }

            foreach (var client in connectedClients)
            {
                if (steamId == client.Value.steamId)
                {
                    return client.Key;
                }
            }

            Debug.LogError($"Failed to get clientId for steamID {steamId}");
            return ulong.MaxValue;
        }

        public ulong GetTransportId(SteamId steamId)
        {
            if (!steamId.IsValid)
            {
                Debug.LogError("Invalid SteamId");
                return ulong.MaxValue;
            }

            if (steamId == userSteamId)
            {
                return NetworkManager.Singleton.LocalClientId;
            }

            if (steamId == targetSteamId)
            {
                return ServerClientId;
            }

            foreach (var client in connectionManagers)
            {
                if (steamId == client.Value.SteamId)
                {
                    return client.Key;
                }
            }

            Debug.LogError($"Failed to get transportId for steamID {steamId}");
            return ulong.MaxValue;
        }

        #endregion
    }
}