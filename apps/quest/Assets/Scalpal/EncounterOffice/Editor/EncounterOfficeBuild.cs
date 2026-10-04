using System;
using System.IO;
using System.Linq;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
using Scalpal.Voice;
using TMPro;
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
        static ScalpalBrand brand;
        // Seated clinician eye position in the scene's tracking-origin space (see TrackedHeadCamera below).
        public static readonly Vector3 Viewer = new Vector3(0,1.2f,1.75f);
        [MenuItem("Scalpal/Encounter Office/Prepare Diagnosis Office")]
        public static void Prepare()
        {
            // Deliberately reuse current Android OpenXR project configuration without changing surgery build settings.
            Directory.CreateDirectory(Root + "/Scenes"); Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Prefabs"); AssetDatabase.Refresh();
            brand = ScalpalBrandBuild.Prepare();
            EncounterOfficeLighting.EnsureLightmapUVs();
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
            // Rays are built at runtime from each controller's aim pose (ScalpalPointerHand); the grip transforms only carry the talk hint.
            rig.talkHint = Text(rig.left,"TalkHint","Hold grip to talk",new Vector3(0,.075f,.06f),.024f,TextAnchor.MiddleCenter);
            // Read at roughly half a metre on the left controller: 32 mm per metre label floor.
            var talkFit = rig.talkHint.GetComponent<ScalpalTextFit>(); talkFit.preferredSize = Em(ScalpalTextRole.Label,.5f); talkFit.maximumWidth = .30f; talkFit.maximumHeight = .05f; talkFit.Fit();
            rig.talkHint.gameObject.SetActive(false);
            var ui = new GameObject("EncounterVisualFallback").AddComponent<EncounterOfficePanel>(); ui.session = session;
            // Consoles flank the patient, yaw-facing the seated clinician at ~1.55 m so body type clears 24 mm/m.
            var left = Panel("InterviewConsole", new Vector3(.95f,1.42f,.55f), Viewer, new Vector2(1.0f,1.16f)); left.SetParent(ui.transform,true);
            var right = Panel("FindingsConsole", new Vector3(-.95f,1.42f,.55f), Viewer, new Vector2(1.0f,1.16f)); right.SetParent(ui.transform,true);
            ui.heading = Text(left,"Title","Scalpal",new Vector3(-.45f,.545f,-.026f),.034f);
            ui.status = Text(left,"Status","Choose a synthetic patient.",new Vector3(-.45f,.425f,-.026f),.024f);
            Button(left,ui,"Patients","page","patients",-.235f,.225f,.43f);
            Button(left,ui,"Reload list","reload_patients","",.235f,.225f,.43f);
            Button(left,ui,"History","page","history",-.31f,.14f,.28f); Button(left,ui,"Examine","page","exam",0,.14f,.28f); Button(left,ui,"Tests","page","tests",.31f,.14f,.28f);
            ui.options = new EncounterOfficeButton[4];
            for (int i=0;i<4;i++) ui.options[i] = Button(left,ui,"Question","option","",0,.05f-i*.09f,.90f);
            ui.suggestions = Button(left,ui,"Suggestions","suggestions","",0,.05f,.90f);
            Button(left,ui,"Back","previous","",-.36f,-.32f,.18f); Button(left,ui,"More","next","",-.17f,-.32f,.18f);
            Button(left,ui,"Voice","voice","",.07f,-.32f,.28f); Button(left,ui,"Stop","stop","",.335f,-.32f,.21f);
            Button(left,ui,"Present to Scalpal","attending","",-.115f,-.42f,.67f); Button(left,ui,"Refresh","refresh","",.345f,-.42f,.21f);
            Text(right,"FindingsTitle","Your findings",new Vector3(-.45f,.53f,-.026f),.034f);
            ui.chart = Text(right,"Chart","No examinations performed.",new Vector3(-.45f,.455f,-.026f),.024f);
            Button(right,ui,"Back","chart_previous","",-.345f,-.095f,.21f); Button(right,ui,"More","chart_next","",-.115f,-.095f,.21f); Button(right,ui,"Findings / score","chart_toggle","",.235f,-.095f,.43f);
            // Patient, parent and Jarvis lines appear in the shared lower-middle DialogueBox, not on this panel.
            Button(right,ui,"Summary","summary","",-.235f,-.19f,.43f);
            ui.microphoneMode=Button(right,ui,"Open mic","mic_mode","",.235f,-.19f,.43f);
            var assessment = Panel("AssessmentConsole",new Vector3(0,.70f,.22f),Viewer,new Vector2(1.42f,.50f)); assessment.SetParent(ui.transform,true);ui.assessment=assessment;
            ui.draft = Text(assessment,"Draft","Your assessment",new Vector3(-.66f,.22f,-.026f),.025f);
            Button(assessment,ui,"‹","draft_previous","",.52f,.18f,.10f);Button(assessment,ui,"›","draft_next","",.64f,.18f,.10f);
            string[] fields={"diagnosis","differential","procedure","urgency"};
            for(int i=0;i<4;i++) Button(assessment,ui,fields[i],"field",fields[i],-.525f+i*.35f,-.04f,.34f);
            Button(assessment,ui,"Options","page","assessment",-.525f,-.16f,.34f); Button(assessment,ui,"Keyboard","keyboard","",-.175f,-.16f,.34f);
            ui.surgery=Button(assessment,ui,"Enter OR","surgery","",.175f,-.16f,.34f);ui.surgery.gameObject.SetActive(false);
            Button(assessment,ui,"Submit","submit","",.525f,-.16f,.34f);
            var keys = Panel("RayKeyboard",new Vector3(0,.31f,.22f),Viewer,new Vector2(1.5f,.45f)); keys.SetParent(ui.transform,true); ui.keyboard=keys;
            string alphabet="abcdefghijklmnopqrstuvwxyz";
            for(int i=0;i<alphabet.Length;i++) Button(keys,ui,alphabet[i].ToString(),"key",alphabet[i].ToString(),-.66f+(i%10)*.146f,.16f-(i/10)*.105f,.13f,.092f);
            Button(keys,ui,"Space","key","space",-.32f,-.16f,.3f,.092f); Button(keys,ui,";","key",";",0,-.16f,.14f,.092f); Button(keys,ui,"Back","key","back",.24f,-.16f,.3f,.092f); Button(keys,ui,"Clear","key","clear",.57f,-.16f,.3f,.092f);
            // Keep lower controls physically in front of seated hands/legs while preserving their angular layout.
            ForegroundPanel(assessment,new Vector3(0,.922f,.90f),.556f);
            ForegroundPanel(keys,new Vector3(0,.705f,.90f),.556f);
            // Size every panel string for its real distance from the seated clinician (brand floors: 32 mm/m labels, 24 mm/m body).
            ScalpalBrandLayout.SizeForViewer(ui.transform,Viewer);
            ScalpalBrandLayout.SizeForViewer(patients.stateLabel.transform,Viewer);
            keys.gameObject.SetActive(false);
            // Measure imported face rather than assume Blender/FBX handedness.
            var nose = female.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name=="NoseTip");
            var headPivot = female.GetComponentsInChildren<Transform>(true).First(t=>t.name=="HeadPivot");
            if (!nose) throw new InvalidOperationException("Imported patient NoseTip missing; cannot determine face direction.");
            float faceZ = nose.position.z - headPivot.position.z;
            if (Mathf.Abs(faceZ) < .02f) throw new InvalidOperationException("Imported patient face direction is ambiguous.");
            if (faceZ < 0) foreach (var art in new[]{office,female,male}) art.transform.rotation = Quaternion.Euler(0,180,0) * art.transform.rotation;
            Debug.Log("SCALPAL_ENCOUNTER_ART_FACING importedNoseOffsetZ="+faceZ+" clinician=positiveZ artYawCorrection="+(faceZ<0?180:0));
            systems.SetActive(true);patients.Select(null);ui.Refresh();
            foreach(var fit in UnityEngine.Object.FindObjectsByType<ScalpalTextFit>(FindObjectsInactive.Include,FindObjectsSortMode.None))fit.Fit();
            EncounterOfficeLighting.Apply(office,female,male);
            EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();
            EncounterOfficeLighting.Bake(scene);
            CleanupUnusedGeneratedAssets();
            Debug.Log("SCALPAL_ENCOUNTER_OFFICE_PREPARED scene="+ScenePath+" globalBuildSettingsUnchanged=true");
        }
        [MenuItem("Scalpal/Encounter Office/Verify Diagnosis Office")]
        public static void Verify() { EncounterOfficeValidation.Run(); EncounterRouteValidation.Run(); Scalpal.Shell.Editor.DialogueBoxValidation.Run(); EncounterOfficeLightingValidation.Run(); }
        public static void PrepareAndVerify() { Prepare(); Verify(); }

        static void ForegroundPanel(Transform panel,Vector3 position,float scale)
        {
            panel.position=position;panel.localScale=Vector3.one*scale;
            // Fit limits are world meters; shrink them with the card so text retains its relative button/field size.
            foreach(var text in panel.GetComponentsInChildren<ScalpalTextFit>(true))
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
            string layout=Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_PREVIEW_LAYOUT");
            if(layout=="assessment")
            {
                var ui=UnityEngine.Object.FindFirstObjectByType<EncounterOfficePanel>();ui.Act("page","assessment");ui.Act("keyboard","");
            }
            Scalpal.Shell.DialogueFeed dialogue=null;
            if(layout=="dialogue"||layout=="dialogue-attending")
            {
                // Editor-only illustration: a fixed patient and authored-looking lines, not a live encounter.
                var session=UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
                session.patient.Select(new EncounterState{patientId=EncounterContract.FemalePatientId,patientName="Priya Ramaswamy",speaker="patient",patientSex="female",patientAge=40,speakerName="Priya Ramaswamy",speakerSex="female",speakerAge=40,phase="interview"});
                var ui=UnityEngine.Object.FindFirstObjectByType<EncounterOfficePanel>();ui.Act("page",layout=="dialogue"?"history":"assessment");
                dialogue=Scalpal.Shell.DialogueFeed.Attach();var box=dialogue.box;
                if(layout=="dialogue")
                {
                    box.Say(Scalpal.Shell.DialogueSpeaker.You,"","When did the pain start, and where was it at first?");
                    box.Say(Scalpal.Shell.DialogueSpeaker.Patient,"Priya Ramaswamy","Last night, around my belly button. This morning it moved down to the right side and it hurts when I walk.");
                }
                else
                {
                    box.Say(Scalpal.Shell.DialogueSpeaker.You,"","I think this is acute appendicitis.");
                    box.Say(Scalpal.Shell.DialogueSpeaker.Attending,"","Good. What else is on your differential, and how urgently should she go to theatre?");
                }
                box.CompleteLine();box.Tick(0);for(int i=0;i<60;i++)box.Tick(1f/72);
            }
            foreach(var fit in UnityEngine.Object.FindObjectsByType<ScalpalTextFit>(FindObjectsInactive.Include,FindObjectsSortMode.None))fit.Fit();
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
            finally { camera.targetTexture=null; RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(render); if(dialogue)UnityEngine.Object.DestroyImmediate(dialogue.gameObject); }
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
        static GameObject Surface(Transform parent,string name,float width,float height,float radius,Material material,Vector3 local)
        {
            var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(parent,false);go.transform.localPosition=local;
            go.GetComponent<MeshFilter>().sharedMesh=Rounded(width,height,radius);go.GetComponent<Renderer>().sharedMaterial=material;return go;
        }
        static Transform Panel(string name,Vector3 position,Vector3 viewer,Vector2 size)
        {
            // Level, yaw-only facing toward the seated viewer (text reads on the panel's -Z face).
            var panel=new GameObject(name).transform;panel.SetPositionAndRotation(position,ScalpalPlacement.Level(position-viewer));
            Surface(panel,"FrostedGlass",size.x,size.y,Mathf.Min(.05f,size.y*.16f),brand.glass,Vector3.zero);
            return panel;
        }
        static float Em(ScalpalTextRole role,float distance) => ScalpalBrandLayout.Em(brand,role,distance);
        static TextMeshPro Text(Transform parent,string name,string content,Vector3 position,float size,TextAnchor anchor=TextAnchor.UpperLeft)
        {
            var role=name.StartsWith("Label_",StringComparison.Ordinal)||name=="PatientState"||name=="TalkHint"?ScalpalTextRole.Label:name=="Title"||name=="FindingsTitle"?ScalpalTextRole.Title:ScalpalTextRole.Body;
            float width=name=="Draft"?1.0f:name=="PatientState"?1.1f:.90f;
            float height=name=="Draft"?.19f:name=="Chart"?.50f:name=="Status"?.15f:name=="Title"?.12f:.10f;
            return brand.Text(parent,name,content,role,position,size*1.2f,width,height,anchor);
        }
        static EncounterOfficeButton Button(Transform parent,EncounterOfficePanel panel,string label,string command,string argument,float x,float y,float width,float height=.072f)
        {
            var go=Surface(parent,"Button_"+command+"_"+label,width,height,height/2,brand.button,new Vector3(x,y,-.012f));
            var collider=go.AddComponent<BoxCollider>();collider.size=new Vector3(width,height,.025f);
            var button=go.AddComponent<EncounterOfficeButton>();button.panel=panel;button.command=command;button.argument=argument;
            var text=Text(parent,"Label_"+label,label,new Vector3(x,y,-.024f),.028f,TextAnchor.MiddleCenter);text.transform.SetParent(go.transform,true);button.label=text;
            var fit=text.GetComponent<ScalpalTextFit>();fit.maximumWidth=width-.035f;fit.maximumHeight=height-.008f;fit.Fit();return button;
        }
    }
}
