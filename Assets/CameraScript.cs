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
    // second camera shadowing gameCamera, renders game objects directly to renderRt for use
    // in integration later
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
    
    // Texture where external & internal camera frames are drawn to, then compressed
    // to jpg and transmitted
    private static Texture2D videoCaptureTexture;
    
    
    // Networking and network discovery objects
    private TcpClient client = new TcpClient();
    private volatile bool socketConnected;
    private UnityWebRequest discoveryRequest;
    private Thread connectionThread;
    
    private bool connectionAttemptFinished = true;

    // indicates that external camera is started and available for use
    private static bool externalCameraActive;
    // indicates that a jpg frame has been sent and a new one can proceed
    private bool readyToSend = true;

    
    // Set logging on or off (logging introduces lag)
    private const bool logEnabled = true;
    private bool cursorLookMode = true;
    

    //============================================================================================
    //   
    // LIFECYCLE METHODS
    //
    //=============================================================================================
    
    public CameraScript()
    {
        instance = this;
        Debug.unityLogger.logEnabled = logEnabled;
    }

    // Initialise all components
    private void Awake()
    {
        // Assign the camera variables
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
        filepath = Path.Combine(Application.persistentDataPath, "vid.mp4");
    }
    
    IEnumerator Start()
    {
        // Start long running coroutine to generate Jpgs and transmit
        while (true)
        {
            yield return new WaitForEndOfFrame();
   
            processJpg();
            
            // Enable this instead of processJpg() to try the fragmented video approach
            // processVideo();
        }
    }
  
    
    private void Update()
    {
        checkForFPSToggle();
    }
    
    // manually update secondary camera to follow main camera every frame
    private void OnPreRender()
    {
        renderCamera.gameObject.transform.position = gameCamera.gameObject.transform.position;
        renderCamera.gameObject.transform.rotation = gameCamera.gameObject.transform.rotation;
    }
    
     // Create a Jpg from a combination of an external camera frame and an in-game frame and transmit
    void processJpg()
    {
        long t = currentTime();

        if (!socketConnected || !readyToSend) return;
        
      
        readyToSend = false;

        //  Create a temporary RenderTexture of the same size as the texture
        var tmp = RenderTexture.GetTemporary(960,540,0,
            RenderTextureFormat.Default,RenderTextureReadWrite.Linear);

        MLog("processJpg :: Blitting " + (currentTime() - t));


        // Use actual external camera frame as background if available, otherwise placeholder
        // Use shader to overlay in-game frame on top of external camera
        // (In-game renderTexture assigned to shader in Awake() )
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
        
        MLog("processJpg :: Reading pixels " + (currentTime() - t));
        videoCaptureTexture.ReadPixels(new Rect(0, 0, 960, 540), 0,0);
        videoCaptureTexture.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(tmp);

        // Texture2Ds can be encoded to jpg by Unity
        MLog("processJpg :: Encoding JPG ");
        var jpgBytes = videoCaptureTexture.EncodeToJPG(40);
        var bytes = Encoding.UTF8.GetBytes(jpgBytes.Length + "\n");
        
        MLog("processJpg :: Starting sending " + jpgBytes.Length + " bytes");
        try
        {
            // write the size of the jpg, then the actual jpg
            instance.client.GetStream().Write(bytes, 0, bytes.Length);
            instance.client.GetStream().Write(jpgBytes, 0, jpgBytes.Length);
        }
        catch (Exception e)
        {
            MLog("processJpg :: Error -"+e.Message);
            
            closeClient();
            socketConnected = false;
            
            findNewServer();
        }

        MLog("processJpg :: Done");

        readyToSend = true;
        
    }
    
    private void OnDestroy()
    {
        closeClient();
        
        connectionThread?.Interrupt();
        ssdpListenThread?.Interrupt();
        ssdpRequestThread?.Interrupt();  
    }

    
    //============================================================================================
    //   
    // CAMERA/STREAM INITIATION METHODS
    //
    //=============================================================================================
    
    
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
        MLog("enableExternalCamera :: External Camera Enabled");
        MLCamera.Start();
        MLCamera.Connect();
        MLCamera.StartPreview();
        externalCameraActive = true;
    }

    private void disableExternalCamera()
    {
        MLog("disableExternalCamera :: External Camera Disabled");
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


    //============================================================================================
    //   
    // NETWORK METHODS
    //
    //=============================================================================================
    
    // Discover server application address (demo assumes only one server active)     
    private void findNewServer()
    {
        MLog("findNewServer :: Finding new server");
        
        // Http call to a private server to obtain a previously registered server address
        StartCoroutine(getServerAddress());

        // This section is the alternative method - automatic service discovery. Works on desktop,
        // but headsets cannot seem to send or receive UDP multicasts

        //  listenThread = new Thread(listenForServiceReply);
        //  listenThread.IsBackground = true;
        //  listenThread.Start();

        // sendThread = new Thread(sendServiceRequest);
        // sendThread.IsBackground = true;
        // sendThread.Start();
    }

    
    
    // Coroutine to attempt fetching pre-registered server address at intervals.
    // Once valid address received, attempt to connect then start receiving stream.
    // The connection logic unfortunately cannot be easily delegated to one thread because
    // UnityWebRequest must be called on the main thread, necessitating a complicated 
    // workaround.
    IEnumerator getServerAddress()
    {
        
        while (!socketConnected)
        {
            MLog("getServerAddress :: Making discovery request");
            
            discoveryRequest = UnityWebRequest.Get("https://danielchan.io/mldiscovery/get");
            yield return discoveryRequest.SendWebRequest();
              
            if(discoveryRequest.isNetworkError || discoveryRequest.isHttpError) {
                MLog("getServerAddress :: Error - "+discoveryRequest.error);
            }

            var message = discoveryRequest.downloadHandler.text;
            MLog("getServerAddress :: Received server address - "+message);
            
            var address = message.Split(':');
            var ip = address[0];
            var port = Convert.ToInt32(address[1]);
            if(ip.Equals("") || port<1) continue;

            connectionAttemptFinished = false;
            connectionThread = new Thread(()=>connectToServerBlocking(ip, port));
            connectionThread.IsBackground = true;
            connectionThread.Start();
            
            yield return new WaitUntil(() => connectionAttemptFinished);
            MLog("getServerAddress :: Finished connection attempt");
            
            yield return new WaitForSeconds(3);
        }
    }

    
    // Connect to server once address discovered
    void connectToServerBlocking(string ip, int port)
    {
        closeClient();
        
        try{
            client = new TcpClient();
            client.Connect(ip, port);
            var initialMessage = Encoding.UTF8.GetBytes("producer\n");
            client.GetStream().Write(initialMessage, 0, initialMessage.Length);
            var reader = new StreamReader(client.GetStream());
            var response = reader.ReadLine();
            if (response.Contains("rejected"))
            {
                closeClient();
            }
            else
            {
                socketConnected = true;
            }
            MLog("connectToServerBlocking :: Response from server - "+response);
        }
        catch (Exception e)
        {
            MLog("connectToServerBlocking :: Error -"+e.Message);
        }

        connectionAttemptFinished = true;
        connectionThread = null;
    }

    // Convenience method to close tcp client safely
    private void closeClient()
    {
        try
        {
            client.Close();
            socketConnected = false;
        }
        catch (ObjectDisposedException e)
        {
            MLog("closeClient :: Error -"+e.Message);
        }
    }

    //============================================================================================
    //   
    // UTILITY METHODS
    //
    //=============================================================================================
    
    // Convenience method for logging. The Unity logger can however introduce significant delay 
    private void MLog(String str)
    {
        Debug.unityLogger.Log(str);
    }
    
    // Returns current Unix time in ms
    private long currentTime()
    {
        return (long) (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds;
    }

    // Toggles use of the FPS Controller when X is pressed on keyboard
    private void checkForFPSToggle()
    {
        if (Input.GetKeyUp(KeyCode.X))
        {
            MLog("checkForFPSToggle :: X pressed");
            
            var fpsController = GameObject.Find("FPSController")?.GetComponent<FirstPersonController>();
            if (fpsController == null) return;
            
            if (cursorLookMode)
            {
                fpsController.enabled = false;
                cursorLookMode = false;
            }
            else
            {
                fpsController.enabled = true;
                cursorLookMode = true;
            }
        }
    }

    

    //============================================================================================
    //   
    // DISCARDED CODE
    //
    // Code samples for selected approaches that have been tried and rejected. Included for possible
    // reuse in future development.
    //
    //=============================================================================================

    
    // :::::: SSDP (Simple Service Discovery Protocol) ::::::
    // Attempt to use UDP Multicast to well-known multicast address to request server details.
    // Worked using desktop applications, but unable to replicate on headset (eg unable to receive 
    // even other background chatter on UniWireless or private router network). 
    
    private Thread ssdpRequestThread;
    private Thread ssdpListenThread;
    
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

    // Listening for service announcement over UDP Multicast containing server address
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
            closeClient();
            client = new TcpClient();
           
            
            connectToServerBlocking(address[0],Convert.ToInt32(address[1]));

            if (socketConnected)
            {
                break;
            }


        }

        ssdpListenThread = null;
    }
    
    

    // :::::: FRAGMENTED VIDEO ::::::
    // Attempt to record and send consecutive short video fragments using VideoCapture API.
    // Non-viable due to delay in stopping and starting recording to disk. Does not integrate
    // holograms anayway.
    
    private int count = 0;
    String filepath;
    private VideoState videostate = VideoState.VIDEO_NONE;

    enum VideoState
    {
        VIDEO_NONE,
        VIDEO_READY,
        VIDEO_STARTED,
        VIDEO_ENDED,
        VIDEO_SENT
    }    
    
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



    // :::::: JPG ENCODING MULTITHREADING ::::::
    // Attempt to multithread JPG encoding with a queue
    // Failed due to only being able to call EncodeToJPG on main thread
    
    private static BlockingCollection<int> sendingQueue = new BlockingCollection<int>();
    private static bool texture1Available = true;
    private static bool texture2Available = true;
    private static Texture2D videoCaptureTexture2;
    
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