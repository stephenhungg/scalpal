using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using InputDevice = UnityEngine.XR.InputDevice;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Handoff
{
    /// <summary>World-locked, paginated handoff surface. Rendering and input only; no case authority.</summary>
    public sealed class HandoffCard : MonoBehaviour
    {
        public Font font;
        public Material glass, buttonMaterial, textMaterial;
        public Camera viewer;
        public float distance = 1.2f;
        public bool Visible => content && content.activeSelf;
        public int PageCount => pages.Count;
        public int Page => page;
        const float Width = 1.18f, TextWidth = 1.04f, LineHeight = .046f;
        const int BodyLines = 6, FontSize = 80;
        const float BodySize = .0052f, LabelSize = .0068f;
        static readonly Color Ink = new Color(.94f, .95f, .91f);
        readonly List<string> pages = new List<string>();
        readonly List<Target> targets = new List<Target>();
        readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        readonly List<Mesh> meshes = new List<Mesh>();
        GameObject content;
        Material textInk, surfaceMaterial, actionMaterial;
        LineRenderer pointer;
        Action<int> selected;
        string heading, copy;
        string[] actions = Array.Empty<string>();
        bool[] available;
        bool triggerHeld, armed, positioned, focused = true, paused;
        int page, hovered = -1;
        sealed class Target
        {
            public Rect bounds;
            public Renderer renderer;
            public int action;
            public bool enabled;
        }

        public void Show(string title, string body, string[] labels, Action<int> onSelect, bool[] enabled = null)
        {
            selected = onSelect;
            bool unchanged = Visible && heading == title && copy == body && Same(actions, labels) && Same(available, enabled);
            if (unchanged) return;
            heading = title ?? ""; copy = body ?? "";
            actions = labels == null ? Array.Empty<string>() : (string[])labels.Clone();
            available = enabled == null ? null : (bool[])enabled.Clone();
            EnsureMaterials();
            if (!positioned) Recenter();
            page = 0; pages.Clear();
            var lines = Wrap(copy, BodySize, TextWidth);
            for (int start = 0; start < lines.Count; start += BodyLines)
                pages.Add(string.Join("\n", lines.GetRange(start, Mathf.Min(BodyLines, lines.Count - start))));
            if (pages.Count == 0) pages.Add("");
            Render();
        }

        public void Hide()
        {
            if (content) content.SetActive(false);
            if (pointer) pointer.enabled = false;
            positioned = false; armed = false; selected = null;
        }

        public void Recenter()
        {
            if (!viewer) viewer = Camera.main;
            if (!viewer) return;
            Vector3 forward = Vector3.ProjectOnPlane(viewer.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            transform.SetPositionAndRotation(viewer.transform.position + forward * distance - Vector3.up * .06f, Quaternion.LookRotation(forward));
            positioned = true;
        }

        void EnsureMaterials()
        {
            if (!font) throw new InvalidOperationException("HandoffCard requires the shared Inter font reference.");
            if (textInk) return;
            if (!glass || !buttonMaterial || !textMaterial)
                throw new InvalidOperationException("HandoffCard requires the shared EncounterOffice glass, button and text materials.");
            textInk = new Material(textMaterial); textInk.renderQueue = 3020;
            surfaceMaterial = new Material(glass); surfaceMaterial.renderQueue = 3000;
            actionMaterial = new Material(buttonMaterial); actionMaterial.renderQueue = 3010;
            owned.Add(textInk); owned.Add(surfaceMaterial); owned.Add(actionMaterial);
            Font.textureRebuilt += RefreshFont;
        }

        void RefreshFont(Font changed) { if (changed == font && textInk) textInk.mainTexture = font.material.mainTexture; }

        void Render()
        {
            if (content) { content.SetActive(false); Dispose(content); }
            foreach (var mesh in meshes) if (mesh) Dispose(mesh);
            meshes.Clear();
            targets.Clear(); hovered = -1;
            content = new GameObject("HandoffCardContent"); content.transform.SetParent(transform, false);
            var actionHeights = new float[actions.Length];
            float buttonsHeight = 0;
            for (int i=0; i<actions.Length; i++)
            {
                actionHeights[i] = Mathf.Max(.102f, Wrap(actions[i] ?? "", LabelSize, TextWidth-.045f).Count*.055f+.025f);
                buttonsHeight += actionHeights[i]+.018f;
            }
            float height = .54f + buttonsHeight + (pages.Count > 1 ? .10f : 0);
            var backdrop = Surface("Glass", new Rect(-Width / 2, -height / 2, Width, height), 0, surfaceMaterial, new Color(.075f,.12f,.135f,.97f));
            // Consume the office rig's physics ray so a handoff click cannot activate a control behind this card.
            var blocker = backdrop.gameObject.AddComponent<BoxCollider>();
            blocker.size = new Vector3(Width,height,.025f);
            float top = height / 2;
            Text("PhaseStepper", "EXPLORE  /  OFFICE  /  OR  /  REPLAY  /  RECAP", new Vector3(-TextWidth/2, top-.045f, -.008f), .0035f, new Color(.76f,.80f,.77f));
            Text("Title", string.Join("\n", Wrap(heading, LabelSize, TextWidth)), new Vector3(-TextWidth/2, top-.105f, -.012f), LabelSize, Ink);
            var lines = pages[page].Split('\n');
            for (int i=0; i<lines.Length; i++)
            {
                float bodyY=top-.218f-i*LineHeight;
                bool found=lines[i].StartsWith("found",StringComparison.OrdinalIgnoreCase);
                bool missed=lines[i].StartsWith("missed",StringComparison.OrdinalIgnoreCase);
                if(found || missed)
                    Surface("RiskChip",new Rect(-TextWidth/2-.008f,bodyY-.046f,TextWidth+.016f,.043f),-.008f,actionMaterial,
                        found ? new Color(.19f,.34f,.29f,.98f) : new Color(.36f,.27f,.18f,.98f));
                Text("Body",lines[i],new Vector3(-TextWidth/2,bodyY,-.014f),BodySize,Ink);
            }
            float y = -height/2 + buttonsHeight + .012f;
            if (pages.Count > 1)
            {
                AddTarget("Previous", new Rect(-TextWidth/2,y+.005f,.31f,.075f), -1, page > 0, .0048f);
                Text("Page", $"{page+1} / {pages.Count}", new Vector3(0,y+.041f,-.018f), .0048f, Ink, TextAnchor.MiddleCenter);
                AddTarget("Next", new Rect(TextWidth/2-.31f,y+.005f,.31f,.075f), -2, page+1 < pages.Count, .0048f);
            }
            for (int i=0; i<actions.Length; i++)
            {
                y -= actionHeights[i]+.018f;
                AddTarget(actions[i], new Rect(-TextWidth/2, y+.013f, TextWidth,actionHeights[i]), i, available == null || i >= available.Length || available[i], LabelSize);
            }
            RefreshFont(font);
        }

        void AddTarget(string label, Rect bounds, int action, bool enabled, float size)
        {
            var surface = Surface("Action_"+action,bounds,-.008f,actionMaterial,enabled ? new Color(.24f,.36f,.33f,.96f) : new Color(.17f,.20f,.21f,.92f));
            targets.Add(new Target { bounds=bounds, renderer=surface, action=action, enabled=enabled });
            Text("ActionLabel",string.Join("\n",Wrap(label ?? "",size,bounds.width-.045f)),new Vector3(bounds.center.x,bounds.center.y,-.018f),size,enabled ? Ink : new Color(.66f,.70f,.70f),TextAnchor.MiddleCenter);
        }

        TextMesh Text(string name,string value,Vector3 position,float size,Color color,TextAnchor anchor=TextAnchor.UpperLeft)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetParent(content.transform,false); text.transform.localPosition=position;
            text.font=font; text.fontSize=FontSize; text.characterSize=size; text.lineSpacing=LineHeight/(FontSize*size*.1f);
            text.anchor=anchor; text.alignment=anchor==TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            text.color=color; text.richText=false; text.text=value;
            font.RequestCharactersInTexture(value,FontSize,FontStyle.Normal);
            text.GetComponent<Renderer>().sharedMaterial=textInk;
            return text;
        }

        List<string> Wrap(string value,float size,float width)
        {
            font.RequestCharactersInTexture(value,FontSize,FontStyle.Normal);
            var lines=new List<string>();
            foreach (string paragraph in value.Replace("\r","").Split('\n'))
            {
                var line=new StringBuilder(); float measured=0;
                foreach (string word in paragraph.Split(' '))
                {
                    string part=(line.Length>0 ? " " : "")+word;
                    float partWidth=Measure(part,size);
                    if (line.Length>0 && measured+partWidth>width) { lines.Add(line.ToString()); line.Clear(); measured=0; part=word; }
                    foreach (char c in part)
                    {
                        float advance=Measure(c.ToString(),size);
                        if (line.Length>0 && measured+advance>width) { lines.Add(line.ToString()); line.Clear(); measured=0; }
                        line.Append(c); measured+=advance;
                    }
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
        float Measure(string value,float size)
        {
            float width=0; foreach (char c in value) if (font.GetCharacterInfo(c,out var glyph,FontSize,FontStyle.Normal)) width+=glyph.advance*size*.1f;
            return width;
        }

        Renderer Surface(string name,Rect rect,float z,Material material,Color tint)
        {
            const int segments=8;
            float radius=Mathf.Min(.025f,Mathf.Min(rect.width,rect.height)*.5f);
            var vertices=new List<Vector3> { new Vector3(rect.center.x,rect.center.y,z) };
            var uv=new List<Vector2> { Vector2.one*.5f };
            for (int corner=0;corner<4;corner++)
            {
                var center=new Vector2(corner==0 || corner==3 ? rect.xMax-radius : rect.xMin+radius,corner<2 ? rect.yMax-radius : rect.yMin+radius);
                for (int i=0;i<=segments;i++)
                {
                    float angle=(corner*90f+i*90f/segments)*Mathf.Deg2Rad;
                    var p=center+new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*radius;
                    vertices.Add(new Vector3(p.x,p.y,z)); uv.Add(new Vector2((p.x-rect.xMin)/rect.width,(p.y-rect.yMin)/rect.height));
                }
            }
            var indices=new List<int>(); for(int i=1;i<vertices.Count;i++) { indices.Add(0); indices.Add(i); indices.Add(i==vertices.Count-1 ? 1 : i+1); }
            var mesh=new Mesh { name="HandoffRoundedSurface" }; mesh.SetVertices(vertices); mesh.SetUVs(0,uv); mesh.SetTriangles(indices,0); mesh.RecalculateBounds();
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer)); obj.transform.SetParent(content.transform,false);
            obj.GetComponent<MeshFilter>().sharedMesh=mesh; meshes.Add(mesh);
            var renderer=obj.GetComponent<Renderer>(); renderer.sharedMaterial=material; Tint(renderer,tint); return renderer;
        }
        static void Tint(Renderer renderer,Color color) { var block=new MaterialPropertyBlock(); block.SetColor("_Color",color); renderer.SetPropertyBlock(block); }

        void Update()
        {
            if (!Visible || !focused || paused) return;
            if (!viewer) viewer=Camera.main;
            if (!viewer) return;
            if (!positioned) Recenter();
            var device=InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            device.TryGetFeatureValue(CommonUsages.triggerButton,out bool pressed);
            bool tracked=device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked,out bool isTracked) && isTracked;
            if (tracked && device.TryGetFeatureValue(CommonUsages.devicePosition,out Vector3 position) && device.TryGetFeatureValue(CommonUsages.deviceRotation,out Quaternion rotation))
            {
                var origin=viewer.transform.parent;
                var ray=new Ray(origin ? origin.TransformPoint(position) : position,origin ? origin.TransformDirection(rotation*Vector3.forward) : rotation*Vector3.forward);
                if (!pressed) armed=true;
                Point(ray,armed && pressed && !triggerHeld,device,true);
            }
            else { armed=false; if(pointer)pointer.enabled=false; }
            triggerHeld=pressed;
#if UNITY_EDITOR
            if (Mouse.current!=null) Point(viewer.ScreenPointToRay(Mouse.current.position.ReadValue()),Mouse.current.leftButton.wasPressedThisFrame,default,false);
#endif
        }
        void Point(Ray ray,bool press,InputDevice device,bool showRay)
        {
            var local=new Ray(transform.InverseTransformPoint(ray.origin),transform.InverseTransformDirection(ray.direction));
            int hit=-1; Vector3 point=ray.origin+ray.direction*3;
            if (new Plane(Vector3.forward,Vector3.zero).Raycast(local,out float travel))
            {
                Vector3 p=local.GetPoint(travel); point=transform.TransformPoint(p);
                for(int i=0;i<targets.Count;i++) if(targets[i].enabled && targets[i].bounds.Contains(new Vector2(p.x,p.y))) { hit=i; break; }
            }
            if (showRay)
            {
                if (!pointer)
                {
                    pointer=new GameObject("HandoffRay").AddComponent<LineRenderer>(); pointer.transform.SetParent(transform,false);
                    pointer.useWorldSpace=true; pointer.positionCount=2; pointer.startWidth=.002f; pointer.endWidth=.001f; pointer.sharedMaterial=actionMaterial;
                }
                pointer.enabled=true; pointer.SetPosition(0,ray.origin); pointer.SetPosition(1,point);
            }
            if(hit!=hovered)
            {
                for(int i=0;i<targets.Count;i++) Tint(targets[i].renderer,!targets[i].enabled ? new Color(.17f,.20f,.21f,.92f) : i==hit ? new Color(.43f,.48f,.58f,.98f) : new Color(.24f,.36f,.33f,.96f));
                hovered=hit; if(hit>=0 && device.isValid)device.SendHapticImpulse(0,.15f,.025f);
            }
            if(!press || hit<0)return;
            int action=targets[hit].action;
            if(device.isValid)device.SendHapticImpulse(0,.3f,.045f);
            if(action<0) { page+=action==-1 ? -1 : 1; Render(); } else selected?.Invoke(action);
        }
        void OnApplicationFocus(bool value) { focused=value; armed=false; if(pointer)pointer.enabled=false; }
        void OnApplicationPause(bool value) { paused=value; armed=false; if(pointer)pointer.enabled=false; }
        static bool Same<T>(T[] a,T[] b)
        {
            if(a==null || b==null)return a==b;
            if(a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(!EqualityComparer<T>.Default.Equals(a[i],b[i]))return false;
            return true;
        }
        static void Dispose(UnityEngine.Object value) { if(Application.isPlaying)Destroy(value);else DestroyImmediate(value); }
        void OnDestroy() { Font.textureRebuilt-=RefreshFont; foreach(var value in owned)if(value)Dispose(value); foreach(var mesh in meshes)if(mesh)Dispose(mesh); }
    }
}
