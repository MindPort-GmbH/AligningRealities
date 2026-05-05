using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Manages registration workflow, including marker setup, state changes, and alignment algorithms for a target object.
/// </summary>
/// <remarks>
/// David Mertens, TH Koeln.
/// </remarks>
///
public class Registration : MonoBehaviour
{
    public RegiTarget regiTarget;
    public RegistrationPlaneProjection RegistrationPlaneProjection;
    public Algorithm algorithmToUse;
    public event Action StateChanged;
    public string numUuidsKey = "demoTargetUuidKey";
    public bool onlyCorrectYAxis;
    [SerializeField] private bool loadSceneAfterSave;
    [SerializeField] private string sceneToLoadAfterSave = "DatahubTest";

    [HideInInspector] public State currentState;
    [HideInInspector] public List<GameObject> markers;

    private AnchorLoaderManager _anchorLoaderManager;
    private Vector3 _tipPosition;
    private Calibrator _calibrator;
    private Kabsch.Kabsch _kabsch = new();

    public enum State
    {
        Calibration,
        MarkerSetup,
        Confirmation,
        Inactive,
    }

    public enum Algorithm
    {
        Kabsch,
        ProjectionPlaneMapping
    }

    private void Awake()
    {
        RegistrationPlaneProjection = new RegistrationPlaneProjection();
        markers = new List<GameObject>();
        regiTarget.SetVisible(false);

        _anchorLoaderManager = gameObject.AddComponent<AnchorLoaderManager>();
        _anchorLoaderManager.numUuidsPlayerPref = numUuidsKey;
    }

    /// <summary>
    /// Sets the current registration state and triggers state change events.
    /// </summary>
    /// <param name="nextState">The new state to set.</param>
    public void SetState(State nextState)
    {
        currentState = nextState;
        StateChanged?.Invoke();

        switch (nextState)
        {
            case State.MarkerSetup:
                ResetEverything();
                OVRSpatialAnchor anchor = regiTarget.GetComponent<OVRSpatialAnchor>();
                if (anchor != null) Destroy(anchor);
                regiTarget.SetVisible(false);
                break;

            case State.Inactive:
                ResetMarker();
                break;
        }
    }

    /// <summary>
    /// Adds a marker at the specified position and aligns the target if the maximum number of markers is reached.
    /// </summary>
    /// <param name="position">World position for the marker.</param>
    public void AddMarker(Vector3 position)
    {
        if (markers.Count >= regiTarget.amountControlPoints) return;

        GameObject go = Helper.CreateSmallSphere();
        go.transform.position = position;
        go.AddComponent<OVRSpatialAnchor>();
        Helper.SetColor(go, Helper.GetColorForIndex(markers.Count));
        markers.Add(go);
        go.transform.SetParent(transform);

        if (ReachedMaxMarkerAmount())
        {
            Align(regiTarget);
            SetState(State.Confirmation);
            regiTarget.gameObject.AddComponent<OVRSpatialAnchor>();
        }
    }

    /// <summary>
    /// Restores the last placed anchor using device anchor data.
    /// </summary>
    public void RestoreLastPlacedAnchor()
    {
        LinkPositionFromDevice();
    }

    /// <summary>
    /// Resets the registration target and all placed markers.
    /// </summary>
    public void ResetEverything()
    {
        ResetTarget();
        ResetMarker();
    }

    /// <summary>
    /// Saves the current registration data asynchronously.
    /// </summary>
    public void SaveRegistration()
    {
        if (regiTarget.GetComponent<OVRSpatialAnchor>() == null)
        {
            regiTarget.gameObject.AddComponent<OVRSpatialAnchor>();
        }

        StartCoroutine(SaveAnchorsDelayed());
    }

    private void ResetTarget()
    {
        regiTarget.SetVisible(false);
        regiTarget.transform.position = Vector3.zero;
        regiTarget.transform.rotation = Quaternion.identity;
    }

