using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Owns generated geometry only. Source atlas meshes are never replaced or mutated.
    public sealed class VolumetricTissue : MonoBehaviour
    {
        public TissueVolume Volume { get; private set; }
        public Mesh Surface { get; private set; }
        MeshRenderer surfaceRenderer;
        MeshCollider contact;
        Material[] materials;
        public void Initialize(TissueVolume volume)
        {
            if(Volume!=null) return;
            Volume=volume;
            Surface=new Mesh {name="RuntimeAbdominalVolume"};Surface.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh=Surface;
            surfaceRenderer=gameObject.AddComponent<MeshRenderer>();
            materials=new Material[volume.Materials.Length];
            for(int i=0;i<materials.Length;i++)
            {
                materials[i]=TissueRuntimeMaterial.Create(volume.Materials[i].id,volume.Materials[i].color);
                materials[i].SetFloat("_Glossiness",i==0?.25f:.45f);
            }
            surfaceRenderer.sharedMaterials=materials;
            contact=gameObject.AddComponent<MeshCollider>();
            CommitSurface();SetVisible(false);
        }
        public void SetVisible(bool visible)
        {
            if(surfaceRenderer) surfaceRenderer.enabled=visible;
            if(contact) contact.enabled=visible;
            if(!visible) Volume?.Freeze();
        }
        public bool Visible => surfaceRenderer && surfaceRenderer.enabled && contact && contact.enabled;
        public void CommitSurface()
        {
            if(Volume==null||!Volume.Dirty) return;
            Volume.WriteSurface(Surface);
            contact.sharedMesh=null;contact.sharedMesh=Surface;
        }
        public void ResetTissue(){Volume?.Reset();CommitSurface();}
        void OnDisable()=>SetVisible(false);
        void OnDestroy()
        {
            if(Surface) Dispose(Surface);
            if(materials!=null) foreach(var material in materials) if(material) Dispose(material);
        }
        static void Dispose(Object item){if(Application.isPlaying)Destroy(item);else DestroyImmediate(item);}
    }
}
