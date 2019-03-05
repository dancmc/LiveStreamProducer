using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.MagicLeap;
using UnityStandardAssets.Characters.FirstPerson;


/// <summary>
/// Main drop-in script to add live streaming to any application.
/// 1. Add a second (not tagged as main) camera to scene
/// 2. Attach CameraScript to the camera
/// 3. Add BlendShader to Assets and set as blendShader attribute on CameraScript in Unity editor
/// 4. Call static method CameraScript.permissionsHandled once Camera and LAN permissions are dealt with
/// 
/// If external camera permissions denied, will render in-game objects against black background 
/// </summary>

public class CameraScript : MonoBehaviour
{
  
    private static CameraScript instance;
    
    // gameCamera is the main camera rendering objects for the user to view
    private Camera gameCamera;
    // renderCamera is a second camera shadowing gameCamera, which renders game objects
    // directly to renderRt
    private Camera renderCamera;
    // renderRt is the target rendertexture for renderCamera
    private RenderTexture renderRt;
    
    // blendShader uses the external camera texture as a base, and draws game objects from
    // renderRt on top of it
    public Shader blendShader;
    private Material material;
    
    // testRt is a black texture standing in for the external camera texture when not
    // running on headset
    private RenderTexture testRt;
    
    /*
     * Networking and network discovery objects.
     * ssdp Request and Listen threads are used for SSDP service discovery,
     * works on desktop but not on ML headsets, have been commented out
     */
    private TcpClient client = new TcpClient();
    private volatile bool socketConnected;
    private static bool externalCameraActive;
    
    private Thread discoveryThread;
    private UnityWebRequest discoveryRequest;
    private bool connectionAttemptFinished = true;
    
    private Thread ssdpRequestThread;
    private Thread ssdpListenThread;

    // indicates that a jpg frame has been sent and a new one can proceed
    private bool readyToSend = true;
    
    
    // Failed attempt to compress jpgs in a background thread
    // However encodeToJpg is a Unity method and cannot be called in a non-main thread
    private static BlockingCollection<int> sendingQueue = new BlockingCollection<int>();
    private static bool texture1Available = true;
    private static bool texture2Available = true;
    private static Texture2D videoCaptureTexture;
    private static Texture2D videoCaptureTexture2;

    // Attempt to save short snippets of video to file using built in API then transmit those
    // API latency made this unusable
    private int count = 0;
    String filepath;
    private VideoState videostate = VideoState.VIDEO_NONE;

    private bool cursorLookMode = true;
    
    // Set logging on or off (logging introduces lag)
    private const bool logEnabled = true;

    public CameraScript()
    {
        instance = this;
        Debug.unityLogger.logEnabled = logEnabled;
    }
    
    private static void MLog(String str)
    {
        Debug.unityLogger.Log(str);
    }

    private void Awake()
    {
        
        filepath = Path.Combine(Application.persistentDataPath, "vid.mp4");
        gameCamera = Camera.main;
        renderCamera = GetComponent<Camera>();
        
        // Initialise the shader
        material = new Material(blendShader);
        

        // Initialise the rendertextures and colour testRt black
        testRt = new RenderTexture(960, 540, 0);
        renderRt = new RenderTexture(960, 540, 0);

        var previous = RenderTexture.active;
        RenderTexture.active = testRt;
        var color = new Color(0f, 0f, 0f, 1f);
        GL.Clear(true, true, color);
        RenderTexture.active = previous;

        // Set renderRt as the target texture of the secondary camera
        renderCamera.targetTexture = renderRt;
        // Pass renderRt reference to shader for sampling later
        material.SetTexture("_Overlay", renderRt);

        videoCaptureTexture = new Texture2D(960, 540, TextureFormat.RGBA32, false);
        videoCaptureTexture2 = new Texture2D(960, 540, TextureFormat.RGBA32, false);
    }
    
    void Start()
    {
        // Start long running coroutine to generate frames and transmit
        StartCoroutine(nameof(RenderAndSend));
    }

  
    
    private void Update()
    {
        if (Input.GetKeyUp(KeyCode.X))
        {
            MLog("X pressed");
            if (cursorLookMode)
            {
                GameObject.Find("FPSController").GetComponent<FirstPersonController>().enabled = false;
                cursorLookMode = false;
            }
            else
            {
                GameObject.Find("FPSController").GetComponent<FirstPersonController>().enabled = true;
                cursorLookMode = true;
            }
        }
    }

    // Entry point to start streaming once permissions have been handled
    public static void permissionsHandled(bool permissionsGranted)
    {
        if (instance == null) return;
        if (permissionsGranted)
        {
            instance.enableExternalCamera();
        }
        instance.startStream();
    }


    private void enableExternalCamera()
    {
        MLog("External Camera Enabled");
        MLCamera.Start();
        MLCamera.Connect();
        MLCamera.StartPreview();
        externalCameraActive = true;
    }

    private void disableExternalCamera()
    {
        MLog("External Camera Disabled");
        MLCamera.StopPreview();
        MLCamera.Disconnect();
        MLCamera.Stop();
        externalCameraActive = false;
    }
    