    private void ResetMarker()
    {
        markers.ForEach(Destroy);
        markers.Clear();
    }

    private void LinkPositionFromDevice()
    {
        _anchorLoaderManager.AnchorLoader.LoadAnchorsByUuid(regiTarget);
        SetState(State.Confirmation);
    }

    private IEnumerator SaveAnchorsDelayed()
    {
        OVRSpatialAnchor anchor = regiTarget.GetComponent<OVRSpatialAnchor>();
        if (anchor == null)
        {
            anchor = regiTarget.gameObject.AddComponent<OVRSpatialAnchor>();
        }

        const float maxWaitSeconds = 10f;
        float startTime = Time.realtimeSinceStartup;
        while (anchor != null && (!anchor.Created || anchor.Uuid == Guid.Empty))
        {
            if (Time.realtimeSinceStartup - startTime >= maxWaitSeconds)
            {
                Debug.LogWarning($"[Registration] SaveAnchorsDelayed aborted: anchor not ready within {maxWaitSeconds}s. created={anchor.Created}, uuid={anchor.Uuid}");
                yield break;
            }

            yield return null;
            anchor = regiTarget.GetComponent<OVRSpatialAnchor>();
        }

        if (anchor == null)
        {
            Debug.LogWarning("[Registration] SaveAnchorsDelayed aborted: target has no OVRSpatialAnchor component after wait.");
            yield break;
        }

        Task<AnchorLoaderManager.SaveAnchorOutcome> saveTask = _anchorLoaderManager.SaveAnchorAndPersistIfValidAsync(anchor);
        while (!saveTask.IsCompleted)
        {
            yield return null;
        }

        if (saveTask.IsFaulted)
        {
            Debug.LogError($"[Registration] SaveAnchorsDelayed failed with exception: {saveTask.Exception}");
            yield break;
        }

        AnchorLoaderManager.SaveAnchorOutcome saveOutcome = saveTask.Result;
        if (!saveOutcome.Persisted)
        {
            Debug.LogWarning($"[Registration] SaveAnchorsDelayed did not persist UUID. status={saveOutcome.Status}, uuid={saveOutcome.Uuid}");
            yield break;
        }

        if (!loadSceneAfterSave)
        {
            yield break;
        }

        yield return new WaitForSeconds(2);
        LoadConfiguredScene();
    }

    private void LoadConfiguredScene()
    {
        if (string.IsNullOrWhiteSpace(sceneToLoadAfterSave))
        {
            Debug.LogWarning("Scene load after save is enabled, but no target scene is configured.");
            return;
        }

        SceneManager.LoadScene(sceneToLoadAfterSave, LoadSceneMode.Single);
    }


    private void Align(RegiTarget target)
    {
        if (markers == null || markers.Count == 0 || target == null) return;

        if (algorithmToUse == Algorithm.Kabsch)
            AlignMeshKabsch(markers.Select(marker => marker.transform.position).ToList(), target);

        if (algorithmToUse == Algorithm.ProjectionPlaneMapping)
            RegistrationPlaneProjection.AlignMesh(markers.Select(marker => marker.transform.position).ToList(), target);

    }

    private void AlignMeshKabsch(List<Vector3> selectedPositions, RegiTarget toTransform)
    {
        toTransform.transform.position = Vector3.zero;
        toTransform.transform.rotation = Quaternion.identity;
        _kabsch.ReferencePoints = selectedPositions.ToArray();
        _kabsch.InPoints = toTransform.GetActiveRelativeMarkerPositions();
        _kabsch.TargetObject = toTransform.gameObject;
        _kabsch.SolveKabsch();
        toTransform.SetVisible(true);

        if (onlyCorrectYAxis)
        {
            var rotation = toTransform.transform.rotation;
            rotation.x = 0f;
            rotation.z = 0f;
            toTransform.transform.rotation = rotation;
        }
    }


    private bool ReachedMaxMarkerAmount()
    {
        return markers.Count >= regiTarget.amountControlPoints;
    }
}
