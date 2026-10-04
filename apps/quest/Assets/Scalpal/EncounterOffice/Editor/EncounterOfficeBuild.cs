using System;
using System.IO;
using System.Linq;
using Scalpal.Voice;
using Scalpal.Realtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.EncounterOffice.Editor
{
    public static class EncounterOfficeBuild
    {
        public const string Root = "Assets/Scalpal/EncounterOffice";
        public const string ScenePath = Root + "/Scenes/DiagnosisOffice.unity";
        public const string SurgeryScenePath = "Assets/Scalpal/Quest/Scenes/NativeSession.unity";
        static Material lilac, glass, glassBorder, glassButton;
        static readonly Color InkColor = new Color(.94f,.97f,.95f);
        [MenuItem("Scalpal/Encounter Office/Prepare Diagnosis Office")]
        public static void Prepare()
        {
            // Deliberately reuse current Android OpenXR project configuration without changing surgery build settings.
            Directory.CreateDirectory(Root + "/Scenes"); Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Prefabs"); AssetDatabase.Refresh();
            lilac = Material("office_ui_lilac", new Color(.83f,.79f,.91f));
            glass=Glass("office_glass_card",new Color(.055f,.082f,.086f,.78f));
            glassBorder=Glass("office_glass_border",new Color(.73f,.83f,.78f,.35f));
            glassButton=Glass("office_glass_button",new Color(.26f,.36f,.33f,.78f));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var office = Art("DoctorOffice", "BotanicalDoctorOffice");
            office.transform.position = Vector3.zero;
            var female = Art("PatientFemale", "GenericAdultFemaleTemplate"); var male = Art("PatientMale", "GenericAdultMaleTemplate");
            female.SetActive(false);male.SetActive(false);
            var systems = new GameObject("EncounterOfficeSystems");systems.SetActive(false);
            var session = systems.AddComponent<NativeEncounterSession>();
            var realtime = systems.AddComponent<QuestSessionBridge>();realtime.autoConnect=false;session.realtime=realtime;
            var voice = systems.AddComponent<QuestJarvisVoice>(); var speaker = systems.AddComponent<AudioSource>();
            speaker.playOnAwake = false; speaker.spatialBlend = 0;
            var voiceProps = new SerializedObject(voice); voiceProps.FindProperty("speaker").objectReferenceValue = speaker; voiceProps.ApplyModifiedPropertiesWithoutUndo();
            session.voice = voice;
            var patients = systems.AddComponent<EncounterPatientPresentation>(); patients.female = female; patients.male = male; patients.voice = voice; session.patient = patients;
            patients.stateLabel = Text(systems.transform, "PatientState", "Patient · waiting", new Vector3(0,1.65f,.2f), .022f, TextAnchor.MiddleCenter);
            patients.stateLabel.transform.localRotation = Quaternion.Euler(0,180,0);
            var origin = new GameObject("EncounterTrackingOrigin").transform;
            var rig = origin.gameObject.AddComponent<EncounterOfficeRig>(); rig.origin = origin; rig.session = session;
            var head = new GameObject("TrackedHeadCamera").AddComponent<Camera>(); head.transform.SetParent(origin,false); head.tag = "MainCamera";
            head.transform.SetLocalPositionAndRotation(new Vector3(0,1.2f,1.75f), Quaternion.Euler(0,180,0));
            head.nearClipPlane = .025f; head.farClipPlane = 25; head.clearFlags = CameraClearFlags.SolidColor; head.backgroundColor = new Color(.88f,.9f,.85f); head.stereoTargetEye = StereoTargetEyeMask.Both;
            head.gameObject.AddComponent<AudioListener>(); rig.head = head;
            rig.left = new GameObject("LeftTrackedController").transform; rig.left.SetParent(origin,false);
            rig.right = new GameObject("RightTrackedController").transform; rig.right.SetParent(origin,false);
            rig.leftRay = Ray(rig.left); rig.rightRay = Ray(rig.right);
            rig.talkHint = Text(rig.left,"TalkHint","Hold grip to talk",new Vector3(0,.075f,.06f),.024f,TextAnchor.MiddleCenter);
            var talkFit = rig.talkHint.GetComponent<EncounterOfficeText>(); talkFit.maximumWidth = .28f; talkFit.maximumHeight = .05f;
            rig.talkHint.gameObject.SetActive(false);
            var ui = new GameObject("EncounterVisualFallback").AddComponent<EncounterOfficePanel>(); ui.session = session;
            var left = Panel("InterviewConsole", new Vector3(.94f,1.48f,-.20f), new Vector3(0,1.2f,1.75f), new Vector2(.88f,1.1f)); left.SetParent(ui.transform,true);
            var right = Panel("FindingsConsole", new Vector3(-.94f,1.48f,-.20f), new Vector3(0,1.2f,1.75f), new Vector2(.88f,1.1f)); right.SetParent(ui.transform,true);
            ui.heading = Text(left,"Title","Scalpal",new Vector3(-.38f,.49f,-.026f),.034f);
            ui.status = Text(left,"Status","Choose a synthetic patient.",new Vector3(-.38f,.35f,-.026f),.024f);
            Button(left,ui,"Patients","page","patients",-.21f,.20f,.39f);
            Button(left,ui,"Reload list","reload_patients","",.21f,.20f,.39f);
            Button(left,ui,"History","page","history",-.28f,.10f,.25f); Button(left,ui,"Examine","page","exam",0,.10f,.25f); Button(left,ui,"Tests","page","tests",.28f,.10f,.25f);
            ui.options = new EncounterOfficeButton[4];
            for (int i=0;i<4;i++) ui.options[i] = Button(left,ui,"Question","option","",0,-i*.09f,.80f);
            Button(left,ui,"Back","previous","",-.315f,-.36f,.19f); Button(left,ui,"More","next","",-.105f,-.36f,.19f);
            Button(left,ui,"Voice","voice","",.105f,-.36f,.19f); Button(left,ui,"Stop","stop","",.315f,-.36f,.19f);
            Button(left,ui,"Present to Jarvis","attending","",-.105f,-.46f,.60f); Button(left,ui,"Refresh","refresh","",.315f,-.46f,.19f);
            Text(right,"FindingsTitle","Your findings",new Vector3(-.38f,.49f,-.026f),.034f);
            ui.chart = Text(right,"Chart","No examinations performed.",new Vector3(-.38f,.37f,-.026f),.024f);
            Button(right,ui,"Back","chart_previous","",-.28f,-.09f,.25f); Button(right,ui,"More","chart_next","",0,-.09f,.25f); Button(right,ui,"Findings / score","chart_toggle","",.28f,-.09f,.25f);
            ui.response = Text(right,"Response","Patient response appears here.",new Vector3(-.38f,-.17f,-.026f),.024f);
            Button(right,ui,"Back","response_previous","",-.21f,-.42f,.39f); Button(right,ui,"More","response_next","",.21f,-.42f,.39f);
            Button(right,ui,"Summary","summary","",-.21f,-.51f,.39f);
            ui.microphoneMode=Button(right,ui,"Open mic","mic_mode","",.28f,-.51f,.25f);
            var assessment = Panel("AssessmentConsole",new Vector3(0,.70f,.22f),new Vector3(0,1.2f,1.75f),new Vector2(1.15f,.40f)); assessment.SetParent(ui.transform,true);ui.assessment=assessment;
            ui.draft = Text(assessment,"Draft","Your assessment",new Vector3(-.52f,.16f,-.026f),.025f);
            Button(assessment,ui,"‹","draft_previous","",.405f,.13f,.09f);Button(assessment,ui,"›","draft_next","",.51f,.13f,.09f);
            string[] fields={"diagnosis","differential","procedure","urgency"};
            for(int i=0;i<4;i++) Button(assessment,ui,fields[i],"field",fields[i],-.42f+i*.28f,-.02f,.25f);
            Button(assessment,ui,"Options","page","assessment",-.42f,-.12f,.25f); Button(assessment,ui,"Keyboard","keyboard","",-.14f,-.12f,.25f);
            ui.surgery=Button(assessment,ui,"Enter OR","surgery","",.14f,-.12f,.25f);ui.surgery.gameObject.SetActive(false);
            Button(assessment,ui,"Submit","submit","",.42f,-.12f,.25f);
            var keys = Panel("RayKeyboard",new Vector3(0,.31f,.22f),new Vector3(0,1.2f,1.75f),new Vector2(1.5f,.45f)); keys.SetParent(ui.transform,true); ui.keyboard=keys;
            string alphabet="abcdefghijklmnopqrstuvwxyz";
            for(int i=0;i<alphabet.Length;i++) Button(keys,ui,alphabet[i].ToString(),"key",alphabet[i].ToString(),-.66f+(i%10)*.146f,.16f-(i/10)*.105f,.13f);
            Button(keys,ui,"Space","key","space",-.32f,-.16f,.3f); Button(keys,ui,";","key",";",0,-.16f,.14f); Button(keys,ui,"Back","key","back",.24f,-.16f,.3f); Button(keys,ui,"Clear","key","clear",.57f,-.16f,.3f);
            // Keep lower controls physically in front of seated hands/legs while preserving their angular layout.
            ForegroundPanel(assessment,new Vector3(0,.922f,.90f),.556f);
            ForegroundPanel(keys,new Vector3(0,.705f,.90f),.556f);
            keys.gameObject.SetActive(false);
            var light = new GameObject("WarmDaylight").AddComponent<Light>(); light.type=LightType.Directional; light.transform.rotation=Quaternion.Euler(45,-30,0);light.intensity=.8f;light.color=new Color(1,.96f,.88f);light.shadows=LightShadows.None;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.74f,.76f,.7f);
            // Measure imported face rather than assume Blender/FBX handedness.
            var nose = female.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name=="NoseTip");
            var headPivot = female.GetComponentsInChildren<Transform>(true).First(t=>t.name=="HeadPivot");
            if (!nose) throw new InvalidOperationException("Imported patient NoseTip missing; cannot determine face direction.");
            float faceZ = nose.position.z - headPivot.position.z;
            if (Mathf.Abs(faceZ) < .02f) throw new InvalidOperationException("Imported patient face direction is ambiguous.");
            if (faceZ < 0) foreach (var art in new[]{office,female,male}) art.transform.rotation = Quaternion.Euler(0,180,0) * art.transform.rotation;
            Debug.Log("SCALPAL_ENCOUNTER_ART_FACING importedNoseOffsetZ="+faceZ+" clinician=positiveZ artYawCorrection="+(faceZ<0?180:0));
            systems.SetActive(true);patients.Select(null);ui.Refresh();
            foreach(var fit in UnityEngine.Object.FindObjectsByType<EncounterOfficeText>(FindObjectsInactive.Include,FindObjectsSortMode.None))fit.Fit();
            EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();
            CleanupUnusedGeneratedAssets();
            Debug.Log("SCALPAL_ENCOUNTER_OFFICE_PREPARED scene="+ScenePath+" globalBuildSettingsUnchanged=true");
        }
        [MenuItem("Scalpal/Encounter Office/Verify Diagnosis Office")]
        public static void Verify() { EncounterOfficeValidation.Run(); EncounterRouteValidation.Run(); }
        public static void PrepareAndVerify() { Prepare(); Verify(); }

        static void ForegroundPanel(Transform panel,Vector3 position,float scale)
        {
            panel.position=position;panel.localScale=Vector3.one*scale;
            // Fit limits are world meters; shrink them with the card so text retains its relative button/field size.
            foreach(var text in panel.GetComponentsInChildren<EncounterOfficeText>(true))
            {
                text.maximumWidth*=scale;text.maximumHeight*=scale;text.Fit();
            }
        }

        [MenuItem("Scalpal/Encounter Office/Capture Mono Preview")]
        public static void CapturePreview()
        {
            string output = Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_PREVIEW");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SCALPAL_ENCOUNTER_PREVIEW must be an absolute output path.");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if(Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_PREVIEW_LAYOUT")=="assessment")
            {
                var ui=UnityEngine.Object.FindFirstObjectByType<EncounterOfficePanel>();ui.Act("page","assessment");ui.Act("keyboard","");
            }
            foreach(var fit in UnityEngine.Object.FindObjectsByType<EncounterOfficeText>(FindObjectsInactive.Include,FindObjectsSortMode.None))fit.Fit();
            var camera = UnityEngine.Object.FindFirstObjectByType<EncounterOfficeRig>().head;
            var render = new RenderTexture(1920, 1080, 24);
            var previous = RenderTexture.active;
            var texture = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            try
            {
                camera.stereoTargetEye = StereoTargetEyeMask.None; camera.fieldOfView = 80; camera.targetTexture = render; camera.Render();
                RenderTexture.active = render; texture.ReadPixels(new Rect(0,0,1920,1080),0,0);texture.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllBytes(output,texture.EncodeToPNG());
                Debug.Log("SCALPAL_ENCOUNTER_PREVIEW_OK monoEditorOnly=true");
            }
            finally { camera.targetTexture=null; RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(render); }
        }

        [MenuItem("Scalpal/Encounter Office/Build Android Office")]
        public static void Build()
        {
            string output = Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_APK");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SCALPAL_ENCOUNTER_APK must be an absolute output path.");
            if (!File.Exists(SurgeryScenePath)) throw new InvalidOperationException("Missing existing native surgery scene: "+SurgeryScenePath);
            Verify();
            var package = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            string product = PlayerSettings.productName, version = PlayerSettings.bundleVersion;
            int versionCode = PlayerSettings.Android.bundleVersionCode;
            var http = PlayerSettings.insecureHttpOption;
            bool appBundle = EditorUserBuildSettings.buildAppBundle;
            var preloadedAssets = PlayerSettings.GetPreloadedAssets();
            try
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,"com.scalpal.encounteroffice");
                PlayerSettings.productName="Scalpal Botanical Clinic";PlayerSettings.bundleVersion="0.1.0-office";PlayerSettings.Android.bundleVersionCode=1;
                PlayerSettings.insecureHttpOption=InsecureHttpOption.DevelopmentOnly;EditorUserBuildSettings.buildAppBundle=false;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath,SurgeryScenePath},locationPathName=output,target=BuildTarget.Android,options=BuildOptions.Development});
                if(report.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("Encounter office Android build failed: "+report.summary.result);
                Debug.Log("SCALPAL_ENCOUNTER_APK_OK bytes="+report.summary.totalSize+" package=com.scalpal.encounteroffice");
            }
            finally
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,package);PlayerSettings.productName=product;PlayerSettings.bundleVersion=version;PlayerSettings.Android.bundleVersionCode=versionCode;
                PlayerSettings.insecureHttpOption=http;EditorUserBuildSettings.buildAppBundle=appBundle;
                PlayerSettings.SetPreloadedAssets(preloadedAssets);AssetDatabase.SaveAssets();
            }
        }

        static void CleanupUnusedGeneratedAssets()
        {
            var seeds = new[]{ScenePath,Root+"/Prefabs/DoctorOffice.prefab",Root+"/Prefabs/PatientFemale.prefab",Root+"/Prefabs/PatientMale.prefab"};
            var used = new System.Collections.Generic.HashSet<string>(AssetDatabase.GetDependencies(seeds,true));
            foreach(var guid in AssetDatabase.FindAssets("",new[]{Root+"/Materials"}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid), extension=Path.GetExtension(path);
                if((extension==".mat"||extension==".asset")&&!used.Contains(path))AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
        }

        static GameObject Art(string asset,string name)
        {
            string path=Root+"/Art/Models/"+asset+".fbx";
            var importer=AssetImporter.GetAtPath(path) as ModelImporter;
            if(importer==null) throw new InvalidOperationException("Missing authored office model: "+path);
            var model=AssetDatabase.LoadAssetAtPath<GameObject>(path);if(!model)throw new InvalidOperationException("Office model failed import: "+path);
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(model);instance.name=name;
            foreach(var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                var mapped=renderer.sharedMaterials.Select(source=>source?ArtMaterial(source.name):throw new InvalidOperationException("Imported office/human material missing on "+renderer.name)).ToArray();renderer.sharedMaterials=mapped;
            }
            PrefabUtility.SaveAsPrefabAsset(instance,Root+"/Prefabs/"+asset+".prefab");return instance;
        }
        [Serializable] sealed class Palette { public PaletteEntry[] unityMaterials; }
        [Serializable] sealed class PaletteEntry { public string name; public float[] color; public float[] srgb; public string diffuseTexture; public bool alphaClip; public float roughness, metallic; }
        static Material ArtMaterial(string name)
        {
            string clean=name.Replace(" (Instance)","");Color color=new Color(.72f,.77f,.7f);
            string inventory=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../assets/environments/doctor-office/inventory.json"));
            // Read exact author palette if present; Blender FBX diffuse is also retained by the importer.
            if(File.Exists(inventory))
            {
                var palette=JsonUtility.FromJson<Palette>(File.ReadAllText(inventory));var entry=palette?.unityMaterials?.FirstOrDefault(item=>item.name==clean);
                var values=entry?.srgb??entry?.color;if(values!=null&&values.Length>=3)color=new Color(values[0],values[1],values[2]);
            }
            var result = Material("art_"+clean,color);
            if(File.Exists(inventory))
            {
                var palette = JsonUtility.FromJson<Palette>(File.ReadAllText(inventory));
                var entry = palette?.unityMaterials?.FirstOrDefault(item=>item.name==clean);
                if(entry!=null)
                {
                    result.SetFloat("_Glossiness",1-entry.roughness);result.SetFloat("_Metallic",entry.metallic);
                    if(!string.IsNullOrEmpty(entry.diffuseTexture))
                    {
                        string texturePath=entry.diffuseTexture.StartsWith("Assets/",StringComparison.Ordinal)?entry.diffuseTexture:Root+"/Art/Textures/"+entry.diffuseTexture;
                        var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                        if(!texture)throw new InvalidOperationException("Required human diffuse texture missing: "+texturePath);
                        result.mainTexture=texture;
                    }
                    result.SetFloat("_Mode",entry.alphaClip?1:0);result.SetFloat("_Cutoff",.5f);
                    result.SetInt("_SrcBlend",(int)BlendMode.One);result.SetInt("_DstBlend",(int)BlendMode.Zero);result.SetInt("_ZWrite",1);
                    if(entry.alphaClip)result.EnableKeyword("_ALPHATEST_ON");else result.DisableKeyword("_ALPHATEST_ON");
                    result.DisableKeyword("_ALPHABLEND_ON");result.DisableKeyword("_ALPHAPREMULTIPLY_ON");result.renderQueue=entry.alphaClip?2450:-1;
                    EditorUtility.SetDirty(result);
                }
            }
            return result;
        }
        static Material Material(string name,Color color)
        {
            string path=Root+"/Materials/"+name+".mat";var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!material){material=new Material(Shader.Find("Standard"));AssetDatabase.CreateAsset(material,path);}material.enableInstancing=true;material.color=color;material.SetFloat("_Glossiness",.15f);EditorUtility.SetDirty(material);return material;
        }
        static Material Glass(string name,Color color)
        {
            string path=Root+"/Materials/"+name+".mat";var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!material){material=new Material(Shader.Find("Scalpal/Encounter Office/Glass"));AssetDatabase.CreateAsset(material,path);}
            material.shader=Shader.Find("Scalpal/Encounter Office/Glass");material.renderQueue=name=="office_glass_button"?3010:name=="office_glass_border"?3001:3000;material.color=color;EditorUtility.SetDirty(material);return material;
        }
        static Mesh Rounded(float width,float height,float radius)
        {
            string name="ui_round_"+Mathf.RoundToInt(width*10000)+"_"+Mathf.RoundToInt(height*10000)+"_"+Mathf.RoundToInt(radius*10000);
            string path=Root+"/Materials/"+name+".asset";var stored=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(stored)return stored;
            const int steps=8;var vertices=new Vector3[4*(steps+1)+1];var uv=new Vector2[vertices.Length];var triangles=new int[(vertices.Length-1)*3];
            uv[0]=new Vector2(.5f,.5f);int index=1;
            for(int corner=0;corner<4;corner++)
            {
                float x=corner==0||corner==3?width/2-radius:-width/2+radius;
                float y=corner<2?height/2-radius:-height/2+radius;
                for(int j=0;j<=steps;j++)
                {
                    float angle=(corner*90+j*90f/steps)*Mathf.Deg2Rad;
                    vertices[index]=new Vector3(x+Mathf.Cos(angle)*radius,y+Mathf.Sin(angle)*radius,0);
                    uv[index]=new Vector2(vertices[index].x/width+.5f,vertices[index].y/height+.5f);index++;
                }
            }
            for(int i=1;i<vertices.Length;i++){int t=(i-1)*3;triangles[t]=0;triangles[t+1]=i;triangles[t+2]=i==vertices.Length-1?1:i+1;}
            var mesh=new Mesh{name=name,vertices=vertices,uv=uv,triangles=triangles};mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static Mesh Border(float width,float height,float radius,float thickness)
        {
            var outside=Rounded(width,height,radius);var inside=Rounded(width-2*thickness,height-2*thickness,radius-thickness);
            string path=Root+"/Materials/"+outside.name+"_ring.asset";var stored=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(stored)return stored;
            int count=outside.vertexCount-1;var vertices=new Vector3[count*2];var triangles=new int[count*6];var outer=outside.vertices;var inner=inside.vertices;
            for(int i=0;i<count;i++){vertices[i*2]=outer[i+1];vertices[i*2+1]=inner[i+1];int next=(i+1)%count;int t=i*6;triangles[t]=i*2;triangles[t+1]=next*2;triangles[t+2]=i*2+1;triangles[t+3]=i*2+1;triangles[t+4]=next*2;triangles[t+5]=next*2+1;}
            var mesh=new Mesh{name=outside.name+"_ring",vertices=vertices,triangles=triangles};mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static GameObject Surface(Transform parent,string name,float width,float height,float radius,Material material,Vector3 local)
        {
            var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(parent,false);go.transform.localPosition=local;
            go.GetComponent<MeshFilter>().sharedMesh=Rounded(width,height,radius);go.GetComponent<Renderer>().sharedMaterial=material;return go;
        }
        static Transform Panel(string name,Vector3 position,Vector3 viewer,Vector2 size)
        {
            var panel=new GameObject(name).transform;panel.SetPositionAndRotation(position,Quaternion.Euler(0,180,0));
            Surface(panel,"FrostedGlass",size.x,size.y,.045f,glass,Vector3.zero);
            var border=Surface(panel,"SubtleGlassBorder",size.x+.004f,size.y+.004f,.047f,glassBorder,new Vector3(0,0,.003f));
            border.GetComponent<MeshFilter>().sharedMesh=Border(size.x+.004f,size.y+.004f,.047f,.002f);
            return panel;
        }
        static TextMesh Text(Transform parent,string name,string content,Vector3 position,float size,TextAnchor anchor=TextAnchor.UpperLeft)
        {
            var text=new GameObject(name).AddComponent<TextMesh>();text.transform.SetParent(parent,false);text.transform.localPosition=position;text.text=content;text.anchor=anchor;text.characterSize=size*.25f;text.fontSize=48;
            string fontName=name.StartsWith("Label_",StringComparison.Ordinal)||name=="PatientState"?"Inter-Medium":name=="Title"||name=="FindingsTitle"?"Inter-SemiBold":"Inter-Regular";
            text.font=AssetDatabase.LoadAssetAtPath<Font>(Root+"/Fonts/"+fontName+".ttf");
            if(!text.font)throw new InvalidOperationException("The licensed Inter font must be imported before preparing the scene.");
            var fontMaterial=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Materials/office_world_text_"+fontName+".mat");
            if(!fontMaterial){fontMaterial=new Material(Shader.Find("Scalpal/Encounter Office/World Text"));AssetDatabase.CreateAsset(fontMaterial,Root+"/Materials/office_world_text_"+fontName+".mat");}
            fontMaterial.renderQueue=3020;fontMaterial.mainTexture=text.font.material.mainTexture;EditorUtility.SetDirty(fontMaterial);text.GetComponent<Renderer>().sharedMaterial=fontMaterial;text.color=InkColor;
            var fit=text.gameObject.AddComponent<EncounterOfficeText>();fit.preferredCharacterSize=size*.25f;
            fit.maximumWidth=name=="Draft"?.80f:name=="PatientState"?1.1f:.76f;
            fit.maximumHeight=name=="Draft"?.13f:name=="Chart"?.44f:name=="Response"?.23f:name=="Status"?.12f:.10f;fit.Fit();
            return text;
        }
        static EncounterOfficeButton Button(Transform parent,EncounterOfficePanel panel,string label,string command,string argument,float x,float y,float width)
        {
            var go=Surface(parent,"Button_"+command+"_"+label,width,.072f,.036f,glassButton,new Vector3(x,y,-.012f));
            var collider=go.AddComponent<BoxCollider>();collider.size=new Vector3(width,.072f,.025f);
            var button=go.AddComponent<EncounterOfficeButton>();button.panel=panel;button.command=command;button.argument=argument;
            var text=Text(parent,"Label_"+label,label,new Vector3(x,y,-.024f),.028f,TextAnchor.MiddleCenter);text.transform.SetParent(go.transform,true);button.label=text;
            var fit=text.GetComponent<EncounterOfficeText>();fit.maximumWidth=width-.035f;fit.maximumHeight=.062f;fit.Fit();return button;
        }
        static LineRenderer Ray(Transform parent)
        {
            var ray=new GameObject("ControllerSelectionRay").AddComponent<LineRenderer>();ray.transform.SetParent(parent,false);ray.useWorldSpace=true;ray.positionCount=2;ray.startWidth=.003f;ray.endWidth=.001f;ray.sharedMaterial=lilac;ray.enabled=false;return ray;
        }
    }
}
