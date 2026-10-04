using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scalpal.Surgery.EditorTools
{
    // A bounded additive prefab in the same source frame as AnatomyExercise. The runtime caller
    // must parent it to session.anatomy.transform with local identity before rebuilding its index.
    // No independent fit, centering, rescaling, importer mutation or second controller is authored.
    public static class OpenSurgeryRiskBuilder
    {
        public const string PrefabPath = "Assets/Scalpal/Surgery/Resources/OpenSurgeryRisks.prefab";
        const string ManifestPath = "Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json";
        const string ExpectedModelPath = "Assets/Scalpal/Anatomy/Models/cardiovascular.fbx";
        static readonly string[] Ids = {
            "cardiovascular__common_iliac_artery_r", "cardiovascular__common_iliac_vein_r"
        };
        [Serializable] sealed class Atlas { public int schemaVersion; public SystemEntry[] systems; public PartEntry[] parts; }
        [Serializable] sealed class SystemEntry { public string id, group, assetPath; }
        [Serializable] sealed class PartEntry { public string stableId, displayName, system, objectName; public int triangles; }

        [MenuItem("Scalpal/Surgery/Build and Validate Open Surgery Risks")]
        public static void BuildAndValidate()
        {
            var manifest=AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            Require(manifest,"Missing anatomy atlas manifest.");
            var atlas=JsonUtility.FromJson<Atlas>(manifest.text);
            Require(atlas!=null && atlas.schemaVersion==1 && atlas.systems!=null && atlas.parts!=null,"Unsupported anatomy atlas manifest.");
            var system=atlas.systems.Single(s=>s.id=="cardiovascular");
            Require(system.group=="body" && system.assetPath==ExpectedModelPath,"Risk geometry must be the shared-frame cardiovascular model.");
            var model=AssetDatabase.LoadAssetAtPath<GameObject>(system.assetPath);
            Require(model,"Missing cardiovascular source model.");
            var sources=model.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh).ToArray();
            var parts=Ids.Select(id=>atlas.parts.Single(p=>p.stableId==id)).ToArray();
            var sourceById=new Dictionary<string,MeshFilter>(StringComparer.Ordinal);
            foreach(var part in parts)
            {
                Require(part.system==system.id && part.triangles>0,"Risk part is not verified cardiovascular geometry: "+part.stableId);
                var source=sources.Single(f=>f.name==part.objectName);
                Require(source.GetComponent<Renderer>(),"Missing source mesh renderer: "+part.stableId);
                Require(source.sharedMesh.triangles.Length/3==part.triangles,"Manifest triangle count disagrees with imported source: "+part.stableId);
                sourceById.Add(part.stableId,source);
            }
            int anatomyLayer=LayerMask.NameToLayer("Anatomy");
            var root=new GameObject("OpenSurgeryRisks");
            try
            {
                var copied=new Dictionary<Transform,Transform>();
                foreach(var part in parts)
                {
                    var source=sourceById[part.stableId];
                    var node=CopyTransform(source.transform,model.transform,root.transform,copied);
                    if(anatomyLayer>=0) node.gameObject.layer=anatomyLayer;
                    node.gameObject.AddComponent<MeshFilter>().sharedMesh=source.sharedMesh;
                    var renderer=node.gameObject.AddComponent<MeshRenderer>();
                    string palette=part.stableId.Contains("vein")?"vessel_vein":"vessel_artery";
                    var material=LoadMaterial(palette); var ghost=LoadMaterial(palette+"_ghost");
                    renderer.sharedMaterials=Enumerable.Repeat(material,Math.Max(1,source.sharedMesh.subMeshCount)).ToArray();
                    renderer.shadowCastingMode=source.GetComponent<Renderer>().shadowCastingMode;
                    renderer.receiveShadows=source.GetComponent<Renderer>().receiveShadows;
                    var identity=node.gameObject.AddComponent<AnatomyPart>();
                    identity.stableId=part.stableId; identity.displayName=part.displayName; identity.system=part.system; identity.ghostMaterial=ghost;
                    var collider=node.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh=source.sharedMesh; collider.convex=false; collider.isTrigger=false;
                }
                copied[model.transform].name=system.id;
                Validate(root,sourceById,parts);
                EnsureFolder("Assets/Scalpal/Surgery/Resources");
                var prefab=PrefabUtility.SaveAsPrefabAsset(root,PrefabPath,out bool saved);
                Require(saved && prefab,"Could not save open surgery risk prefab.");
                AssetDatabase.SaveAssets();
                Validate(prefab,sourceById,parts);
                var models=AssetDatabase.GetDependencies(PrefabPath,true).Where(path=>path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(models.Length==1 && models[0]==ExpectedModelPath,"Risk prefab unexpectedly depends on another model.");
                Debug.Log("SCALPAL_OPEN_SURGERY_RISKS_VALIDATED parts=2 colliders=2 triangles="+parts.Sum(p=>p.triangles)+
                    " sourceFrame=preserved localRoot=identity source="+ExpectedModelPath+" output="+PrefabPath+
                    " headset=false participantRegistration=false");
            }
            finally { Object.DestroyImmediate(root); }
        }
        static Material LoadMaterial(string name)
        {
            string path="Assets/Scalpal/Anatomy/Materials/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            Require(material && material.shader,"Missing authored anatomy palette material: "+path); return material;
        }
        // Same ancestor-copy convention as AnatomyAtlasBuilder.CopyTransform, deliberately including
        // the imported model root's rotation/scale. Only the two mesh nodes receive geometry.
        static Transform CopyTransform(Transform source,Transform modelRoot,Transform outputRoot,Dictionary<Transform,Transform> copied)
        {
            if(copied.TryGetValue(source,out var existing)) return existing;
            var parent=source==modelRoot?outputRoot:CopyTransform(source.parent,modelRoot,outputRoot,copied);
            var node=new GameObject(source.name).transform; node.SetParent(parent,false);
            node.localPosition=source.localPosition; node.localRotation=source.localRotation; node.localScale=source.localScale;
            copied.Add(source,node); return node;
        }
        static void Validate(GameObject root,Dictionary<string,MeshFilter> sources,PartEntry[] definitions)
        {
            Require(root.transform.localPosition==Vector3.zero && root.transform.localRotation==Quaternion.identity && root.transform.localScale==Vector3.one,
                "Risk prefab root must have local identity.");
            var parts=root.GetComponentsInChildren<AnatomyPart>(true);
            Require(parts.Length==2 && parts.Select(p=>p.stableId).OrderBy(v=>v).SequenceEqual(Ids.OrderBy(v=>v)),"Unexpected risk anatomy identities.");
            Require(root.GetComponentsInChildren<Collider>(true).Length==2,"Risk prefab requires exactly two owned colliders.");
            Require(root.GetComponentsInChildren<Rigidbody>(true).Length==0 && root.GetComponentsInChildren<AnatomyController>(true).Length==0,
                "Risk prefab must use its parent's existing anatomy controller and registration.");
            foreach(var part in parts)
            {
                var filter=part.GetComponent<MeshFilter>(); var collider=part.GetComponent<MeshCollider>(); var source=sources[part.stableId];
                Require(filter && collider && filter.sharedMesh==source.sharedMesh && collider.sharedMesh==filter.sharedMesh && !collider.convex && !collider.isTrigger,
                    "Risk collider does not belong to its exact imported mesh: "+part.stableId);
                var renderer=part.GetComponent<Renderer>();
                Require(renderer && renderer.enabled && renderer.sharedMaterials.All(m=>m && m.shader),"Risk materials are incomplete.");
                Matrix4x4 actual=root.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
                Matrix4x4 expected=source.transform.localToWorldMatrix;
                for(int i=0;i<16;i++) Require(Mathf.Abs(actual[i]-expected[i])<.00001f,"Source frame changed for "+part.stableId+" matrix element "+i);
                Require(filter.sharedMesh.triangles.Length/3==definitions.Single(d=>d.stableId==part.stableId).triangles,"Risk triangle count changed.");
            }
        }
        static void EnsureFolder(string path)
        {
            if(AssetDatabase.IsValidFolder(path)) return;
            int slash=path.LastIndexOf('/'); string parent=path.Substring(0,slash); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent,path.Substring(slash+1));
        }
        static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    }
}
