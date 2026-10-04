using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Scalpal.Surgery.EditorTools
{
    // Synthetic component evidence. Actual imported atlas geometry is display-fitted here; this is
    // not registration, controller input, patient data or a headset capture. No scene asset is saved.
    public static class OpenSurgeryPreview
    {
        const string BundlePath = "Assets/Scalpal/Exercises/Resources/scalpal_bundle.json";
        const string AtlasPath = "Assets/Scalpal/Anatomy/Prefabs/AnatomyExercise_lap_appendectomy.prefab";
        const string IncisionPath = "/tmp/scalpal-open-incision.png";
        const string DeliveryPath = "/tmp/scalpal-open-delivery.png";

        [MenuItem("Scalpal/Surgery/Render Open Body Previews")]
        public static void Render()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Open surgery preview needs a graphics device; omit -nographics.");
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(BundlePath);
            if (!asset) throw new InvalidOperationException("Missing offline case bundle: " + BundlePath);
            var bundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
            var procedure = bundle.procedures.Single(p => p.id == "open_appendectomy");
            var runner = new CaseRunner(procedure); runner.Begin();
            if (runner.Body == null) throw new InvalidOperationException("Open case has no body state.");
            Scene preview = EditorSceneManager.NewPreviewScene();
            var roots = new List<GameObject>();
            var owned = new HashSet<Object>();
            RenderTexture target = null;
            try
            {
                var root = NewRoot("SyntheticOpenBodyPreview", preview, roots);
                var wound = root.AddComponent<OpenWoundView>(); wound.Build();
                wound.SetMarker(new[]{ new Vector3(-.03f,0,0),new Vector3(.03f,0,0) });
                wound.Apply(runner.Body);
                ValidateClosed(wound);
                var cameraObject = NewRoot("SyntheticVrPreviewCamera", preview, roots);
                var camera = cameraObject.AddComponent<Camera>(); camera.scene = preview;
                camera.transform.position = new Vector3(0,-.075f,-.225f);
                camera.transform.LookAt(new Vector3(0,0,0),Vector3.up);
                camera.orthographic = true; camera.orthographicSize = .079f; camera.aspect = 4f/3f; camera.fieldOfView = 37; camera.nearClipPlane = .001f; camera.farClipPlane = 4;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f,.063f,.080f);
                camera.allowHDR = false; camera.allowMSAA = true;
                AddLight(preview,roots,"SurgicalLight",new Vector3(-.08f,.10f,-.16f),.72f,new Color(1,.94f,.87f));
                AddLight(preview,roots,"FillLight",new Vector3(.09f,-.02f,-.10f),.22f,new Color(.7f,.83f,1));
                var title = Label(preview,roots,camera,"SKIN + FAT INCISION",new Vector3(.08f,.91f,.16f),.0055f);
                Label(preview,roots,camera,"Synthetic editor preview / virtual camera",new Vector3(.08f,.12f,.16f),.0032f);
                Label(preview,roots,camera,"Authored wound + imported atlas / no headset test",new Vector3(.08f,.075f,.16f),.0027f);
                target = new RenderTexture(1440,1080,24,RenderTextureFormat.ARGB32) { antiAliasing=4 };
                target.Create(); camera.targetTexture = target;

                Feed(runner,"mark_incision"); Feed(runner,"incise_skin"); wound.Apply(runner.Body);
                Check(runner.Body.Get("skin","opened") == 1 && runner.Body.Get("fat","opened") == 1, "Incision fixture did not open skin and fat.");
                Check(InnerGap(wound,"skin") > .01f && InnerGap(wound,"fat") > .01f, "Wound did not reflect opened body layers.");
                Check(InnerGap(wound,"fascia") < .0001f, "Uncut fascia must remain closed.");
                ValidateRegistration(wound,runner.Body);
                Capture(camera,target,IncisionPath);

                Feed(runner,"open_fascia"); Feed(runner,"split_muscle"); Feed(runner,"open_peritoneum"); Feed(runner,"deliver_appendix");
                wound.Apply(runner.Body);
                Check(runner.Body.Get("appendix","delivered") == 1 && runner.Body.Get("peritoneum","tentedBeforeCut") == 1, "Delivery fixture did not satisfy physical exposure/tenting.");
                Check(InnerGap(wound,"muscle") >= .014f && InnerGap(wound,"peritoneum") > .01f, "Deep wound layers failed to update from body state.");
                var appendix = BuildAtlasAppendix(preview,roots);
                appendix.SetActive(runner.Body.Get("appendix","delivered") > 0);
                // The visual lift comes from the measured fact. The mesh is explicitly display-fitted,
                // not passed off as native patient registration or live deformation evidence.
                appendix.transform.position += Vector3.back*(float)runner.Body.Get("appendix","liftMm")*.001f;
                title.text = "APPENDIX DELIVERED";
                Capture(camera,target,DeliveryPath);
                Check(runner.Mistakes.Count == 0,"Ideal preview input unexpectedly produced guardrail violations.");
                Debug.Log("SCALPAL_OPEN_SURGERY_PREVIEW_VALIDATED incision="+IncisionPath+" delivery="+DeliveryPath+
                    " actualAtlas=appendix bodyEvents="+runner.Body.Log.Count+" registrationHides=true source=synthetic_editor_virtual_camera headset=false");
            }
            finally
            {
                // Runtime presentation owns generated meshes/materials. Dispose before closing the
                // Editor scene so its runtime OnDestroy does not call delayed Destroy in edit mode.
                foreach (var root in roots) if (root)
                {
                    foreach (var wound in root.GetComponentsInChildren<OpenWoundView>(true))
                    {
                        foreach (var filter in wound.GetComponentsInChildren<MeshFilter>(true))
                            if (filter.sharedMesh && !AssetDatabase.Contains(filter.sharedMesh)) owned.Add(filter.sharedMesh);
                        foreach (var renderer in wound.GetComponentsInChildren<Renderer>(true))
                            foreach (var material in renderer.sharedMaterials)
                                if (material && !AssetDatabase.Contains(material)) owned.Add(material);
                    }
                }
                foreach (var item in owned) if (item) Object.DestroyImmediate(item);
                if (target) { target.Release(); Object.DestroyImmediate(target); }
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }
        static void Feed(CaseRunner runner, string id)
        {
            var step=runner.Procedure.steps.Single(s=>s.id==id);
            foreach(var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
        }
        static GameObject NewRoot(string name, Scene scene, List<GameObject> roots)
        {
            var go=new GameObject(name); SceneManager.MoveGameObjectToScene(go,scene); roots.Add(go); return go;
        }
        static void AddLight(Scene scene,List<GameObject> roots,string name,Vector3 position,float intensity,Color color)
        {
            var go=NewRoot(name,scene,roots); go.transform.position=position; go.transform.LookAt(Vector3.zero);
            var light=go.AddComponent<Light>(); light.type=LightType.Directional; light.intensity=intensity; light.color=color;
        }
        static TextMesh Label(Scene scene,List<GameObject> roots,Camera camera,string text,Vector3 position,float size)
        {
            var go=NewRoot("PreviewLabel",scene,roots); go.transform.position=camera.ViewportToWorldPoint(position); go.transform.rotation=camera.transform.rotation;
            var label=go.AddComponent<TextMesh>(); label.text=text; label.fontSize=56; label.characterSize=size*14f/label.fontSize;
            label.anchor=TextAnchor.MiddleLeft; label.color=new Color(.85f,.93f,.97f);
            var bounds=go.GetComponent<Renderer>().bounds;
            float maxWidth=camera.orthographicSize*2*camera.aspect*.83f;
            if(bounds.size.x>maxWidth) label.characterSize*=maxWidth/bounds.size.x;
            return label;
        }
        static GameObject BuildAtlasAppendix(Scene scene,List<GameObject> roots)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(AtlasPath);
            if(!prefab) throw new InvalidOperationException("Missing imported anatomy prefab: "+AtlasPath);
            var part=prefab.GetComponentsInChildren<AnatomyPart>(true).Single(p=>p.stableId=="appendix");
            var root=NewRoot("ActualAtlasAppendix_DisplayFitted",scene,roots);
            var renderers=new List<MeshRenderer>();
            foreach(var source in part.GetComponentsInChildren<MeshFilter>(true))
            {
                if(!source.sharedMesh) continue;
                var go=new GameObject(source.name); go.transform.SetParent(root.transform,false);
                go.transform.position=source.transform.position; go.transform.rotation=source.transform.rotation; go.transform.localScale=source.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh=source.sharedMesh;
                var renderer=go.AddComponent<MeshRenderer>(); renderer.sharedMaterials=source.GetComponent<Renderer>().sharedMaterials; renderers.Add(renderer);
            }
            Check(renderers.Count>0,"Imported appendix has no meshes.");
            Bounds bounds=BoundsOf(renderers);
            // Preserve proportions, with a stated display fit rather than inventing physical scale.
            Vector3 longest=bounds.size.z>bounds.size.x && bounds.size.z>bounds.size.y?Vector3.forward:bounds.size.y>bounds.size.x?Vector3.up:Vector3.right;
            root.transform.rotation=Quaternion.FromToRotation(longest,Vector3.right);
            bounds=BoundsOf(renderers); float extent=Mathf.Max(bounds.size.x,Mathf.Max(bounds.size.y,bounds.size.z));
            Check(extent>0,"Imported appendix has degenerate geometry."); root.transform.localScale=Vector3.one*(.061f/extent);
            bounds=BoundsOf(renderers); root.transform.position-=bounds.center;
            return root;
        }
        static Bounds BoundsOf(List<MeshRenderer> renderers)
        {
            var bounds=renderers[0].bounds; for(int i=1;i<renderers.Count;i++) bounds.Encapsulate(renderers[i].bounds); return bounds;
        }
        static float InnerGap(OpenWoundView wound,string id)
        {
            var mesh=wound.GetComponentsInChildren<MeshFilter>(true).Single(f=>f.name==id+"RightLip").sharedMesh;
            return Mathf.Abs(mesh.vertices[(64/2)*5+1].y)*2;
        }
        static void ValidateClosed(OpenWoundView wound)
        {
            foreach(var id in new[]{"skin","fat","fascia","muscle","peritoneum"}) Check(InnerGap(wound,id)<.0001f,"Initial layer is unexpectedly open: "+id);
        }
        static void ValidateRegistration(OpenWoundView wound,BodyState body)
        {
            wound.SetRegistrationValid(false); wound.Apply(body);
            Check(wound.GetComponentsInChildren<Renderer>(true).All(r=>!r.gameObject.activeInHierarchy),"Invalid registration leaves wound geometry visible.");
            wound.SetRegistrationValid(true); wound.Apply(body);
            Check(wound.GetComponentsInChildren<Renderer>(true).Any(r=>r.gameObject.activeInHierarchy),"Valid registration did not restore wound geometry.");
        }
        static void Capture(Camera camera,RenderTexture target,string path)
        {
            var previous=RenderTexture.active; Texture2D image=null;
            try
            {
                camera.Render(); RenderTexture.active=target; image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
                image.ReadPixels(new Rect(0,0,target.width,target.height),0,0); image.Apply(); File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally { RenderTexture.active=previous; if(image) Object.DestroyImmediate(image); }
            Check(File.Exists(path)&&new FileInfo(path).Length>10000,"Preview image is missing or suspiciously empty: "+path);
        }
        static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    }
}
