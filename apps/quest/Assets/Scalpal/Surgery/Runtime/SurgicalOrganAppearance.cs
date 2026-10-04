using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Render-only teaching approximation over the original schematic cecum. All positions
    // stay inside its source ellipsoid envelope; shallow haustral grooves recede by up to
    // about 3 mm at the source's 25 mm radial extent. Contact still uses the original surface.
    // No anatomical attachment, clinical segmentation, deformation or scoring is invented.
    [DisallowMultipleComponent, DefaultExecutionOrder(250)]
    public sealed class SurgicalOrganAppearance : MonoBehaviour
    {
        NativeCaseSession session;
        AnatomyPart part;
        MeshRenderer original, shown;
        GameObject view;
        Mesh mesh;
        Material serosa, taenia;
        Texture2D detail;
        MaterialPropertyBlock highlight;
        bool originalSuppressed;
        public Mesh PresentationMesh => mesh;
        public MeshRenderer PresentationRenderer => shown;
        public AnatomyPart SourcePart => part;

        public void Initialize(NativeCaseSession owner, OpenBodyInteraction interaction)
        {
            Dispose(); session = owner;
            if (highlight == null) highlight = new MaterialPropertyBlock();
            if (!owner || !owner.anatomy || !interaction || !owner.anatomy.TryGetPart("cecum", out part)) return;
            var filter = part.GetComponent<MeshFilter>(); original = part.GetComponent<MeshRenderer>();
            if (!filter || !filter.sharedMesh || !original) { part = null; original = null; return; }
            originalSuppressed = original.forceRenderingOff;
            Bounds bounds = filter.sharedMesh.bounds;
            if (bounds.size.x <= 0 || bounds.size.y <= 0 || bounds.size.z <= 0) return;
            mesh = Pouch(bounds);
            serosa = TissueRuntimeMaterial.Create("CecumMoistSerosa_Teaching", new Color(.66f,.36f,.34f));
            detail = SerosaDetail(); serosa.mainTexture = detail; serosa.SetFloat("_Glossiness", .57f);
            taenia = TissueRuntimeMaterial.Create("CecumTaenia_Teaching", new Color(.77f,.62f,.55f));
            taenia.SetFloat("_Glossiness", .4f);
            view = new GameObject("CecumHaustratedPresentation"); view.layer = part.gameObject.layer;
            view.transform.SetParent(filter.transform, false);
            view.AddComponent<SurgicalVisualGeometry>();
            view.AddComponent<MeshFilter>().sharedMesh = mesh;
            shown = view.AddComponent<MeshRenderer>(); shown.sharedMaterials = new[] { serosa, taenia };
            shown.shadowCastingMode = original.shadowCastingMode; shown.receiveShadows = original.receiveShadows;
            Refresh();
        }

        public void Refresh()
        {
            if (!shown || !original || !part) return;
            // Keep enabled/HasVisibleGeometry and collider semantics intact. The scene graph
            // continues to own visibility; only the draw of the old ellipsoid is suppressed.
            bool visible = isActiveAndEnabled && session && session.anatomy && session.anatomy.RegistrationValid
                && part.IsVisible && original.enabled && original.gameObject.activeInHierarchy && !part.IsGhosted;
            shown.enabled = visible;
            original.forceRenderingOff = visible || originalSuppressed;
            if (visible)
            {
                original.GetPropertyBlock(highlight, 0);
                if (highlight.isEmpty) original.GetPropertyBlock(highlight);
                shown.SetPropertyBlock(highlight, 0); shown.SetPropertyBlock(highlight, 1);
            }
        }
        void LateUpdate() => Refresh();

        static Mesh Pouch(Bounds bounds)
        {
            const int rings = 32, sides = 48;
            Vector3 half = bounds.extents;
            int axis = half.x > half.y && half.x > half.z ? 0 : half.y > half.z ? 1 : 2;
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            var vertices = new List<Vector3>(); var uv = new List<Vector2>();
            var body = new List<int>(); var bands = new List<int>();
            for (int row = 0; row <= rings; row++)
            {
                float t = Mathf.Lerp(-.97f, .97f, row / (float)rings);
                float envelope = Mathf.Sqrt(1-t*t);
                for (int side = 0; side <= sides; side++)
                {
                    float angle = side * Mathf.PI * 2 / sides;
                    // Six shallow sacculations with three taenia sectors. Everything recedes
                    // into the original envelope rather than enlarging the measured organ.
                    float pocket = .945f + .045f * Mathf.Cos((t+1)*Mathf.PI*6 + .25f*Mathf.Cos(angle*3));
                    float sector = .982f + .018f * Mathf.Cos(angle*3);
                    Vector3 v = bounds.center;
                    v[axis] += t*half[axis];
                    v[a] += Mathf.Cos(angle)*half[a]*envelope*pocket*sector;
                    v[b] += Mathf.Sin(angle)*half[b]*envelope*pocket*sector;
                    vertices.Add(v); uv.Add(new Vector2(side/(float)sides,row/(float)rings));
                }
            }
            for (int row=0;row<rings;row++) for(int side=0;side<sides;side++)
            {
                int i=row*(sides+1)+side, j=i+sides+1;
                // Three narrow pale longitudinal bands, not an invented insertion landmark.
                var triangles = side%16 == 0 ? bands : body;
                triangles.AddRange(new[]{ i,i+1,j, i+1,j+1,j });
            }
            for(int end=0;end<2;end++)
            {
                Vector3 tip=bounds.center;tip[axis]+=half[axis]*(end==0?-1:1);
                int pole=vertices.Count;vertices.Add(tip);uv.Add(new Vector2(.5f,end));
                int row=end==0?0:rings*(sides+1);
                for(int side=0;side<sides;side++)
                    if(end==0)body.AddRange(new[]{pole,row+side+1,row+side});
                    else body.AddRange(new[]{pole,row+side,row+side+1});
            }
            var result=new Mesh{name="CecumHaustrated_RenderOnly_SourceEnvelope"};
            result.SetVertices(vertices);result.SetUVs(0,uv);result.subMeshCount=2;
            result.SetTriangles(body,0);result.SetTriangles(bands,1);result.RecalculateNormals();result.RecalculateBounds();
            return result;
        }
        static Texture2D SerosaDetail()
        {
            const int size=128;
            var image=new Texture2D(size,size,TextureFormat.RGB24,false){name="CecumSerosa_SubtleProceduralDetail",wrapMode=TextureWrapMode.Repeat};
            var colors=new Color[size*size];
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float variation=.96f+.04f*Mathf.Sin(x*.37f+Mathf.Sin(y*.19f))*Mathf.Sin(y*.41f);
                colors[y*size+x]=new Color(variation,variation*.985f,variation*.98f);
            }
            image.SetPixels(colors);image.Apply(false,true);return image;
        }
        void OnDisable(){if(shown)shown.enabled=false;if(original)original.forceRenderingOff=originalSuppressed;}
        void OnDestroy()=>Dispose();
        public void Dispose()
        {
            if(original)original.forceRenderingOff=originalSuppressed;
            if(view){view.SetActive(false);Release(view);}
            Release(mesh);Release(serosa);Release(taenia);Release(detail);
            view=null;mesh=null;serosa=taenia=null;detail=null;shown=original=null;part=null;
        }
        static void Release(Object item){if(!item)return;if(Application.isPlaying)Destroy(item);else DestroyImmediate(item);}
    }
}