    private void startStream()
    {
        instance.videostate = VideoState.VIDEO_READY;
        instance.findNewServer();
    }

    
    // manually update secondary camera to follow main camera every frame
    private void OnPreRender()
    {
        renderCamera.gameObject.transform.position = gameCamera.gameObject.transform.position;
        renderCamera.gameObject.transform.rotation = gameCamera.gameObject.transform.rotation;
    }


    // Attempt to encode a frame and transmit if possible at end of each frame
    IEnumerator RenderAndSend()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

            
            processJpg();
            
            // processVideo();
        }
    }

    
    // Discover server application address (demo assumes only one server active)     
    private void findNewServer()
    {
        MLog("Finding new server");
        
        // This makes a http call to a private server to obtain
        // a previously registered server address
        StartCoroutine(getServerAddress());

        // This section is the alternative method - automatic service
        // discovery. Works on desktop, but headsets cannot seem to 
        // send or receive UDP multicasts

//        listenThread = new Thread(listenForServiceReply);
//        listenThread.IsBackground = true;
//        listenThread.Start();
//
//        sendThread = new Thread(sendServiceRequest);
//        sendThread.IsBackground = true;
//        sendThread.Start();
    }

    
    
    // Coroutine to attempt fetching pre-registered server address at intervals 
    // Once valid address received, attempt to connect then start receiving stream
    IEnumerator getServerAddress()
    {
        
        while (!socketConnected)
        {
            discoveryRequest = UnityWebRequest.Get("https://danielchan.io/mldiscovery/get");
            yield return discoveryRequest.SendWebRequest();
              
            if(discoveryRequest.isNetworkError || discoveryRequest.isHttpError) {
                MLog(discoveryRequest.error);
            }

            var message = discoveryRequest.downloadHandler.text;
            MLog(message);
            
            var address = message.Split(':');
            var ip = address[0];
            var port = Convert.ToInt32(address[1]);
            if(ip.Equals("") || port<1) continue;

            connectionAttemptFinished = false;
            discoveryThread = new Thread(()=>connectToServerBlocking(ip, port));
            discoveryThread.IsBackground = true;
            discoveryThread.Start();
            
            yield return new WaitUntil(() => connectionAttemptFinished);
            
            MLog("Finished Connection");

            discoveryThread = null;
            
        }


    }

   
    
    
    // UDP Multicast to request service announcement
    void sendServiceRequest()
    {
        MLog("Sending Service Request");
        Thread.Sleep(2000);

        try
        {
            var udpClient = new UdpClient("239.255.255.250", 1900); 
            udpClient.EnableBroadcast = true;

            byte[] request = Encoding.UTF8.GetBytes("ml-stream-locate");
            while (!socketConnected)
            {
                MLog("Sent Service Request");
                udpClient.Send(request, request.Length);
                Thread.Sleep(3000);
            }

            udpClient.Close();
        }
        catch (Exception e)
        {
            MLog(e.Message);
        }

        ssdpRequestThread = null;
    }

    // UDP Multicast receiving to wait for service announcement containing server address
    void listenForServiceReply()
    {
        var udpClient = new UdpClient();


        IPEndPoint localEp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.ExclusiveAddressUse = false;
        udpClient.Client.Bind(localEp);

        IPAddress multicastAddress = IPAddress.Parse("239.255.255.250");
        udpClient.JoinMulticastGroup(multicastAddress);

        while (true)
        {
            byte[] data = udpClient.Receive(ref localEp);
            var message = Encoding.UTF8.GetString(data, 0, data.Length);

            MLog(message);

            if (!message.ToLower().StartsWith("ml-stream-server")) continue;


            var parts = message.Split(' ');
            if (parts.Length < 2) continue;

            var address = parts[1].Split(':');
            client.Close();
            client = new TcpClient();
           
            
            connectToServerBlocking(address[0],Convert.ToInt32(address[1]));

            if (socketConnected)
            {
                break;
            }
 

        }

        ssdpListenThread = null;
    }
    
    // Connect to server once address discovered
    void connectToServerBlocking(String ip, int port)
    {
        try
        {
            client.Close();
        }
        catch (Exception e)
        {
            MLog(e.Message);
        }
        
        try{
            client = new TcpClient();
            client.Connect(ip, port);
            var initialMessage = Encoding.UTF8.GetBytes("producer\n");
            client.GetStream().Write(initialMessage, 0, initialMessage.Length);
            var reader = new StreamReader(client.GetStream());
            var response = reader.ReadLine();
            if (response.Contains("rejected"))
            {
                client.Close();
                socketConnected = false;
            }
            else
            {
                socketConnected = true;
            }
            MLog(response);
        }
        catch (Exception e)
        {
            MLog(e.Message);
        }

        connectionAttemptFinished = true;
    }


    // Create a Jpg from a combination of an external camera frame and an in-game frame and transmit
    void processJpg()
    {
        long t = currentTime();
        if (socketConnected && readyToSend)
        {
            readyToSend = false;

            //  Create a temporary RenderTexture of the same size as the texture
            var tmp = RenderTexture.GetTemporary(
                960,
                540,
                0,
                RenderTextureFormat.Default,
                RenderTextureReadWrite.Linear);

            MLog("Blitting " + (currentTime() - t));


            // Use actual external camera frame as background if available, otherwise placeholder
            // Use shader to overlay in game frame on top of external camera 
            
            if (externalCameraActive)
            {
                Graphics.Blit(MLCamera.PreviewTexture2D, tmp, material);
            }
            else
            {
                Graphics.Blit(testRt, tmp, material);
            }

            // Set blitted renderTexture as active and read to a Texture2D
            var previous = RenderTexture.active;
            RenderTexture.active = tmp;
            MLog("Reading Pixels " + (currentTime() - t));


            videoCaptureTexture.ReadPixels(new Rect(0, 0, 960, 540), 0,
                0);
            videoCaptureTexture.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            // Texture2Ds can be encoded to jpg by Unity
            var jpgBytes = videoCaptureTexture.EncodeToJPG(40);

            
            var bytes = Encoding.UTF8.GetBytes(jpgBytes.Length + "\n");
            MLog("Starting Sending " + jpgBytes.Length);
            try
            {
                // write the size of the jpg, then the actual jpg
                instance.client.GetStream().Write(bytes, 0, bytes.Length);
                instance.client.GetStream().Write(jpgBytes, 0, jpgBytes.Length);
            }
            catch (Exception e)
            {
                MLog(e.Message);
                socketConnected = false;

                findNewServer();
            }

            MLog("Done " + jpgBytes.Length);


            readyToSend = true;
        }
    }

    // This is alternative approach of recording short videos using the Video Capture API and transmitting.
    // Unusable due to latency
    void processVideo()
    {
        if (videostate == VideoState.VIDEO_READY)
        {
            videostate = VideoState.VIDEO_STARTED;
            MLog("Starting video");
            MLCamera.StartVideoCapture(filepath);
            MLog("Started video");
            count = 0;
        }

        count++;

        if (count > 240 && videostate == VideoState.VIDEO_STARTED)
        {
            videostate = VideoState.VIDEO_ENDED;
            MLog("Stopping video");
            MLCamera.StopVideoCapture();
            MLog("Stopped video");
        }

        if (videostate == VideoState.VIDEO_ENDED)
        {
            sendVideo();
        }
    }


    void sendVideo()
    {
        if (File.Exists(filepath) && new FileInfo(filepath).Length > 1600000)
        {
            videostate = VideoState.VIDEO_SENT;
            MLog("Sending video");


            byte[] videoBytes = File.ReadAllBytes(filepath);

            MLog("Starting Sending " + videoBytes.Length);
            byte[] bytes = Encoding.UTF8.GetBytes(videoBytes.Length + "\n");
            instance.client.GetStream().Write(bytes, 0, bytes.Length);
            instance.client.GetStream().Write(videoBytes, 0, videoBytes.Length);
            MLog("Done Sending" + videoBytes.Length);
        }
        else
        {
            MLog("File does not exist yet");
        }
    }
    
    // Utility method
    private long currentTime()
    {
        return (long) (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds;
    }

    private void OnDestroy()
    {
        discoveryThread?.Interrupt();
        ssdpListenThread?.Interrupt();
        ssdpRequestThread?.Interrupt();
        
    }


    enum VideoState
    {
        VIDEO_NONE,
        VIDEO_READY,
        VIDEO_STARTED,
        VIDEO_ENDED,
        VIDEO_SENT
    }


    // Code attempting to multithread JPG encoding with a queue
    // Failed due to only being able to call EncodeToJPG on main thread
    
//	public void SendingThread()
//	{
//		
//		// This section should go into a looping coroutine to juggle ready textures
//      // Also copy and paste code from processJpg to read pixels into texture
//      // =========================================================
//		Texture2D texture;
//		int textureID;
//		if (texture1Available)
//		{
//			texture = videoCaptureTexture;
//			texture1Available = false;
//			textureID = 1;
//          sendingQueue.Add(1);
//		} else if(texture2Available)
//
//		{
//			texture = videoCaptureTexture2;
//			texture2Available = false;
//			textureID = 2;
//          sendingQueue.Add(2);
//		}
//		else
//		{
//			continue;
//		}
//		
//		// =========================================================
//		
//
//
//      // This section would process ready textures as fast as possible
//      // The queue would only have max [number of texture buffers] at a time
//		foreach (int id in sendingQueue.GetConsumingEnumerable())
//		{
//			MLog("Encoding");
//
//			byte[] jpgBytes = null;
//			if (id==1)
//				
//			{
//				jpgBytes = videoCaptureTexture.EncodeToJPG(40);
//				texture1Available = true;
//			} else if(id==2)
//			{
//				jpgBytes = videoCaptureTexture2.EncodeToJPG(40);
//				texture2Available = true;
//			}
//			
//
//			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jpgBytes.Length + "\n");
//			MLog("Starting Sending " + jpgBytes.Length);
//			client.GetStream().Write(bytes, 0, bytes.Length);
//			client.GetStream().Write(jpgBytes, 0, jpgBytes.Length);
//			MLog("Done " + jpgBytes.Length);
//		}
//	}
}