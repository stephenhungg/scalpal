// Streams each frame's joints to the Mac over UDP (one JSON datagram per frame, about 2 KB) so the
// simulated robot hand can mirror the learner live (services/hands: `uv run python live.py`).
// Showing the robot mirror is in-app use; saving joints for training goes through HandEpisodeRecorder's consent gate.
#if SCALPAL_HANDS
using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Scalpal.Hands
{
    [DisallowMultipleComponent]
    public sealed class HandStreamer : MonoBehaviour
    {
        [SerializeField] BlazeHandTracker tracker;
        [Tooltip("The Mac's LAN address. Same network as the headset.")]
        [SerializeField] string host = "192.168.1.20";
        [SerializeField] int port = 9123;
        [SerializeField] bool streaming = true;
        [Tooltip("Cap on datagrams per second.")]
        [SerializeField] float maxRate = 30f;

        UdpClient client;
        double lastSent;

        public bool Streaming { get => streaming; set => streaming = value; }
        public int Sent { get; private set; }

        void OnEnable()
        {
            if (tracker != null) tracker.FrameReady += OnFrame;
            try
            {
                client = new UdpClient();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scalpal.Hands] UDP unavailable: " + e.Message, this);
            }
        }

        void OnDisable()
        {
            if (tracker != null) tracker.FrameReady -= OnFrame;
            client?.Close();
            client = null;
        }

        void OnFrame(HandJointsFrame frame)
        {
            if (!streaming || client == null || frame.unityTime - lastSent < 1.0 / maxRate) return;
            lastSent = frame.unityTime;
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(frame));
            try
            {
                client.Send(bytes, bytes.Length, host, port);
                Sent++;
            }
            catch (Exception e)
            {
                // A missing Mac never stalls tracking; the overlay keeps working.
                Debug.LogWarning("[Scalpal.Hands] stream send failed: " + e.Message, this);
                streaming = false;
            }
        }
    }
}
#endif
