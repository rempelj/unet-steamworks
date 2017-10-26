﻿using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Net;
using UnityEngine.Networking.NetworkSystem;
using System;
using Steamworks;

namespace UNETSteamworks
{
    public class NetworkManager : MonoBehaviour
    {
        const short SpawnMsg = 1002;

        public enum SessionConnectionState
        {
            UNDEFINED,
            CONNECTING,
            CANCELLED,
            CONNECTED,
            FAILED,
            DISCONNECTING,
            DISCONNECTED
        }

        public static NetworkManager Instance;

        // inspector vars
        public GameObject playerPrefab;

        // unet vars
        public NetworkClient myClient { get; private set;}

        private Dictionary<ulong, NetworkConnection> steamIdUnetConnectionMap = new Dictionary<ulong, NetworkConnection>();
        
        // steam state vars
        CSteamID steamLobbyId;
        public bool JoinFriendTriggered { get; private set; }
        public SessionConnectionState lobbyConnectionState {get; private set;}
        private bool p2pConnectionEstablished = false; 

        // callbacks
        private Callback<LobbyEnter_t> m_LobbyEntered;
        private Callback<P2PSessionRequest_t> m_P2PSessionRequested;
        private Callback<GameLobbyJoinRequested_t> m_GameLobbyJoinRequested;


        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);

            ClientScene.RegisterPrefab(playerPrefab);

            if (SteamManager.Initialized) {
                m_LobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
                m_P2PSessionRequested = Callback<P2PSessionRequest_t>.Create (OnP2PSessionRequested);
                m_GameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create (OnGameLobbyJoinRequested);

            }
           
