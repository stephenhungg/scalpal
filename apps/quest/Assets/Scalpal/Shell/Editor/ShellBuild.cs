using System;
using System.IO;
using System.Linq;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
using Scalpal.EncounterOffice;
using Scalpal.EncounterOffice.Editor;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Preop;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Shell.Editor
{
    public static class ShellBuild
    {
        public const string Root="Assets/Scalpal/Shell";
        public const string ScenePath=Root+"/Scenes/Launch.unity";
        const string Office="Assets/Scalpal/EncounterOffice";
        [MenuItem("Scalpal/Shell/Prepare Launch and Explore")]
        public static void Prepare()
        {
            Directory.CreateDirectory(Root+"/Art/Materials");Directory.CreateDirectory(Root+"/Scenes");AssetDatabase.Refresh();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var brand=ScalpalBrandBuild.Prepare();
            var root=new GameObject("ScalpalHub");var hub=root.AddComponent<HubController>();
            hub.brand=brand;
            hub.service=root.AddComponent<ScalpalPreopService>();
            var origin=new GameObject("ShellTrackingOrigin").transform;
            var head=new GameObject("TrackedHeadCamera").AddComponent<Camera>();head.transform.SetParent(origin,false);head.transform.localPosition=new Vector3(0,1.6f,0);
            head.tag="MainCamera";head.nearClipPlane=.025f;head.farClipPlane=30;head.fieldOfView=58;head.stereoTargetEye=StereoTargetEyeMask.Both;head.gameObject.AddComponent<AudioListener>();
            var content=new GameObject("WorldLockedHub").transform;content.position=new Vector3(0,1.52f,0);hub.content=content;
            hub.input=origin.gameObject.AddComponent<ShellInput>();hub.input.head=head;hub.input.origin=origin;hub.input.content=content;
            RenderSettings.skybox=Sky();RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.75f,.70f,.83f);
            RenderSettings.fog=true;RenderSettings.fogColor=new Color(.83f,.76f,.83f);RenderSettings.fogMode=FogMode.Linear;RenderSettings.fogStartDistance=4;RenderSettings.fogEndDistance=15;
            var sun=new GameObject("SoftDaylight").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=.65f;sun.color=new Color(1,.93f,.83f);sun.shadows=LightShadows.None;sun.transform.rotation=Quaternion.Euler(40,-25,0);
            Decor(content);
            DialogueBoxBuild.PrepareStyle();
            EditorSceneManager.SaveScene(scene,ScenePath);
            EnsureSceneOrder();
            // EXT hand interaction drives our pointer/pinch actions; preserve all other feature settings.
            foreach(var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/XR/Settings/OpenXRPackageSettings.asset"))
            {
                if(asset.name=="MetaQuestFeature Android")
                {
                    // This OpenXR package makes eye tracking required whenever Quest Pro is
                    // targeted, even with its eye-gaze feature disabled. Quest 3S has no eye tracking.
                    var settings=new SerializedObject(asset);var devices=settings.FindProperty("targetDevices");
                    for(int i=0;i<devices.arraySize;i++)
                    {
                        var device=devices.GetArrayElementAtIndex(i);
                        if(device.FindPropertyRelative("manifestName").stringValue=="cambria")device.FindPropertyRelative("enabled").boolValue=false;
                    }
                    settings.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(asset);
                }
                if(asset.name!="HandInteractionProfile Android")continue;
                var serialized=new SerializedObject(asset);serialized.FindProperty("m_enabled").boolValue=true;serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(asset);
            }
            var meta=OVRProjectConfig.CachedProjectConfig;
            meta.handTrackingSupport=OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            OVRProjectConfig.CommitProjectConfig(meta);
            // Transition shaders are found by name at runtime; keep explicit build references.
            var graphics=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var shaders=graphics.FindProperty("m_AlwaysIncludedShaders");
            foreach(var name in new[]{"Scalpal/Shell/Fade"})
            {
                var shader=Shader.Find(name);if(!shader)throw new InvalidOperationException("Missing shell shader "+name);
                bool exists=false;for(int i=0;i<shaders.arraySize;i++)exists|=shaders.GetArrayElementAtIndex(i).objectReferenceValue==shader;
                if(!exists){int i=shaders.arraySize;shaders.InsertArrayElementAtIndex(i);shaders.GetArrayElementAtIndex(i).objectReferenceValue=shader;}
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();Debug.Log("SCALPAL_SHELL_PREPARED launchFirst=true");
        }
        public static void EnsureSceneOrder()
        {
            var required=new[]{ScenePath,EncounterOfficeBuild.ScenePath,"Assets/Scalpal/Quest/Scenes/NativeSession.unity","Assets/Scalpal/Recap/Scenes/RunEnding.unity"};
            EditorBuildSettings.scenes=required.Select(p=>new EditorBuildSettingsScene(p,true))
                .Concat(EditorBuildSettings.scenes.Where(s=>!required.Contains(s.path)).GroupBy(s=>s.path).Select(g=>g.First())).ToArray();
        }
        static Material Material(string name,Color color)
        {
            string path=Root+"/Art/Materials/"+name+".mat";var value=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!value){value=new Material(Shader.Find("Standard"));AssetDatabase.CreateAsset(value,path);}value.color=color;value.SetFloat("_Glossiness",.25f);value.enableInstancing=true;EditorUtility.SetDirty(value);return value;
        }
        static Material Sky()
        {
            string path=Root+"/Art/Materials/hub_sky.mat";var value=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!value){value=new Material(Shader.Find("Scalpal/Shell/Hub Sky"));AssetDatabase.CreateAsset(value,path);}
            value.SetColor("_Top",new Color(.55f,.47f,.69f));value.SetColor("_Horizon",new Color(.86f,.77f,.81f));value.SetColor("_Bottom",new Color(.89f,.84f,.72f));EditorUtility.SetDirty(value);return value;
        }
        static void Decor(Transform parent)
        {
            var petals=Material("hub_petals",new Color(.85f,.64f,.77f));var butter=Material("hub_butter",new Color(.95f,.82f,.56f));
            petals.EnableKeyword("_EMISSION");petals.SetColor("_EmissionColor",new Color(.15f,.075f,.12f));
            butter.EnableKeyword("_EMISSION");butter.SetColor("_EmissionColor",new Color(.17f,.12f,.045f));
            var green=Material("hub_vines",new Color(.48f,.60f,.55f));
            var prototype=GameObject.CreatePrimitive(PrimitiveType.Sphere);var sphere=prototype.GetComponent<MeshFilter>().sharedMesh;UnityEngine.Object.DestroyImmediate(prototype);
            // Combine each floral material once. All petal shapes are original low-cost primitives.
            var pinkMeshes=new System.Collections.Generic.List<CombineInstance>();var centerMeshes=new System.Collections.Generic.List<CombineInstance>();
            for(int i=0;i<12;i++)
            {
                float sign=i%2==0?-1:1;float h=(i/2)*.40f-1.2f;float x=sign*(2.0f+Mathf.Sin(i*2)*.13f);float z=2.2f+Mathf.Cos(i)*.25f;
                var center=new Vector3(x,h,z);
                for(int j=0;j<5;j++)
                { float a=j*Mathf.PI*2/5;var offset=new Vector3(Mathf.Cos(a)*.055f,Mathf.Sin(a)*.055f,0);pinkMeshes.Add(new CombineInstance{mesh=sphere,transform=Matrix4x4.TRS(center+offset,Quaternion.Euler(0,0,a*Mathf.Rad2Deg),new Vector3(.105f,.048f,.022f))}); }
                centerMeshes.Add(new CombineInstance{mesh=sphere,transform=Matrix4x4.TRS(center+Vector3.back*.01f,Quaternion.identity,Vector3.one*.052f)});
                var vine=new GameObject("Vine").AddComponent<LineRenderer>();vine.transform.SetParent(parent,false);vine.useWorldSpace=false;vine.sharedMaterial=green;vine.startWidth=.009f;vine.endWidth=.003f;vine.positionCount=12;
                for(int j=0;j<12;j++)vine.SetPosition(j,center+new Vector3(Mathf.Sin(j*.3f+i)*.06f,-j*.06f,.02f));
            }
            Combined(parent,"Blossoms",pinkMeshes.ToArray(),petals);Combined(parent,"Pollen",centerMeshes.ToArray(),butter);
            var drifting=parent.gameObject.AddComponent<HubBlossoms>();drifting.petal=sphere;drifting.material=petals;
        }
        static void Combined(Transform parent,string name,CombineInstance[] parts,Material material)
        {
            string path=Root+"/Art/Materials/"+name+".asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(!mesh){mesh=new Mesh{name=name};AssetDatabase.CreateAsset(mesh,path);}mesh.Clear();mesh.CombineMeshes(parts);EditorUtility.SetDirty(mesh);
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(parent,false);obj.GetComponent<MeshFilter>().sharedMesh=mesh;obj.GetComponent<Renderer>().sharedMaterial=material;
            obj.GetComponent<Renderer>().shadowCastingMode=ShadowCastingMode.Off;obj.GetComponent<Renderer>().receiveShadows=false;
        }
        [MenuItem("Scalpal/Shell/Capture Mono Previews")]
        public static void CapturePreviews()
        {
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);var hub=UnityEngine.Object.FindFirstObjectByType<HubController>();hub.Initialize();
            string path=Environment.GetEnvironmentVariable("SCALPAL_SHELL_PREVIEWS");
            if(string.IsNullOrEmpty(path))path=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../assets/previews/shell"));
            Directory.CreateDirectory(path);
            // Show the corrected laser: right controller aim pose near the hip, aimed at Start (accent hover + reticle).
            var start=hub.content.GetComponentsInChildren<ShellButton>(true).Single(button=>button.name=="Button_Start");
            AimPreview(hub.input,start.transform.position);
            Capture(hub.input.head,Path.Combine(path,"launch.png"));
            ScalpalAim.Override=null;ScalpalAim.Clock=null;hub.input.Release();
            var bundle=JsonUtility.FromJson<ScalpalBundle>(Resources.Load<TextAsset>("scalpal_bundle").text);hub.service.Configure("http://127.0.0.1:8787",true);hub.Reload();hub.Enter();hub.input.head.fieldOfView=90;
            Capture(hub.input.head,Path.Combine(path,"explore.png"));
            hub.input.head.fieldOfView=100;hub.input.head.transform.rotation=Quaternion.Euler(0,20,0);
            hub.Model.Select("patient-demo-multi-source");hub.BriefLoaded(bundle.cases.First(c=>c.patientId=="patient-demo-multi-source").brief);
            Capture(hub.input.head,Path.Combine(path,"explore-detail.png"));
            // Transition title card: brand overlay type on the black stereo fade.
            var head=hub.input.head;var flags=head.clearFlags;var background=head.backgroundColor;
            var title=new GameObject("TransitionTitlePreview").AddComponent<ShellTransition>();
            try
            {
                hub.content.gameObject.SetActive(false);head.clearFlags=CameraClearFlags.SolidColor;head.backgroundColor=Color.black;head.fieldOfView=80;head.transform.rotation=Quaternion.identity;
                title.CreateTitle("Office · Priya Ramaswamy, 40\nRight lower abdominal pain since last night");
                Capture(head,Path.Combine(path,"transition-title.png"));
            }
            finally { UnityEngine.Object.DestroyImmediate(title.gameObject);hub.content.gameObject.SetActive(true);head.clearFlags=flags;head.backgroundColor=background; }
            Debug.Log("SCALPAL_SHELL_PREVIEWS_OK monoEditorOnly=true path="+path);
        }
        // Editor-only stand-in for a tracked right controller: aim pose 0.45 m right/below the head, pointed at the target.
        public static void AimPreview(ShellInput input,Vector3 worldTarget)
        {
            var origin=input.origin;var head=input.head.transform;
            var tip=origin.InverseTransformPoint(head.position+head.right*.20f+Vector3.down*.30f+head.forward*.28f);
            var aim=Quaternion.LookRotation(origin.InverseTransformDirection(worldTarget-origin.TransformPoint(tip)),Vector3.up);
            float clock=0;ScalpalAim.Clock=()=>clock+=.02f;
            ScalpalAim.Override=hand=>hand==1?new ScalpalPointerSample{kind=ScalpalPointerKind.Controller,position=tip,rotation=aim,select=0}:default;
            Physics.SyncTransforms();input.StepPointers();
        }
        static void Capture(Camera camera,string path)
        {
            foreach(var text in UnityEngine.Object.FindObjectsByType<ScalpalTextFit>(FindObjectsInactive.Include,FindObjectsSortMode.None))text.Fit();
            var target=new RenderTexture(2200,1400,24);var previous=RenderTexture.active;var image=new Texture2D(2200,1400,TextureFormat.RGB24,false);
            try { camera.stereoTargetEye=StereoTargetEyeMask.None;camera.targetTexture=target;camera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,2200,1400),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG()); }
            finally {camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);}
        }
        public static void PrepareAndVerify() { Prepare();ShellValidation.Run();CapturePreviews(); }
        public static void BuildWithPreviews() { Prepare();CapturePreviews();Build(); }
        [MenuItem("Scalpal/Shell/Build Android")]
        public static void Build()
        {
            EnsureSceneOrder();ShellValidation.Run();
            // Validation intentionally exercises/mutates a scene instance. Build the committed clean scene.
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var output=Environment.GetEnvironmentVariable("SCALPAL_SHELL_APK");
            if(string.IsNullOrEmpty(output)||!Path.IsPathRooted(output))throw new InvalidOperationException("SCALPAL_SHELL_APK requires an absolute APK path.");
            string version=PlayerSettings.bundleVersion, product=PlayerSettings.productName;int code=PlayerSettings.Android.bundleVersionCode;
            var preloads=PlayerSettings.GetPreloadedAssets();var http=PlayerSettings.insecureHttpOption;var aab=EditorUserBuildSettings.buildAppBundle;
            try
            {
                PlayerSettings.bundleVersion="0.6.0-shell";PlayerSettings.Android.bundleVersionCode=11;PlayerSettings.productName="Scalpal";
                PlayerSettings.insecureHttpOption=InsecureHttpOption.DevelopmentOnly;EditorUserBuildSettings.buildAppBundle=false;Directory.CreateDirectory(Path.GetDirectoryName(output));
                var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=EditorBuildSettings.scenes.Where(s=>s.enabled).Select(s=>s.path).ToArray(),target=BuildTarget.Android,locationPathName=output,options=BuildOptions.Development });
                if(result.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("Shell Android build failed: "+result.summary.result);
                Debug.Log("SCALPAL_SHELL_APK_OK bytes="+new FileInfo(output).Length);
            }
            finally {PlayerSettings.bundleVersion=version;PlayerSettings.Android.bundleVersionCode=code;PlayerSettings.productName=product;PlayerSettings.SetPreloadedAssets(preloads);PlayerSettings.insecureHttpOption=http;EditorUserBuildSettings.buildAppBundle=aab;AssetDatabase.SaveAssets();}
        }
    }
}
