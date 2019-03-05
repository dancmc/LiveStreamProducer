using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;

/// <summary>
/// This is a simple coordinator script for this stream producer app to request necessary privileges
/// then activate streaming.
/// </summary>
[RequireComponent(typeof(PrivilegeRequester))]
public class LiveStreamProducer : MonoBehaviour
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


    // A bit of a hack to make sure objects are positioned around user on app start.
    // Otherwise sometimes objects persist elsewhere in the room in old positions from previous runs.
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


    // Method adapted from official ML tutorial samples
    private void HandlePrivilegesDone(MLResult result)
    {
        if (!result.IsOk)
        {
            if (result.Code == MLResultCode.PrivilegeDenied) Instantiate(Resources.Load("PrivilegeDeniedError"));

            Debug.LogErrorFormat(
                "handlePrivilegesDone :: LiveStreamProducer failed to get all requested " +
                "privileges, disabling script. Reason: {0}",
                result);
            CameraScript.permissionsHandled(false);
            enabled = false;
            
            return;
        }
        
        Debug.Log("handlePrivilegesDone :: Succeeded in requesting all privileges");
        CameraScript.permissionsHandled(true);
    }
}