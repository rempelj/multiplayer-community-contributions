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

    public class FacepunchTransport : NetworkTransport, ISocketManager
    {
        private class Client
        {
            public SteamId steamId;
            public SocketConnection connection;
        }

        private SocketManager socketManager;

        // Because in "hostless peer" games, its possible to be both client and host. "out" connections are like hosts. "in" connections are like clients.
        private Dictionary<ulong, FacepunchConnectionManager> outConnections;
        private Dictionary<ulong, Client> inConnections;

        [Space]
        [Tooltip(
            "The Steam App ID of your game. Technically you're not allowed to use 480, but Valve doesn't do anything about it so it's fine for testing purposes.")]
        [SerializeField]
        private uint steamAppId = 480;

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

        #region ConnectionManager Implementation

        public void PublicInvokeOnTransportEvent(NetworkEvent eventType, ulong clientId, ArraySegment<byte> payload,
            float receiveTime)
        {
            InvokeOnTransportEvent(eventType, clientId, payload, receiveTime);
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
            foreach (var connectionManager in outConnections.Values)
            {
                connectionManager.Connection.Close();
            }

            foreach (var client in inConnections.Values)
            {
                client.connection.Close();
            }

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnecting local client.");
            }
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (inConnections.TryGetValue(clientId, out var user))
            {
                user.connection.Close();
                inConnections.Remove(clientId);

                if (LogLevel <= LogLevel.Developer)
                {
                    Debug.Log($"[{nameof(FacepunchTransport)}] - Disconnecting remote client with ID {clientId}.");
                }
            }
            else if (outConnections.TryGetValue(clientId, out var host))
            {
                host.Connection.Close();
                outConnections.Remove(clientId);

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
            outConnections = new Dictionary<ulong, FacepunchConnectionManager>();
            inConnections = new Dictionary<ulong, Client>();
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

                foreach (var connectionManager in outConnections.Values)
                {
                    connectionManager.Close();
                }

                foreach (var client in inConnections.Values)
                {
                    client.connection.Close();
                }

                inConnections.Clear();
                outConnections.Clear();
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

            if (inConnections.TryGetValue(clientId, out var client))
            {
                client.connection.SendMessage((IntPtr) buffer, data.Count, sendType);
            }
            else if (outConnections.TryGetValue(clientId, out var transport))
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
            foreach (var connectionManager in outConnections.Values)
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
            throw new NotImplementedException("Not implemented for hostless peer");
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

        #region SocketManager Implementation

        void ISocketManager.OnConnecting(SocketConnection connection, ConnectionInfo info)
        {
            var result = connection.Accept();

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log(
                    $"[{nameof(FacepunchTransport)}] - Accepting connection from SteamId:{info.Identity.SteamId}. ({result})");
            }
        }

        void ISocketManager.OnConnected(SocketConnection connection, ConnectionInfo info)
        {
            if (!inConnections.ContainsKey(connection.Id))
            {
                inConnections.Add(connection.Id, new Client
                {
                    connection = connection,
                    steamId = info.Identity.SteamId
                });

                InvokeOnTransportEvent(NetworkEvent.Connect, connection.Id, default, Time.realtimeSinceStartup);

                if (LogLevel <= LogLevel.Developer)
                {
                    Debug.Log(
                        $"[{nameof(FacepunchTransport)}] - Connected with user SteamId:{info.Identity.SteamId}, ConnectionId:{connection.Id}. ({info.State})");
                }
            }
            else if (LogLevel <= LogLevel.Normal)
            {
                Debug.LogWarning(
                    $"[{nameof(FacepunchTransport)}] - Failed to connect client SteamId:{info.Identity.SteamId}, ConnectionId:{connection.Id}, client already connected. ({info.State})");
            }
        }

        void ISocketManager.OnDisconnected(SocketConnection connection, ConnectionInfo info)
        {
            inConnections.Remove(connection.Id);

            InvokeOnTransportEvent(NetworkEvent.Disconnect, connection.Id, default, Time.realtimeSinceStartup);

            if (LogLevel <= LogLevel.Developer)
            {
                Debug.Log(
                    $"[{nameof(FacepunchTransport)}] - Disconnected user SteamId:{info.Identity.SteamId}, ConnectionId:{connection.Id} ({info.State} - {info.EndReason})");
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

        public ulong RequestConnection(SteamId steamId)
        {
            var hostlessConnection = new FacepunchConnectionManager(this, steamId);
            hostlessConnection.Connect();
            var connection = hostlessConnection.Connection;
            ulong clientId = connection.Id;

            outConnections.Add(clientId, hostlessConnection);

            Debug.Log($"FacepunchTransport.ConnectHostlessPeer - SteamId:{steamId}, ClientId:{clientId}");

            return clientId;
        }


        public void SetSteamAppID(uint id)
        {
            steamAppId = id;
        }

        public SteamId GetSteamIdForConnection(ulong clientId)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId)
            {
                return userSteamId;
            }

            if (inConnections.TryGetValue(clientId, out var client))
            {
                return client.steamId;
            }

            if (outConnections.TryGetValue(clientId, out var host))
            {
                return host.SteamId;
            }

            return new SteamId();
        }

        public SteamId GetSteamIdForAccount(ulong accountId)
        {
            if (accountId == SteamClient.SteamId.AccountId)
            {
                return userSteamId;
            }

            foreach (var client in inConnections)
            {
                if (accountId == client.Value.steamId.AccountId)
                {
                    return client.Value.steamId;
                }
            }

            foreach (var transport in outConnections)
            {
                if (accountId == transport.Value.SteamId.AccountId)
                {
                    return transport.Value.SteamId;
                }
            }

            return new SteamId();
        }

        public ulong GetClientId(SteamId steamId)
        {
            if (steamId == userSteamId)
            {
                return NetworkManager.Singleton.LocalClientId;
            }

            foreach (var client in inConnections)
            {
                if (steamId == client.Value.steamId)
                {
                    return client.Key;
                }
            }

            foreach (var client in outConnections)
            {
                if (steamId == client.Value.SteamId)
                {
                    return client.Key;
                }
            }

            //  Debug.LogError($"Failed to get clientId for steamID {steamId}");
            return ulong.MaxValue;
        }


        public string GetConnectionStatus(SteamId steamId)
        {
            foreach (var transport in outConnections)
            {
                if (steamId == transport.Value.SteamId)
                {
                    return transport.Value.ConnectionInfo.State.ToString();
                }
            }

            foreach (var transport in inConnections)
            {
                if (steamId == transport.Value?.steamId)
                {
                    return "Connected";
                }
            }

            return "NoConnection";
        }

        #endregion
    }
}