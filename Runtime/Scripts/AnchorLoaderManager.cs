using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Manages spatial anchor creation, deletion, saving, and linking within the application.
/// </summary>
/// <remarks>
/// David Mertens, TH Koeln.
/// </remarks>
/// 
public class AnchorLoaderManager : MonoBehaviour
{
    public readonly struct SaveAnchorOutcome
    {
        public bool Persisted { get; }
        public Guid Uuid { get; }
        public string Status { get; }

        public SaveAnchorOutcome(bool persisted, Guid uuid, string status)
        {
            Persisted = persisted;
            Uuid = uuid;
            Status = status;
        }
    }

    public string numUuidsPlayerPref = "NumUuids";
    public List<OVRSpatialAnchor> anchors;
    public AnchorLoader AnchorLoader;
    public List<Guid> Uuids;

    private void Awake()
    {
        AnchorLoader = new AnchorLoader(this);
        anchors = new List<OVRSpatialAnchor>();
        Uuids = new List<Guid>();
    }
    
    /// <summary>
    /// Asynchronously deletes all anchors and their saved UUIDs.
    /// </summary>
    /// 
    public async Task DeleteAllAnchors()
    {
        var result = await OVRSpatialAnchor.EraseAnchorsAsync(anchors, anchors.Select(a => a.Uuid));
        if (result.Success)
        {
            anchors.ForEach(a => Destroy(a.gameObject));
        }
        
        Uuids.Clear();
        anchors.Clear();
        DeleteSavedUuids();
    }

    public void LinkNewAnchor(OVRSpatialAnchor anchor)
    {
        anchors.Add(anchor);
    }

    public void SaveAnchor(OVRSpatialAnchor anchor)
    {
        _ = SaveAnchorAndPersistIfValidAsync(anchor);
    }

    public async Task<SaveAnchorOutcome> SaveAnchorAndPersistIfValidAsync(OVRSpatialAnchor anchor)
    {
        if (anchor == null)
        {
            Debug.LogWarning("[AnchorLoaderManager] SaveAnchorAndPersistIfValidAsync called with null anchor.");
            return new SaveAnchorOutcome(false, Guid.Empty, "NullAnchor");
        }

        Guid initialUuid = anchor.Uuid;

        try
        {
            var saveTask = anchor.SaveAnchorAsync();
            while (!saveTask.IsCompleted)
            {
                await Task.Yield();
            }

            object saveResult = saveTask.GetResult();
            bool success = TryGetResultSuccess(saveResult, false);
            string status = GetResultStatus(saveResult);
            Guid finalUuid = anchor.Uuid;

            if (!success || finalUuid == Guid.Empty)
            {
                Debug.LogWarning($"[AnchorLoaderManager] Not persisting UUID. saveSuccess={success}, finalUuid={finalUuid}, status={status}");
                return new SaveAnchorOutcome(false, finalUuid, status);
            }

            OverwriteSavedUuids(finalUuid);
            return new SaveAnchorOutcome(true, finalUuid, status);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AnchorLoaderManager] SaveAnchorAsync threw for uuid={initialUuid}: {ex}");
            return new SaveAnchorOutcome(false, Guid.Empty, "Exception");
        }
    }

    private void SaveUuid(Guid uuid)
    {
        if (!PlayerPrefs.HasKey(numUuidsPlayerPref))
        {
            PlayerPrefs.SetInt(numUuidsPlayerPref, 0);
        }

        int playerNumUuids = PlayerPrefs.GetInt(numUuidsPlayerPref);
        string key = "uuid" + playerNumUuids;
        PlayerPrefs.SetString(key, uuid.ToString());
        PlayerPrefs.SetInt(numUuidsPlayerPref, ++playerNumUuids);
        PlayerPrefs.Save();
    }

    private void DeleteSavedUuids()
    {
        if (!PlayerPrefs.HasKey(numUuidsPlayerPref))
        {
            return;
        }

        int numUuids = PlayerPrefs.GetInt(numUuidsPlayerPref);
        for (int i = 0; i < numUuids; i++)
        {
            string key = $"uuid{i}";
            if (PlayerPrefs.HasKey(key))
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        PlayerPrefs.DeleteKey(numUuidsPlayerPref);
        PlayerPrefs.Save();
    }

    private IEnumerator AnchorCreated(OVRSpatialAnchor instancedAnchor)
    {
        while (!instancedAnchor.Created && !instancedAnchor.Localized)
        {
            yield return new WaitForEndOfFrame();
        }

        Guid anchorGuid = instancedAnchor.Uuid;
        RegiTarget tracker = instancedAnchor.GetComponent<RegiTarget>();
        tracker.uuidName = anchorGuid;
        anchors.Add(instancedAnchor);
    }

    public List<Guid> GetSavedUuids()
    {
        var result = new List<Guid>();
        if (!PlayerPrefs.HasKey(numUuidsPlayerPref))
            return result;

        int numUuids = PlayerPrefs.GetInt(numUuidsPlayerPref);
        for (int i = 0; i < numUuids; i++)
        {
            string uuidString = PlayerPrefs.GetString($"uuid{i}", string.Empty);
            if (Guid.TryParse(uuidString, out Guid uuid) && uuid != Guid.Empty)
            {
                result.Add(uuid);
            }
            else if (!string.IsNullOrWhiteSpace(uuidString))
            {
                Debug.LogWarning($"[AnchorLoaderManager] Invalid UUID in PlayerPrefs key 'uuid{i}': '{uuidString}'");
            }
        }

        return result;
    }

    private static bool TryGetResultSuccess(object result, bool defaultValue)
    {
        if (result == null)
            return defaultValue;

        Type resultType = result.GetType();
        var successProperty = resultType.GetProperty("Success");
        if (successProperty == null)
            return defaultValue;

        object successValue = successProperty.GetValue(result);
        return successValue is bool success ? success : defaultValue;
    }

    private static string GetResultStatus(object result)
    {
        if (result == null)
            return "Unknown";

        object status = result.GetType().GetProperty("Status")?.GetValue(result);
        return status?.ToString() ?? "Unknown";
    }

    private void OverwriteSavedUuids(Guid uuid)
    {
        DeleteSavedUuids();
        SaveUuid(uuid);
    }
}
