// Improved IFacialMocapReceiver with proactive handshake (UDP) and optional TCP mode
using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace JayT.UnityAvatarTools.Facial
{
    /// <summary>
    /// iFacialMocap receiver for Unity
    /// - Supports UDP (default) by sending magic string to iPhone:49983
    /// - Supports optional TCP mode (send UDPTCP magic then listen on TCP port 49986)
    /// See official docs: https://www.ifacialmocap.com/for-developer/
    /// </summary>
    public class IFacialMocapReceiver : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField] private int receivePort = 49983; // local UDP receive port (default)
        [SerializeField] private string remoteIP = "";   // iPhone IP (set this in inspector)
        [SerializeField] private bool useTCP = false;      // if true, request TCP mode (then listen on TCP port 49986)

        [Header("Debug")]
        [SerializeField] private bool showDebugLog = false;

        [Header("Connection Status")]
        [SerializeField] private bool isConnected = false;
        [SerializeField] private string lastReceivedTime = "未接続";
        [SerializeField] private int receivedPacketCount = 0;

        private Dictionary<string, float> blendShapeValues = new Dictionary<string, float>();
        private Vector3 headPosition = Vector3.zero;
        private Quaternion headRotation = Quaternion.identity;

        // Networking
        private UdpClient udpListener;      // listens for incoming UDP frames (when using UDP)
        private UdpClient udpSender;        // used to send magic handshake to iPhone
        private Thread udpReceiveThread;
        private volatile bool isRunning = false;

        // TCP
        private TcpListener tcpListener;
        private Thread tcpAcceptThread;
        private Thread tcpReceiveThread;

        private readonly object lockObject = new object();
        private bool hasNewData = false;

        // config constants from official docs
        private const int iPhonePort = 49983;
        private const int pcTcpPort = 49986;
        private const string udpMagic = "iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719";
        private const string udptcpMagic = "iFacialMocap_UDPTCP_sahuasouryya9218sauhuiayeta91555dy3719";
        private const string tcpFrameDelimiter = "___iFacialMocap"; // used for TCP framing per docs

        void OnValidate()
        {
            // nothing heavy here; keep inspector responsive
            if (string.IsNullOrEmpty(remoteIP)) remoteIP = "";
        }

        void Start()
        {
            if (string.IsNullOrEmpty(remoteIP))
            {
                Debug.LogWarning("IFacialMocapReceiver: remoteIP is empty. Set the iPhone's IP address in the inspector.");
                return;
            }

            InitializeNetworking();
        }

        void OnDestroy()
        {
            StopNetworking();
        }

        private void InitializeNetworking()
        {
            try
            {
                udpSender = new UdpClient();

                // Start UDP listener if using UDP mode
                if (!useTCP)
                {
                    udpListener = new UdpClient(receivePort);
                    isRunning = true;
                    udpReceiveThread = new Thread(UDPReceiveLoop) { IsBackground = true };
                    udpReceiveThread.Start();

                    // proactively send magic string to iPhone to request UDP streaming
                    SendUdpMagic(udpMagic, iPhonePort);

                    Debug.Log($"IFacialMocapReceiver: UDP mode. Listening on port {receivePort}, requested iPhone {remoteIP}:{iPhonePort}");
                }
                else
                {
                    // In TCP mode: send udptcp magic to request iOS to open TCP connection back to PC:49986
                    tcpListener = new TcpListener(IPAddress.Any, pcTcpPort);
                    tcpListener.Start();

                    tcpAcceptThread = new Thread(TcpAcceptLoop) { IsBackground = true };
                    tcpAcceptThread.Start();

                    SendUdpMagic(udptcpMagic, iPhonePort);

                    Debug.Log($"IFacialMocapReceiver: TCP mode requested. TCP listening on {pcTcpPort}, sent UDPTCP request to {remoteIP}:{iPhonePort}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"IFacialMocapReceiver InitializeNetworking failed: {e.Message}");
            }
        }

        private void StopNetworking()
        {
            isRunning = false;

            try { udpReceiveThread?.Interrupt(); } catch {};
            try { tcpAcceptThread?.Interrupt(); } catch {};
            try { tcpReceiveThread?.Interrupt(); } catch {};

            udpListener?.Close();
            udpSender?.Close();

            try { tcpListener?.Stop(); } catch {};

            Debug.Log("IFacialMocapReceiver: stopped");
        }

        private void SendUdpMagic(string magic, int port)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(magic);
                udpSender.Send(b, b.Length, remoteIP, port);
                if (showDebugLog) Debug.Log($"Sent magic to {remoteIP}:{port} -> {magic}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to send magic: {e.Message}");
            }
        }

        // UDP receive loop - simple packet-per-frame
        private void UDPReceiveLoop()
        {
            IPEndPoint anyEP = new IPEndPoint(IPAddress.Any, 0);
            isRunning = true;

            while (isRunning)
            {
                try
                {
                    byte[] data = udpListener.Receive(ref anyEP);
                    string msg = Encoding.UTF8.GetString(data);

                    if (showDebugLog) Debug.Log($"UDP recv {anyEP.Address}:{anyEP.Port} len={data.Length}");

                    HandleIncomingMessage(msg);
                }
                catch (SocketException se)
                {
                    if (showDebugLog) Debug.LogWarning($"UDP socket closed or error: {se.Message}");
                    break;
                }
                catch (ThreadInterruptedException) { break; }
                catch (Exception e)
                {
                    Debug.LogError($"UDPReceiveLoop error: {e.Message}");
                }
            }
        }

        // TCP accept loop: accept one connection then spawn receive thread
        private void TcpAcceptLoop()
        {
            while (true)
            {
                try
                {
                    TcpClient client = tcpListener.AcceptTcpClient();
                    if (showDebugLog) Debug.Log($"TCP client connected from {client.Client.RemoteEndPoint}");

                    // start receive loop for this client
                    tcpReceiveThread = new Thread(() => TcpReceiveLoop(client)) { IsBackground = true };
                    tcpReceiveThread.Start();

                    break; // for simplicity accept one connection
                }
                catch (SocketException se)
                {
                    Debug.LogWarning($"TCP accept error: {se.Message}");
                    break;
                }
                catch (ThreadInterruptedException) { break; }
            }
        }

        // Read from TCP stream, split frames by delimiter
        private void TcpReceiveLoop(TcpClient client)
        {
            try
            {   var stream = client.GetStream();
                byte[] buffer = new byte[8192];
                StringBuilder sb = new StringBuilder();

                while (client.Connected)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, read);
                    sb.Append(chunk);

                    string s = sb.ToString();
                    int idx;
                    while ((idx = s.IndexOf(tcpFrameDelimiter, StringComparison.Ordinal)) >= 0)
                    {
                        string frame = s.Substring(0, idx);
                        HandleIncomingMessage(frame);

                        s = s.Substring(idx + tcpFrameDelimiter.Length);
                    }

                    sb.Clear();
                    sb.Append(s);
                }

                if (showDebugLog) Debug.Log("TCP client disconnected");
                client.Close();
            }
            catch (Exception e)
            {
                Debug.LogError($"TcpReceiveLoop error: {e.Message}");
            }
        }

        // Common handler for incoming messages (UDP single packets or TCP framed JSON/text)
        private void HandleIncomingMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // messages from iFacialMocap typically start with types like iFacialMocap_head or iFacialMocap_blendShapes
            if (message.StartsWith("iFacialMocap_request") )
            {
                // older/other implementations may send requests; respond with OK if needed
                if (showDebugLog) Debug.Log("Received request message");
                return;
            }

            // Quick sanity: update connection timestamp
            lock (lockObject)
            {
                isConnected = true;
                lastReceivedTime = DateTime.Now.ToString("HH:mm:ss");
                receivedPacketCount++;
                hasNewData = true;
            }

            // iFacialMocap UDP frames use '|' and '&' as in your original parser
            ParseMessage(message);
        }

        private void ParseMessage(string message)
        {
            var parts = message.Split('|');
            if (parts.Length == 0) return;

            string type = parts[0];

            switch (type)
            {
                case "iFacialMocap_head":
                    ParseHeadData(parts);
                    break;
                case "iFacialMocap_blendShapes":
                    ParseBlendShapeData(parts);
                    break;
                default:
                    if (showDebugLog) Debug.Log($"Unhandled message type: {type}");
                    break;
            }
        }

        private void ParseHeadData(string[] parts)
        {
            if (parts.Length < 7) return;
            if (!float.TryParse(parts[1], out float rx)) return;
            if (!float.TryParse(parts[2], out float ry)) return;
            if (!float.TryParse(parts[3], out float rz)) return;
            if (!float.TryParse(parts[4], out float x)) return;
            if (!float.TryParse(parts[5], out float y)) return;
            if (!float.TryParse(parts[6], out float z)) return;

            lock (lockObject)
            {
                headRotation = Quaternion.Euler(rx, ry, rz);
                headPosition = new Vector3(x, y, z);
            }
        }

        private void ParseBlendShapeData(string[] parts)
        {
            lock (lockObject)
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    var kv = parts[i].Split('&');
                    if (kv.Length != 2) continue;

                    if (float.TryParse(kv[1], out float v))
                        blendShapeValues[kv[0]] = v;
                }
            }
        }

        void Update()
        {
            // connection timeout check
            if (lastReceivedTime != "未接続")
            {
                if (DateTime.TryParseExact(lastReceivedTime, "HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime last))
                {
                    if ((DateTime.Now - last).TotalSeconds > 5)
                        isConnected = false;
                }
            }
        }

        #region Public API
        public float GetBlendShapeValue(string name)
        {
            lock (lockObject) { return blendShapeValues.TryGetValue(name, out float v) ? v : 0f; }
        }

        public Dictionary<string, float> GetAllBlendShapeValues()
        {
            lock (lockObject) { return new Dictionary<string, float>(blendShapeValues); }
        }

        public Vector3 GetHeadPosition() { lock (lockObject) return headPosition; }
        public Quaternion GetHeadRotation() { lock (lockObject) return headRotation; }
        public bool IsReceiving() => isRunning;
        public string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                return ip?.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }
        #endregion
    }
}
