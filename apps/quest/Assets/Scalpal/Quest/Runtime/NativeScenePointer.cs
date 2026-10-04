using System;
using System.Collections.Generic;
using System.Text;
using Scalpal.Anatomy;
using Scalpal.Brand;
using Scalpal.Instruments;
using Scalpal.Surgery;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // Read-only scene identification. This does not detect hidden anatomy, change a coach
    // highlight, consume a trigger, score an action, or infer the volunteer's actual organs.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(190)]
    public sealed class NativeScenePointer : MonoBehaviour
    {
        public const float MaximumDistance = 3;
        public struct PointedObject
        {
            public string id, label;
            public bool instrument;
            public Bounds worldBounds;
            public Ray ray;
        }
        sealed class Target
        {
            public AnatomyPart part;
            public InstrumentBehaviour tool;
            public Renderer[] renderers;
        }
        sealed class View
        {
            public GameObject root;
            public LineRenderer box, laser;
            public TextMeshPro label;
            public ScalpalTextFit fit;
            public string rawLabel;
            public float fittedEm;
            public PointedObject hit;
            public bool visible;
            public readonly Vector3[] corners = new Vector3[16];
        }
        static readonly int[] EdgeWalk = { 0,1,2,3,0,4,5,1,5,6,2,6,7,3,7,4 };
        readonly Dictionary<UnityEngine.Object, Target> targets = new Dictionary<UnityEngine.Object, Target>();
        readonly RaycastHit[] hits = new RaycastHit[128];
        readonly View[] views = { new View(), new View() };
        NativeCaseSession session;
        NativeWorkbench rig;
        AnatomyController anatomy;
        OpenSurgerySession surgery;
        Renderer patientSkin;
        Collider[] patientSkinColliders = Array.Empty<Collider>();
        IReadOnlyList<AnatomyPart> parts;
        InstrumentBehaviour[] tools;
        bool paused;

        void Start()
        {
            var owner = GetComponent<NativeCaseSession>();
            if (owner != null) Initialize(owner);
        }
        public void Initialize(NativeCaseSession owner)
        {
            if (rig) rig.ToolsReset -= Clear;
            session = owner; rig = owner ? owner.workbench : null; anatomy = owner ? owner.anatomy : null;
            surgery = owner ? owner.GetComponent<OpenSurgerySession>() : null;
            patientSkin = null; patientSkinColliders = Array.Empty<Collider>();
            parts = null; tools = null; targets.Clear(); Clear();
            if (rig) rig.ToolsReset += Clear;
        }
        void LateUpdate() => Simulate();

        // The validation replaces device samples only; this is the production query/display path.
        public void Simulate()
        {
            if (!isActiveAndEnabled || !session || !rig || !rig.IsReady || !rig.trackingOrigin || !rig.headCamera
                || paused || !XRInput.Source.DisplayRunning || !XRInput.Source.HasFocus || !XRInput.TryPose(XRNode.Head,out _))
            { Clear(); return; }
            if (!surgery) surgery = session.GetComponent<OpenSurgerySession>();
            var currentSkin = session.presentation ? session.presentation.virtualMannequin : null;
            if (patientSkin != currentSkin)
            {
                patientSkin = currentSkin;
                patientSkinColliders = patientSkin ? patientSkin.GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();
            }
            RefreshTargets();
            for (int hand = 0; hand < views.Length; hand++)
            {
                var sample = ScalpalAim.Read(hand);
                var input = FindInput(hand);
                var interactor = input ? input.GetComponent<InstrumentInteractor>() : null;
                if (!sample.Valid || !Finite(sample.position)
                    || (sample.kind == ScalpalPointerKind.Controller && (!input || !input.isActiveAndEnabled
                        || !interactor || !interactor.TrackingValid)))
                { Hide(views[hand]); continue; }
                var ray = new Ray(rig.trackingOrigin.TransformPoint(sample.position),
                    rig.trackingOrigin.rotation * (sample.rotation * Vector3.forward));
                if (!Finite(ray.direction) || ray.direction.sqrMagnitude < .99f)
                { Hide(views[hand]); continue; }
                bool resolved = TryResolve(ray, interactor ? interactor.HeldInstrument : null, out var target, out var bounds, out float beamDistance);
                ShowLaser(views[hand], ray, beamDistance);
                // Hand aim still draws a laser; instrument/organ naming retains its controller contract.
                if (sample.kind != ScalpalPointerKind.Controller || !resolved)
                { HideIdentity(views[hand]); continue; }
                string id = target.tool ? target.tool.instrumentId : target.part.stableId;
                string label = target.tool ? id.Replace('_',' ') : string.IsNullOrEmpty(target.part.displayName) ? id.Replace('_',' ') : target.part.displayName;
                Show(views[hand], new PointedObject { id=id, label=label, instrument=target.tool, worldBounds=bounds, ray=ray });
            }
        }
        XRInstrumentInput FindInput(int hand)
        {
            var node = hand == 0 ? XRNode.LeftHand : XRNode.RightHand;
            foreach (var input in rig.inputs ?? Array.Empty<XRInstrumentInput>()) if (input && input.controller == node) return input;
            return null;
        }
        void RefreshTargets()
        {
            var currentParts = anatomy ? anatomy.Parts : null;
            if (ReferenceEquals(parts,currentParts) && ReferenceEquals(tools,rig.tools)) return;
            parts = currentParts; tools = rig.tools; targets.Clear();
            if (parts != null) foreach (var part in parts)
                if (part) targets[part] = new Target { part=part, renderers=OwnedRenderers(part.transform,part,null) };
            foreach (var tool in tools ?? Array.Empty<InstrumentBehaviour>())
                if (tool) targets[tool] = new Target { tool=tool, renderers=OwnedRenderers(tool.transform,null,tool) };
        }
        static Renderer[] OwnedRenderers(Transform root, AnatomyPart part, InstrumentBehaviour tool)
        {
            var result = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                if (!(renderer is LineRenderer) && (part ? renderer.GetComponentInParent<AnatomyPart>() == part : renderer.GetComponentInParent<InstrumentBehaviour>() == tool)) result.Add(renderer);
            return result.ToArray();
        }
        bool TryResolve(Ray ray, InstrumentBehaviour held, out Target target, out Bounds bounds, out float beamDistance)
        {
            target=null; bounds=default; beamDistance=MaximumDistance;
            int count = Physics.RaycastNonAlloc(ray,hits,MaximumDistance,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Collide);
            // Overflow can omit the nearest surface, so do not label through an unknown occluder.
            Collider nearest = null; float distance=MaximumDistance;
            for (int i=0;i<count;i++)
            {
                var collider=hits[i].collider;
                if (!collider || hits[i].distance>=distance) continue;
                if (patientSkin && collider.transform.IsChildOf(patientSkin.transform) && ClipsSkin(hits[i].point)) continue;
                var tool=collider.GetComponentInParent<InstrumentBehaviour>();
                // Distal trigger bubbles and the pointer hand's held tool are not pointing targets.
                if (tool && (tool == held || collider.GetComponent<InstrumentTipContact>())) continue;
                nearest=collider; distance=hits[i].distance;
            }
            // The authored patient collision is on Ignore Raycast. Query only that known
            // skin explicitly rather than including every helper collider on that layer.
            if (patientSkin && patientSkin.enabled && !patientSkin.forceRenderingOff && patientSkin.gameObject.activeInHierarchy)
                foreach (var collider in patientSkinColliders)
                    if (collider && collider.enabled && collider.gameObject.activeInHierarchy
                        && (Physics.DefaultRaycastLayers & (1 << collider.gameObject.layer)) == 0
                        && collider.Raycast(ray, out var skinHit, distance) && !ClipsSkin(skinHit.point))
                    { nearest = collider; distance = skinHit.distance; }
            beamDistance = distance;
            if (count == hits.Length) return false;
            if (!nearest || nearest.GetComponentInParent<IScalpalPressable>() != null) return false;
            var part=nearest.GetComponentInParent<AnatomyPart>();
            var instrument=nearest.GetComponentInParent<InstrumentBehaviour>();
            UnityEngine.Object identity=part ? (UnityEngine.Object)part : instrument;
            if (!identity || !targets.TryGetValue(identity,out target)) return false;
            if (part && (!session.RegistrationReady || !anatomy || !anatomy.RegistrationValid || !anatomy.TryGetPart(part.stableId,out var indexed)
                || indexed != part || !part.IsVisible || !part.HasVisibleGeometry)) return false;
            if (instrument && (!instrument.isActiveAndEnabled || string.IsNullOrEmpty(instrument.instrumentId))) return false;
            bool any=false;
            foreach (var renderer in target.renderers)
            {
                if (!renderer || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                if (!any) { bounds=renderer.bounds; any=true; } else bounds.Encapsulate(renderer.bounds);
            }
            return any && Finite(bounds.center) && Finite(bounds.size) && bounds.size.sqrMagnitude>0;
        }
        bool ClipsSkin(Vector3 world) => surgery && surgery.Wound && surgery.Wound.ClipsSkinAt(world);
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        void ShowLaser(View view, Ray ray, float distance)
        {
            if (!view.laser)
            {
                view.laser = new GameObject("ORHandLaser").AddComponent<LineRenderer>();
                view.laser.transform.SetParent(transform, false);
                view.laser.useWorldSpace = true; view.laser.positionCount = 2;
                view.laser.startWidth = .0015f; view.laser.endWidth = .0008f;
                view.laser.sharedMaterial = ScalpalBrand.Active.ray;
                view.laser.startColor = view.laser.endColor = ScalpalBrand.SurgicalGreen;
                view.laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                view.laser.receiveShadows = false;
            }
            view.laser.enabled = true;
            // Leave the controller's near field clear; stop at the actual foreground collider.
            view.laser.SetPosition(0, ray.GetPoint(Mathf.Min(.015f, distance)));
            view.laser.SetPosition(1, ray.GetPoint(Mathf.Max(0, distance)));
        }
        void Show(View view, PointedObject hit)
        {
            if (!view.root)
            {
                view.root=new GameObject("SceneIdentityPointer");
                view.root.transform.SetParent(transform,false);
                view.box=new GameObject("SceneBounds").AddComponent<LineRenderer>(); view.box.transform.SetParent(view.root.transform,false);
                view.box.useWorldSpace=true; view.box.positionCount=EdgeWalk.Length; view.box.startWidth=view.box.endWidth=.001f;
                view.box.sharedMaterial=ScalpalBrand.Active.ray; view.box.startColor=view.box.endColor=ScalpalBrand.SurgicalGreen;
                view.box.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; view.box.receiveShadows=false;
                view.label=ScalpalBrand.Active.Text(view.root.transform,"SceneIdentityLabel","",ScalpalTextRole.Label,Vector3.zero,.024f,.5f,.07f,TextAnchor.MiddleCenter);
                view.label.color=ScalpalBrand.SurgicalGreen;
                view.fit=view.label.GetComponent<ScalpalTextFit>();
            }
            view.root.SetActive(true); view.visible=true; view.hit=hit;
            var bounds=hit.worldBounds; bounds.Expand(.002f);
            var min=bounds.min; var max=bounds.max;
            for (int i=0;i<EdgeWalk.Length;i++)
            {
                int corner=EdgeWalk[i];
                view.corners[i]=new Vector3((corner==1||corner==2||corner==5||corner==6)?max.x:min.x,
                    (corner==2||corner==3||corner==6||corner==7)?max.y:min.y,corner>=4?max.z:min.z);
            }
            view.box.SetPositions(view.corners);
            var camera=rig.headCamera.transform;
            view.label.transform.position=bounds.center+camera.up*(bounds.extents.magnitude+.035f);
            view.label.transform.rotation=Quaternion.LookRotation(view.label.transform.position-camera.position,camera.up);
            float distance=Mathf.Max(.05f,Vector3.Distance(view.label.transform.position,camera.position));
            float em=ScalpalBrandLayout.Em(ScalpalBrand.Active,ScalpalTextRole.Label,distance);
            // Distance is evaluated every frame, but expensive TMP layout only runs for a
            // changed name or size. The 6% brand headroom covers this 1% refit tolerance.
            if(view.rawLabel != hit.label || Mathf.Abs(em-view.fittedEm) > em*.01f)
            {
                view.rawLabel=hit.label;view.fittedEm=em;view.label.text=WrapIdentity(hit.label,24);
                view.label.fontSize=em*10;
                var preferred=view.label.GetPreferredValues(Mathf.Infinity,Mathf.Infinity);
                view.fit.preferredSize=em;
                // Explicit bounded lines preserve the minimum size instead of shrinking a
                // long anatomical name into a fixed-width tooltip below the reading floor.
                view.fit.maximumWidth=preferred.x*1.02f+.005f;
                view.fit.maximumHeight=preferred.y*1.02f+.005f;
                view.fit.Fit();
            }
        }
        static string WrapIdentity(string label,int width)
        {
            var result=new StringBuilder();int line=0;
            foreach(var word in label.Split(new[]{' ','\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                int at=0;
                while(at<word.Length)
                {
                    int count=Math.Min(width,word.Length-at);
                    if(line>0)
                    {
                        if(line+1+count>width){result.Append('\n');line=0;}
                        else{result.Append(' ');line++;}
                    }
                    result.Append(word,at,count);line+=count;at+=count;
                    if(at<word.Length){result.Append('\n');line=0;}
                }
            }
            return result.ToString();
        }
        public bool TryGetPointed(int hand,out PointedObject hit)
        {
            hit=default; if(hand<0||hand>=views.Length||!views[hand].visible)return false;
            hit=views[hand].hit; return true;
        }
        public LineRenderer BoxVisual(int hand) => hand>=0&&hand<views.Length ? views[hand].box : null;
        public TextMeshPro LabelVisual(int hand) => hand>=0&&hand<views.Length ? views[hand].label : null;
        public LineRenderer LaserVisual(int hand) => hand>=0&&hand<views.Length ? views[hand].laser : null;
        public void Clear() { foreach(var view in views)Hide(view); }
        static void HideIdentity(View view) { view.visible=false; view.hit=default; if(view.root)view.root.SetActive(false); }
        static void Hide(View view) { HideIdentity(view); if(view.laser)view.laser.enabled=false; }
        void OnApplicationPause(bool value) { paused=value; if(value)Clear(); }
        void OnApplicationFocus(bool value) { if(!value)Clear(); }
        void OnDisable() => Clear();
        void OnDestroy()
        {
            if(rig)rig.ToolsReset-=Clear;
            foreach(var view in views)
            {
                if(view.root) { if(Application.isPlaying)Destroy(view.root);else DestroyImmediate(view.root); }
                if(view.laser) { if(Application.isPlaying)Destroy(view.laser.gameObject);else DestroyImmediate(view.laser.gameObject); }
            }
        }
    }
}
