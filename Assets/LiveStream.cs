using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;

/// <summary>
/// This is a simple coordinator script for this stream producer app to request necessary privileges
/// then activate streaming
/// </summary>
[RequireComponent(typeof(PrivilegeRequester))]
public class LiveStream : MonoBehaviour
{
    private PrivilegeRequester _privilegeRequester;

    public GameObject cube;
    public GameObject sphere;
    public GameObject vitals;

    private void Awake()
    {
        // Request privileges
        Debug.Log("Requesting Privileges");
        _privilegeRequester = GetComponent<PrivilegeRequester>();
        _privilegeRequester.OnPrivilegesDone += HandlePrivilegesDone;

        StartCoroutine(setPosition());
    }

    IEnumerator setPosition()
    {
        yield return new WaitForSeconds(1);
        var mainCam = Camera.main;
        var mainCamTransform = mainCam.transform;
        cube.transform.position = mainCamTransform.position + mainCamTransform.forward * 2f;
        sphere.transform.position = mainCamTransform.position + mainCamTransform.right * -2f;
        vitals.transform.position = mainCamTransform.position + mainCamTransform.right * 2f;
        
        sphere.transform.LookAt(mainCam.transform);
        vitals.transform.rotation = Quaternion.LookRotation(vitals.transform.position - mainCamTransform.position);
    }


    private void OnDestroy()
    {
        if (_privilegeRequester != null) _privilegeRequester.OnPrivilegesDone -= HandlePrivilegesDone;
    }

 private void HandlePrivilegesDone(MLResult result)
    {
        if (result.IsOk)
        {
            Debug.Log("Succeeded in requesting all privileges");  
            CameraScript.permissionsHandled(true);
            
        }
        else
        {
            if (result.Code == MLResultCode.PrivilegeDenied) Instantiate(Resources.Load("PrivilegeDeniedError"));

            Debug.LogErrorFormat(
                "Error: LiveStream failed to get all requested privileges, disabling script. Reason: {0}",
                result);
            CameraScript.permissionsHandled(false);
            enabled = false;
        }

        
    }


}