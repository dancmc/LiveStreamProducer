// %BANNER_BEGIN%
// ---------------------------------------------------------------------
// %COPYRIGHT_BEGIN%
//
// Copyright (c) 2018 Magic Leap, Inc. All Rights Reserved.
// Use of this file is governed by the Creator Agreement, located
// here: https://id.magicleap.com/creator-terms
//
// %COPYRIGHT_END%
// ---------------------------------------------------------------------
// %BANNER_END%

using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;


    [RequireComponent(typeof(PrivilegeRequester))]
    public class LiveStream : MonoBehaviour
    {
        
        private PrivilegeRequester _privilegeRequester;

        public LiveStream()
        {
            
        }


   


        private void Awake()
        {
            // Request privileges
            Debug.Log("Requesting Privileges");
            _privilegeRequester = GetComponent<PrivilegeRequester>();
            _privilegeRequester.OnPrivilegesDone += HandlePrivilegesDone;

        }

        

        private void Update()
        {
        }


        private void OnDisable()
        {
            MLInput.OnControllerButtonDown -= OnButtonDown;

        }


        private void OnApplicationPause(bool pause)
        {
            if (pause) MLInput.OnControllerButtonDown -= OnButtonDown;
        }

        private void OnDestroy()
        {
            if (_privilegeRequester != null) _privilegeRequester.OnPrivilegesDone -= HandlePrivilegesDone;
        }


        #region Private Functions

        private void DisableCamera()
        {
            if (MLCamera.IsStarted)
            {
                CameraScript.setCaptureActive(false);
                MLCamera.Disconnect();
                MLCamera.Stop();
            }
        }

        private void EnableCapture()
        {
                // Enable camera and set controller callbacks
                MLCamera.Start();
                MLCamera.Connect();
                
                CameraScript.setCaptureActive(true);
                MLInput.OnControllerButtonDown += OnButtonDown;

        }

        #endregion



        private void HandlePrivilegesDone(MLResult result)
        {
            if (!result.IsOk)
            {
                if (result.Code == MLResultCode.PrivilegeDenied) Instantiate(Resources.Load("PrivilegeDeniedError"));

                Debug.LogErrorFormat(
                    "Error: VideoCaptureExample failed to get all requested privileges, disabling script. Reason: {0}",
                    result);
                enabled = false;
                return;
            }

            Debug.Log("Succeeded in requesting all privileges");
            
            EnableCapture();

        }


        private void OnButtonDown(byte controllerId, MLInputControllerButton button)
        {
//            if (_controllerConnectionHandler.IsControllerValid(controllerId) &&
//                MLInputControllerButton.Bumper == button)
//            {
//                Debug.Log("Bumper Pressed");
//
//
//                if (!_isCapturing)
//                {
//                    Debug.Log("Bumper action commencing");
//                }
//                else
//                {
//                }
//            }
        }

        
    }