            LogFilter.currentLogLevel = 0;
        }

        void Start()
        {
            string[] args = System.Environment.GetCommandLineArgs ();
            string input = "";
            for (int i = 0; i < args.Length; i++) {
                if (args [i] == "+connect_lobby" && args.Length > i+1) {
                    input = args [i + 1];
                }
            }

            if (!string.IsNullOrEmpty(input))
            {
                // invite accepted and launched game. join friend's game
                ulong lobbyId = 0;

                if (ulong.TryParse(input, out lobbyId))
                {
                    JoinFriendTriggered = true;
                    steamLobbyId = new CSteamID(lobbyId);
                    JoinFriendLobby();
                }

            }
        }

        void Update()
        {
            if (!SteamManager.Initialized)
            {
                return;
            }


            if (!p2pConnectionEstablished)
            {
                return;
            }

            if (myClient == null || !myClient.isConnected)
            {
                return;
            }

            uint packetSize;

            if (SteamNetworking.IsP2PPacketAvailable (out packetSize))
            {
                byte[] data = new byte[packetSize];

                CSteamID senderId;

                if (SteamNetworking.ReadP2PPacket (data, packetSize, out packetSize, out senderId)) 
                {
                    Debug.LogError("message received (" + packetSize + "): " + System.Text.Encoding.Default.GetString(data));


                    /*
                    var sender = GetUnetConnectionForSteamUser(senderId);
                    if (sender != null)
                    {
                        sender.TransportReceive(data, Convert.ToInt32(packetSize), 0);
                    }
                    */

                    myClient.connection.TransportReceive(data, Convert.ToInt32(packetSize), 0);

                }
            }


        }

        HostTopology CreateTopology()
        {
            ConnectionConfig config = new ConnectionConfig();
            config.AddChannel(QosType.ReliableSequenced);
            return new HostTopology(config, 2);
        }
            
        public void Disconnect()
        {
            
            lobbyConnectionState = SessionConnectionState.DISCONNECTED;

            if (NetworkServer.active)
            {
                NetworkServer.Shutdown();
            }

            if (myClient != null)
            {
                myClient.Disconnect();
            }

            steamIdUnetConnectionMap.Clear();
            steamLobbyId.Clear();
            p2pConnectionEstablished = false;
        }


        public void JoinFriendLobby()
        {
            if (!SteamManager.Initialized) {
                lobbyConnectionState = SessionConnectionState.FAILED;
                return;
            }

            lobbyConnectionState = SessionConnectionState.CONNECTING;
            SteamMatchmaking.JoinLobby(steamLobbyId);
            // ...continued in OnLobbyEntered callback
        }

        public void CreateLobbyAndInviteFriend()
        {
            if (!SteamManager.Initialized) {
                lobbyConnectionState = SessionConnectionState.FAILED;
                return;
            }

            lobbyConnectionState = SessionConnectionState.CONNECTING;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, 2);
            // ...continued in OnLobbyEntered callback
        }

        void OnLobbyEntered(LobbyEnter_t pCallback)
        {
            if (!SteamManager.Initialized) {
                lobbyConnectionState = SessionConnectionState.FAILED;
                return;
            }

            steamLobbyId = new CSteamID(pCallback.m_ulSteamIDLobby);

            Debug.LogError("Connected to lobby");
            lobbyConnectionState = SessionConnectionState.CONNECTED;

            var host = SteamMatchmaking.GetLobbyOwner(steamLobbyId);
            var me = SteamUser.GetSteamID();
            if (host.m_SteamID == me.m_SteamID)
            {
                // lobby created. start UNET server
                StartUnetServerForSteam();

                // prompt to invite friend
                StartCoroutine (DoShowInviteDialogWhenReady ());
            }
            else
            {
                // joined friend's lobby.
                JoinFriendTriggered = false;

                Debug.LogError("Sending packet to request p2p connection");

                //send packet to request connection to host via Steam's NAT punch or relay servers
                SteamNetworking.SendP2PPacket (host, null, 0, EP2PSend.k_EP2PSendReliable);

                StartCoroutine (DoWaitForP2PSessionAcceptedAndConnect ());

            }


        }

        public NetworkConnection GetUnetConnectionForSteamUser(CSteamID userId)
        {
            NetworkConnection result;

            if (steamIdUnetConnectionMap.TryGetValue(userId.m_SteamID, out result))
            {
                return result;
            }

            Debug.LogError("Failed to find steam user id");
            return null;
        }

        #region host
        IEnumerator DoShowInviteDialogWhenReady()
        {
            Debug.LogError("Waiting for unet server to start");

            while (!NetworkServer.active) 
            {
                // wait for unet server to start up
                yield return null;
            }

            Debug.LogError("Showing invite friend dialog");
            SteamFriends.ActivateGameOverlayInviteDialog(steamLobbyId);

            yield break;
        }


        void OnP2PSessionRequested(P2PSessionRequest_t pCallback)
        {
            Debug.LogError("P2P session request received");

            if (NetworkServer.active && SteamManager.Initialized) 
            {
                // accept the connection if this user is in the lobby
                int numMembers = SteamMatchmaking.GetNumLobbyMembers(steamLobbyId);

                for (int i = 0; i < numMembers; i++) 
                {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex (steamLobbyId, i);

                    if (member.m_SteamID == pCallback.m_steamIDRemote.m_SteamID)
                    {
                        Debug.LogError("Sending P2P acceptance message");
                        p2pConnectionEstablished = true;

                        SteamNetworking.AcceptP2PSessionWithUser (pCallback.m_steamIDRemote);
                        SteamNetworking.SendP2PPacket (pCallback.m_steamIDRemote, null, 0, EP2PSend.k_EP2PSendReliable);

                        // create new connnection and client and connect them to server
                        var conn = new SteamNetworkConnection(member, CreateTopology());
                        steamIdUnetConnectionMap[member.m_SteamID] = conn;

                        NetworkServer.AddExternalConnection(conn);


                        return;
                    }
                }
            }

        }


        void StartUnetServerForSteam()
        {
            Debug.LogError("Starting unet server");

            var t = CreateTopology();

            NetworkServer.RegisterHandler(SpawnMsg, OnSpawnRequested);

            NetworkServer.Configure(t);
            NetworkServer.dontListen = true;
            NetworkServer.Listen(4444);

            // create a connection to represent the server
            myClient = ClientScene.ConnectLocalServer();
            myClient.Configure(t);
            steamIdUnetConnectionMap[SteamUser.GetSteamID().m_SteamID] = myClient.connection;

        

            // spawn self
            var myConn = NetworkServer.connections[0];
            NetworkServer.SetClientReady(myConn);
            var myplayer = GameObject.Instantiate(playerPrefab);
            NetworkServer.SpawnWithClientAuthority(myplayer, myConn);
        }

        void OnSpawnRequested(NetworkMessage msg)
        {
            Debug.LogError("Spawn request received");

            // spawn peer
            var steamId = new CSteamID(ulong.Parse(msg.ReadMessage<StringMessage>().value));
            var conn = GetUnetConnectionForSteamUser(steamId);

            if (conn != null)
            {
                NetworkServer.SetClientReady(conn);
                var player = GameObject.Instantiate(playerPrefab);

                bool spawned = NetworkServer.SpawnWithClientAuthority(player, conn);
                Debug.LogError(spawned ? "Spawned player" :"Failed to spawn player");
            }
        }

        #endregion

        #region client
        IEnumerator DoWaitForP2PSessionAcceptedAndConnect()
        {
            Debug.LogError("Waiting for P2P acceptance message");

            uint packetSize;
            while (!SteamNetworking.IsP2PPacketAvailable (out packetSize)) {
                yield return null;
            }

            byte[] data = new byte[packetSize];

            CSteamID senderId;

            if (SteamNetworking.ReadP2PPacket (data, packetSize, out packetSize, out senderId)) 
            {
                var host = SteamMatchmaking.GetLobbyOwner (steamLobbyId);
                if (senderId.m_SteamID == host.m_SteamID)
                {
                    Debug.LogError("P2P connection accepted");
                    p2pConnectionEstablished = true;

                    // packet was from host, assume it's notifying client that AcceptP2PSessionWithUser was called
                    P2PSessionState_t sessionState;
                    if (SteamNetworking.GetP2PSessionState (host, out sessionState)) 
                    {
                        // connect to the unet server
                        ConnectToUnetServerForSteam(host);

                        yield break;
                    }

                }
            }

            Debug.LogError("Connection failed");
        }

        void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t pCallback)
        {
            // Invite accepted while game running
            JoinFriendTriggered = true;
            steamLobbyId = pCallback.m_steamIDLobby;
            JoinFriendLobby();
        }

        void ConnectToUnetServerForSteam(CSteamID hostSteamId)
        {
            Debug.LogError("Connecting to Unet server");

            var t = CreateTopology();

            var conn = new SteamNetworkConnection(hostSteamId, t);
            steamIdUnetConnectionMap[hostSteamId.m_SteamID] = conn;

            var steamClient = new SteamNetworkClient(conn);
            steamClient.RegisterHandler(MsgType.Connect, OnConnect);

            this.myClient = steamClient;

            steamClient.SetNetworkConnectionClass<SteamNetworkConnection>();
            steamClient.Configure(t);
            steamClient.Connect();

        }

        void OnConnect(NetworkMessage msg)
        {
            Debug.LogError("Connected to unet server.");
            myClient.UnregisterHandler(MsgType.Connect);

            var conn = myClient.connection as SteamNetworkConnection;

            if (conn != null)
            {
                Debug.LogError("Requesting spawn");
                myClient.Send(SpawnMsg, new StringMessage(SteamUser.GetSteamID().m_SteamID.ToString()));
            }


        }
        #endregion
    }
}
