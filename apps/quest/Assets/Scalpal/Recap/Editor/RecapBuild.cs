using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
using TMPro;

namespace Scalpal.Recap.Editor
{
    public static class RecapBuild
    {
        public const string Root = "Assets/Scalpal/Recap";
        public const string ScenePath = Root + "/Scenes/RunEnding.unity";
        const string Office = "Assets/Scalpal/EncounterOffice";
        static ScalpalBrand brand;
        // Seated/standing viewer in the recap rig's tracking space (Recap Camera below).
        public static readonly Vector3 Viewer = new Vector3(0,1.6f,0);
        [MenuItem("Scalpal/Recap/Prepare and Verify")]
        public static void PrepareAndVerify() { Prepare(); RecapValidation.Run(); }
        public static void VerifyAndPreview() { PrepareAndVerify(); CapturePreviews(); }
        [MenuItem("Scalpal/Recap/Prepare Scene")]
        public static void Prepare()
        {
            Directory.CreateDirectory(Root+"/Scenes");Directory.CreateDirectory(Root+"/Materials");
            AssetDatabase.Refresh();
            brand=ScalpalBrandBuild.Prepare();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Recap Camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,1.6f,0);camera.fieldOfView=52;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.18f,.18f);camera.nearClipPlane=.05f;camera.gameObject.AddComponent<AudioListener>();
            var rig=new GameObject("Recap XR origin").AddComponent<RecapInput>();camera.transform.SetParent(rig.transform,true);rig.origin=rig.transform;rig.head=camera.transform;
            rig.left=new GameObject("Left controller").transform;rig.left.SetParent(rig.transform);rig.right=new GameObject("Right controller").transform;rig.right.SetParent(rig.transform);
            // Pointer rays are created at runtime from each controller's aim pose (ScalpalPointerHand).
            var c=new GameObject("Run ending").AddComponent<RecapController>();rig.controller=c;
            var root=new GameObject("World-locked glass").transform;root.position=new Vector3(0,1.55f,2.05f);
            Surface(root,"Glass",2.65f,1.3f,Vector3.zero,brand.glass);
            var panel=root.gameObject.AddComponent<RecapPanel>();c.panel=panel;panel.controller=c;
            // Scores: diagnosis and surgery, each a label over its value.
            panel.scoreRoot=new GameObject("Scores");panel.scoreRoot.transform.SetParent(root,false);
            panel.diagnosisLabel=Text(panel.scoreRoot.transform,"Diagnosis label","Diagnosis",ScalpalTextRole.Label,-1.2f,.47f,.70f,.11f,.030f);
            panel.diagnosisValue=Text(panel.scoreRoot.transform,"Diagnosis score","82 / 100",ScalpalTextRole.Title,-1.2f,.36f,.70f,.22f,.080f);
            panel.surgeryLabel=Text(panel.scoreRoot.transform,"Surgery label","Surgery · assisted",ScalpalTextRole.Label,-1.2f,.06f,.70f,.11f,.030f);
            panel.surgeryValue=Text(panel.scoreRoot.transform,"Surgery score","76 / 100",ScalpalTextRole.Title,-1.2f,-.05f,.70f,.22f,.080f);
            // Robot replay: the coach's streamed video in a grey-glow frame, one result line, one optional provenance line.
            panel.robotRoot=new GameObject("Robot replay");panel.robotRoot.transform.SetParent(root,false);
            var v=c.gameObject.AddComponent<RecapVideo>();c.replay=v;v.controller=c;
            panel.videoSurface=new GameObject("Robot video");panel.videoSurface.transform.SetParent(panel.robotRoot.transform,false);
            var glow=Surface(panel.videoSurface.transform,"Glow frame",1.24f,.715f,new Vector3(.42f,.17f,-.008f),brand.button);
            ScalpalBrand.Tint(glow.GetComponent<Renderer>(),new MaterialPropertyBlock(),ScalpalBrand.ButtonTint,true);
            v.robotPlayer=Video(panel.videoSurface.transform,"Robot",.42f,.17f);
            panel.robotLine=Text(panel.robotRoot.transform,"Robot result","Robot · Mark McBurney incision · Success · 24 mm",ScalpalTextRole.Body,.42f,-.21f,1.64f,.10f,.026f,TextAnchor.UpperCenter);
            panel.robotNote=Text(panel.robotRoot.transform,"Robot provenance","Learned from 1 headset + 40 synthetic demos",ScalpalTextRole.Body,.42f,-.32f,1.64f,.09f,.022f,TextAnchor.UpperCenter);
            panel.emptyLine=Text(root,"No result",RecapPanel.Empty,ScalpalTextRole.Label,0,.12f,2.2f,.12f,.034f,TextAnchor.UpperCenter);
            panel.exploreButton=Button(root,c,"Choose another patient","explore",-.55f,-.52f,.92f);
            panel.retryButton=Button(root,c,"Retry surgery","retry",.55f,-.52f,.80f);
            var light=new GameObject("Soft daylight").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(35,-30,0);light.intensity=.75f;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.7f,.8f,.75f);
            Flowers(root);
            // Brand floors (32 mm/m labels, 24 mm/m body) for each string's real distance from the viewer.
            ScalpalBrandLayout.SizeForViewer(root,Viewer);
            // Sized with the longest copy; the controller writes the real lines at runtime.
            panel.robotLine.text=RecapPanel.Pending;panel.robotNote.text="";panel.surgeryLabel.text="Surgery";
            panel.emptyLine.gameObject.SetActive(false);panel.videoSurface.SetActive(false);panel.robotNote.gameObject.SetActive(false);
            EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();
            Debug.Log("SCALPAL_RECAP_PREPARED "+ScenePath);
        }
        static TextMeshPro Text(Transform parent,string name,string content,ScalpalTextRole role,float x,float y,float w,float h,float size,TextAnchor anchor=TextAnchor.UpperLeft)
            => brand.Text(parent,name,content,role,new Vector3(x,y,-.025f),size*1.2f,w,h,anchor);
        static GameObject Surface(Transform parent,string name,float w,float h,Vector3 pos,Material mat)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Quad);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=pos;go.transform.localScale=new Vector3(w,h,1);UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=mat;return go;
        }
        static RecapButton Button(Transform root,RecapController c,string label,string action,float x,float y,float width)
        {
            var holder=new GameObject("Button "+action).transform;holder.SetParent(root,false);holder.localPosition=new Vector3(x,y,-.04f);
            var go=Surface(holder,"Hit surface",width,.10f,Vector3.zero,brand.button);var hit=go.AddComponent<BoxCollider>();hit.size=new Vector3(1,1,.2f);
            var b=go.AddComponent<RecapButton>();b.controller=c;b.action=action;b.label=Text(holder,"Label",label,ScalpalTextRole.Label,0,0,width-.035f,.086f,.025f,TextAnchor.MiddleCenter);
            b.label.transform.SetParent(go.transform,true);return b;
        }
        static VideoPlayer Video(Transform parent,string name,float x,float y)
        {
            string rtPath=Root+"/Materials/"+name+".renderTexture";var rt=AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
            if(!rt){rt=new RenderTexture(960,540,0){name=name};AssetDatabase.CreateAsset(rt,rtPath);}
            string path=Root+"/Materials/"+name+"Video.mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!mat){mat=new Material(Shader.Find("Unlit/Texture"));AssetDatabase.CreateAsset(mat,path);}mat.mainTexture=rt;
            var go=Surface(parent,name+" video",1.2f,.675f,new Vector3(x,y,-.015f),mat);var player=go.AddComponent<VideoPlayer>();player.playOnAwake=false;player.isLooping=true;player.audioOutputMode=VideoAudioOutputMode.None;player.renderMode=VideoRenderMode.RenderTexture;player.targetTexture=rt;player.waitForFirstFrame=true;player.aspectRatio=VideoAspectRatio.FitInside;return player;
        }
        static void Flowers(Transform parent)
        {
            var rose=AssetDatabase.LoadAssetAtPath<Material>(Office+"/Materials/art_Office_FlowerRose.mat");
            for(int side=-1;side<=1;side+=2) for(int bloom=0;bloom<3;bloom++) for(int petal=0;petal<5;petal++)
            {
                float angle=petal*Mathf.PI*2/5;var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);go.name="Floral petal";go.transform.SetParent(parent,false);
                go.transform.localPosition=new Vector3(side*(1.36f+bloom*.05f)+Mathf.Cos(angle)*.06f,-.42f+bloom*.36f+Mathf.Sin(angle)*.06f,.07f);
                go.transform.localScale=new Vector3(.09f,.12f,.025f);go.transform.localRotation=Quaternion.Euler(0,0,petal*72);UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=rose;
            }
        }
        [MenuItem("Scalpal/Recap/Capture Previews")]
        public static void CapturePreviews()
        {
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var c=UnityEngine.Object.FindFirstObjectByType<RecapController>();
            var sample=RunResultContract.Parse(File.ReadAllText(Root+"/Fixtures/sample-run-result.json"));
            // Preview uses the real display formatting with a fixture robot reply; no network or video decode.
            typeof(RecapController).GetProperty("Result").GetSetMethod(true).Invoke(c,new object[]{sample});
            string dir=Environment.GetEnvironmentVariable("SCALPAL_RECAP_PREVIEW")??"/tmp/scalpal-recap-preview";Directory.CreateDirectory(dir);
            c.panel.Refresh();Capture(dir+"/pending.png");
            c.ApplyRobot("{\"status\":\"ready\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":true,\"pathErrorMm\":6.2,\"demos\":{\"human\":0,\"synthetic\":40},\"synthetic\":true,\"videoUrl\":null}");
            c.panel.videoSurface.SetActive(true);Capture(dir+"/ready.png");
            Debug.Log("SCALPAL_RECAP_PREVIEWS "+dir);
        }
        public static void Capture(string path)
        {
            var camera=Camera.main;var rt=new RenderTexture(1800,1200,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
            var image=new Texture2D(1800,1200,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1800,1200),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());camera.targetTexture=null;RenderTexture.active=null;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
