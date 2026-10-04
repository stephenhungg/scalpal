using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using Scalpal.EncounterOffice;
using Scalpal.Voice;

namespace Scalpal.Recap.Editor
{
    public static class RecapBuild
    {
        public const string Root = "Assets/Scalpal/Recap";
        public const string ScenePath = Root + "/Scenes/RunEnding.unity";
        const string Office = "Assets/Scalpal/EncounterOffice";
        static Material glass, button, textMaterial;
        static Font font;
        [MenuItem("Scalpal/Recap/Prepare and Verify")]
        public static void PrepareAndVerify() { Prepare(); RecapValidation.Run(); }
        public static void VerifyAndPreview() { PrepareAndVerify(); CapturePreviews(); }
        [MenuItem("Scalpal/Recap/Prepare Scene")]
        public static void Prepare()
        {
            Directory.CreateDirectory(Root+"/Scenes");Directory.CreateDirectory(Root+"/Materials");
            AssetDatabase.Refresh();
            glass=AssetDatabase.LoadAssetAtPath<Material>(Office+"/Materials/office_glass_card.mat");
            button=AssetDatabase.LoadAssetAtPath<Material>(Office+"/Materials/office_glass_button.mat");
            textMaterial=AssetDatabase.LoadAssetAtPath<Material>(Office+"/Materials/office_world_text_Inter-Regular.mat");
            font=AssetDatabase.LoadAssetAtPath<Font>(Office+"/Fonts/Inter-Regular.ttf");
            if(!glass||!button||!font||!textMaterial)throw new InvalidOperationException("Office glass and Inter assets required.");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Recap Camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,1.6f,0);camera.fieldOfView=52;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.18f,.18f);camera.nearClipPlane=.05f;camera.gameObject.AddComponent<AudioListener>();
            var rig=new GameObject("Recap XR origin").AddComponent<RecapInput>();camera.transform.SetParent(rig.transform,true);rig.origin=rig.transform;rig.head=camera.transform;
            rig.left=new GameObject("Left controller").transform;rig.left.SetParent(rig.transform);rig.right=new GameObject("Right controller").transform;rig.right.SetParent(rig.transform);
            rig.leftRay=Ray(rig.left);rig.rightRay=Ray(rig.right);
            var c=new GameObject("Run ending").AddComponent<RecapController>();rig.controller=c;c.voice=c.gameObject.AddComponent<QuestJarvisVoice>();
            c.previewResult=AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/Fixtures/sample-run-result.json");
            var root=new GameObject("World-locked glass").transform;root.position=new Vector3(0,1.6f,2.5f);
            Surface(root,"Glass",2.65f,1.65f,Vector3.zero,glass);
            var panel=root.gameObject.AddComponent<RecapPanel>();c.panel=panel;panel.controller=c;
            panel.title=Text(root,"Title","Your hands, reimagined",-.0f,.70f,2.35f,.09f,.052f,TextAnchor.UpperCenter);
            panel.subtitle=Text(root,"Subtitle","Explore · Office · OR · Replay · Recap",0,.58f,2.35f,.08f,.025f,TextAnchor.UpperCenter);
            panel.status=Text(root,"Status","Run ending preview",-1.19f,.48f,2.38f,.12f,.024f);
            var v=c.gameObject.AddComponent<RecapVideo>();c.replay=v;v.panel=panel;v.sampleClip=AssetDatabase.LoadAssetAtPath<VideoClip>(Root+"/Media/recap-sample.mp4");
            panel.videoRoot=new GameObject("Replay stage");panel.videoRoot.transform.SetParent(root,false);
            v.sourcePlayer=Video(panel.videoRoot.transform,"Source",-.60f,.07f);
            v.robotPlayer=Video(panel.videoRoot.transform,"Shadow",.60f,.07f);
            panel.sourceLabel=Text(panel.videoRoot.transform,"Source label","Your recording · unavailable in sample mode",-1.16f,.35f,1.12f,.05f,.023f);
            panel.robotLabel=Text(panel.videoRoot.transform,"Robot label","Shadow hand · SYNTHETIC SAMPLE",.04f,.35f,1.12f,.05f,.023f);
            // Explicit empty-source caption remains visible; never display the sample as the learner's recording.
            panel.sourcePlaceholder=Text(panel.videoRoot.transform,"No source","Your recorded segment\nappears here when available.",-1.11f,.14f,1.0f,.15f,.027f).gameObject;
            panel.timeline=Text(panel.videoRoot.transform,"Timeline","Paused · 0 / 20 s",-1.16f,-.23f,2.32f,.13f,.024f);
            Button(panel.videoRoot.transform,c,"Play / Pause","play",-.94f,-.45f,.40f);
            Button(panel.videoRoot.transform,c,"− 5 seconds","back",-.49f,-.45f,.40f);Button(panel.videoRoot.transform,c,"+ 5 seconds","forward",-.04f,-.45f,.40f);
            Button(panel.videoRoot.transform,c,"Seek in clip","scrub",.72f,-.45f,.95f);
            panel.errorsNext=Button(panel.videoRoot.transform,c,"›","errors_next",1.10f,-.57f,.16f);
            panel.errors=new RecapButton[3];for(int i=0;i<3;i++){panel.errors[i]=Button(panel.videoRoot.transform,c,"Error timestamp","error",-.85f+i*.70f,-.57f,.65f);panel.errors[i].index=i;}
            panel.scoreRoot=new GameObject("Scorecards stage");panel.scoreRoot.transform.SetParent(root,false);
            panel.leftCard=Text(panel.scoreRoot.transform,"Clinical reasoning","CLINICAL REASONING",-1.16f,.35f,1.10f,.63f,.028f);
            panel.rightCard=Text(panel.scoreRoot.transform,"Procedural skill","PROCEDURAL SKILL",.04f,.35f,1.10f,.63f,.028f);
            panel.feedback=Text(panel.scoreRoot.transform,"Fact feedback","Logged feedback",-1.16f,-.32f,2.32f,.34f,.023f);
            panel.reflectionRoot=new GameObject("Reflection stage");panel.reflectionRoot.transform.SetParent(root,false);
            panel.reflection=Text(panel.reflectionRoot.transform,"Reflection","How did that feel?",-1.08f,.23f,2.16f,.68f,.040f);
            panel.continueButton=Button(root,c,"Reflect on this run","continue",.83f,-.71f,.72f);
            panel.exploreButton=Button(root,c,"Choose another patient","explore",-.53f,-.71f,.92f);
            panel.retryButton=Button(root,c,"Retry surgery","retry",.48f,-.71f,.88f);
            var demo=Button(root,c,"Demo mode: OFF","demo",-.78f,-.71f,.86f);panel.demoLabel=demo.label;
            // Operator toggle is on the replay stage only, avoiding overlap with final navigation.
            demo.transform.SetParent(panel.videoRoot.transform,true);
            var light=new GameObject("Soft daylight").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(35,-30,0);light.intensity=.75f;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.7f,.8f,.75f);
            Flowers(root);
            panel.scoreRoot.SetActive(false);panel.reflectionRoot.SetActive(false);panel.exploreButton.gameObject.SetActive(false);panel.retryButton.gameObject.SetActive(false);
            foreach(var fit in root.GetComponentsInChildren<EncounterOfficeText>(true))fit.Fit();
            EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();
            Debug.Log("SCALPAL_RECAP_PREPARED "+ScenePath);
        }
        static TextMesh Text(Transform parent,string name,string content,float x,float y,float w,float h,float size,TextAnchor anchor=TextAnchor.UpperLeft)
        {
            var t=new GameObject(name).AddComponent<TextMesh>();t.transform.SetParent(parent,false);t.transform.localPosition=new Vector3(x,y,-.025f);t.font=font;t.richText=false;t.fontSize=48;t.characterSize=size*.25f;t.anchor=anchor;t.text=content;t.color=new Color(.95f,.96f,.91f);t.GetComponent<Renderer>().sharedMaterial=textMaterial;
            var fit=t.gameObject.AddComponent<EncounterOfficeText>();fit.maximumWidth=w;fit.maximumHeight=h;fit.preferredCharacterSize=t.characterSize;fit.Fit();return t;
        }
        static GameObject Surface(Transform parent,string name,float w,float h,Vector3 pos,Material mat)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Quad);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=pos;go.transform.localScale=new Vector3(w,h,1);UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=mat;return go;
        }
        static RecapButton Button(Transform root,RecapController c,string label,string action,float x,float y,float width)
        {
            var holder=new GameObject("Button "+action).transform;holder.SetParent(root,false);holder.localPosition=new Vector3(x,y,-.04f);
            var go=Surface(holder,"Hit surface",width,.085f,Vector3.zero,button);var hit=go.AddComponent<BoxCollider>();hit.size=new Vector3(1,1,.2f);
            var b=go.AddComponent<RecapButton>();b.controller=c;b.action=action;b.label=Text(holder,"Label",label,0,0,width-.025f,.068f,.025f,TextAnchor.MiddleCenter);
            b.label.transform.SetParent(go.transform,true);return b;
        }
        static VideoPlayer Video(Transform parent,string name,float x,float y)
        {
            string rtPath=Root+"/Materials/"+name+".renderTexture";var rt=AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
            if(!rt){rt=new RenderTexture(640,480,0){name=name};AssetDatabase.CreateAsset(rt,rtPath);}
            string path=Root+"/Materials/"+name+"Video.mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!mat){mat=new Material(Shader.Find("Unlit/Texture"));AssetDatabase.CreateAsset(mat,path);}mat.mainTexture=rt;
            var go=Surface(parent,name+" video",.68f,.51f,new Vector3(x,y,-.015f),mat);var player=go.AddComponent<VideoPlayer>();player.playOnAwake=false;player.isLooping=false;player.audioOutputMode=VideoAudioOutputMode.None;player.renderMode=VideoRenderMode.RenderTexture;player.targetTexture=rt;player.waitForFirstFrame=true;player.aspectRatio=VideoAspectRatio.FitInside;return player;
        }
        static LineRenderer Ray(Transform parent)
        {
            var line=new GameObject("Pointer").AddComponent<LineRenderer>();line.transform.SetParent(parent,false);line.positionCount=2;line.startWidth=.002f;line.endWidth=.001f;line.sharedMaterial=button;line.enabled=false;return line;
        }
        static void Flowers(Transform parent)
        {
            var rose=AssetDatabase.LoadAssetAtPath<Material>(Office+"/Materials/art_Office_FlowerRose.mat");
            for(int side=-1;side<=1;side+=2) for(int bloom=0;bloom<3;bloom++) for(int petal=0;petal<5;petal++)
            {
                float angle=petal*Mathf.PI*2/5;var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);go.name="Floral petal";go.transform.SetParent(parent,false);
                go.transform.localPosition=new Vector3(side*(1.36f+bloom*.05f)+Mathf.Cos(angle)*.06f,-.48f+bloom*.40f+Mathf.Sin(angle)*.06f,.07f);
                go.transform.localScale=new Vector3(.09f,.12f,.025f);go.transform.localRotation=Quaternion.Euler(0,0,petal*72);UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=rose;
            }
        }
        [MenuItem("Scalpal/Recap/Capture Previews")]
        public static void CapturePreviews()
        {
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var c=UnityEngine.Object.FindFirstObjectByType<RecapController>();var sample=RunResultContract.Parse(c.previewResult.text);
            // Preview uses real display formatting, without network, microphone or sample-as-learner claims.
            typeof(RecapController).GetProperty("Result").GetSetMethod(true).Invoke(c,new object[]{sample});
            typeof(RecapVideo).GetProperty("Fallback").GetSetMethod(true).Invoke(c.replay,new object[]{true});
            c.panel.Refresh();
            c.panel.timeline.text="Paused  10.0 / 20.0 s\n"+RunResultContract.ReplayLabel(sample.replay,true);
            string framePath=Environment.GetEnvironmentVariable("SCALPAL_RECAP_SAMPLE_FRAME");
            if(!string.IsNullOrEmpty(framePath) && File.Exists(framePath))
            {
                var frame=new Texture2D(2,2);frame.LoadImage(File.ReadAllBytes(framePath));Graphics.Blit(frame,c.replay.robotPlayer.targetTexture);UnityEngine.Object.DestroyImmediate(frame);
            }
            string dir=Environment.GetEnvironmentVariable("SCALPAL_RECAP_PREVIEW")??"/tmp/scalpal-recap-preview";Directory.CreateDirectory(dir);
            Capture(dir+"/replay.png");
            typeof(RecapController).GetProperty("Phase").GetSetMethod(true).Invoke(c,new object[]{"scores"});c.panel.Refresh();Capture(dir+"/recap.png");
            Debug.Log("SCALPAL_RECAP_PREVIEWS "+dir);
        }
        static void Capture(string path)
        {
            var camera=Camera.main;var rt=new RenderTexture(1800,1200,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
            var image=new Texture2D(1800,1200,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1800,1200),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());camera.targetTexture=null;RenderTexture.active=null;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
