// Spawns one touchable port site per case port under the registered torso root, at the case's
// torso-frame positions scaled by bodyScale. Only the current place_ports step's sites are shown.
using System.Collections.Generic;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Preop;
using UnityEngine;

namespace Scalpal.Experience
{
    [DisallowMultipleComponent]
    public sealed class PortLayout : MonoBehaviour
    {
        [Tooltip("Registration's torso root: +Z toward the head, +Y out of the abdomen, origin at the umbilicus.")]
        public Transform torsoRoot;
        [Tooltip("Optional visual for a port site. A trigger sphere is added either way.")]
        public GameObject markerPrefab;
        [SerializeField] float triggerRadiusMeters = 0.015f;

        readonly Dictionary<string, GameObject> sites = new Dictionary<string, GameObject>();

        public void Build(SurgicalCase kase)
        {
            Clear();
            if (torsoRoot == null || kase?.procedure?.ports == null) return;
            foreach (var port in kase.procedure.ports)
            {
                var site = markerPrefab != null ? Instantiate(markerPrefab) : new GameObject();
                site.name = "port_" + port.id;
                site.transform.SetParent(torsoRoot, false);
                site.transform.localPosition = TorsoFrame.PortLocalPosition(port, kase.bodyScale);
                var trigger = site.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = triggerRadiusMeters;
                site.AddComponent<PortTarget>().portId = port.id;
                site.SetActive(false);
                sites[port.id] = site;
            }
        }

        public void SetActivePorts(string[] portIds)
        {
            var active = new HashSet<string>(portIds ?? new string[0]);
            foreach (var pair in sites) if (pair.Value != null) pair.Value.SetActive(active.Contains(pair.Key));
        }

        public void Clear()
        {
            foreach (var site in sites.Values) if (site != null) Destroy(site);
            sites.Clear();
        }

        void OnDestroy() { Clear(); }
    }

    // Marks a port site so an instrument tip entering it places that port.
    [DisallowMultipleComponent]
    public sealed class PortTarget : MonoBehaviour
    {
        public string portId = "";
    }
}
