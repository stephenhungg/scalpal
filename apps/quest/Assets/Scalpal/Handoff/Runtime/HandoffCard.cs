using System;
using System.Collections.Generic;
using System.Text;
using Scalpal.Brand;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Scalpal.Handoff
{
    /// <summary>World-locked, paginated handoff surface in the Scalpal brand. Rendering and input only; no case authority.</summary>
    public sealed class HandoffCard : MonoBehaviour
    {
        public Camera viewer;
        public float distance = 1.2f;
        public bool Visible => content && content.activeSelf;
        public int PageCount => pages.Count;
        public int Page => page;
        // Ems in metres at the 1.2 m card distance: body 0.034 (37 mm/m), labels 0.040 (43 mm/m), title 0.050.
        const float Width = 1.18f, TextWidth = 1.04f, LineHeight = .046f;
        const int BodyLines = 6;
        public const float BodySize = .034f, LabelSize = .040f, TitleSize = .050f;
        static Color Ink => ScalpalBrand.Ink;
        readonly List<string> pages = new List<string>();
        readonly List<Target> targets = new List<Target>();
        readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        readonly List<Mesh> meshes = new List<Mesh>();
        GameObject content;
        ScalpalBrand brand;
        readonly ScalpalPointerHand[] pointers = { new ScalpalPointerHand(0, "HandoffRayLeft"), new ScalpalPointerHand(1, "HandoffRayRight") };
        public ScalpalPointerHand Pointer(int hand) => pointers[hand];
        Action<int> selected;
        string heading, copy;
        string[] actions = Array.Empty<string>();
        bool[] available;
        bool positioned, focused = true, paused;
        int page, hovered = -1;
        sealed class Target
        {
            public Rect bounds;
            public Renderer renderer;
            public int action;
            public bool enabled;
            public HandoffCardTarget hit;
        }

        public void Show(string title, string body, string[] labels, Action<int> onSelect, bool[] enabled = null)
        {
            selected = onSelect;
            bool unchanged = Visible && heading == title && copy == body && Same(actions, labels) && Same(available, enabled);
            if (unchanged) return;
            heading = title ?? ""; copy = body ?? "";
            actions = labels == null ? Array.Empty<string>() : (string[])labels.Clone();
            available = enabled == null ? null : (bool[])enabled.Clone();
            EnsureBrand();
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
            foreach (var pointer in pointers) pointer.Clear();
            positioned = false; selected = null;
        }

        public void Recenter()
        {
            if (!viewer) viewer = Camera.main;
            if (!viewer) return;
            // Level with the horizon: yaw-only facing the viewer, never the head's pitch or roll.
            if (!ScalpalPlacement.Place(transform, viewer.transform, distance, .06f))
                transform.SetPositionAndRotation(viewer.transform.position + Vector3.forward * distance - Vector3.up * .06f, Quaternion.identity);
            positioned = true;
        }

        void EnsureBrand() { if (!brand) brand = ScalpalBrand.Active; }

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
            float height = .505f + buttonsHeight + (pages.Count > 1 ? .10f : 0);
            var backdrop = Surface("Glass", new Rect(-Width / 2, -height / 2, Width, height), 0, brand.glass, new Color(.018f,.018f,.022f,.93f));
            // Consume the office rig's physics ray so a handoff click cannot activate a control behind this card.
            var blocker = backdrop.gameObject.AddComponent<BoxCollider>();
            blocker.size = new Vector3(Width,height,.025f);
            float top = height / 2;
            Text("Title", string.Join("\n", Wrap(heading, TitleSize, TextWidth, ScalpalTextRole.Title)), new Vector3(-TextWidth/2, top-.05f, -.012f), TitleSize, Ink, ScalpalTextRole.Title);
            var lines = pages[page].Split('\n');
            for (int i=0; i<lines.Length; i++)
            {
                float bodyY=top-.183f-i*LineHeight;
                bool found=lines[i].StartsWith("found",StringComparison.OrdinalIgnoreCase);
                bool missed=lines[i].StartsWith("missed",StringComparison.OrdinalIgnoreCase);
                if(found || missed)
                    Surface("RiskChip",new Rect(-TextWidth/2-.008f,bodyY-.046f,TextWidth+.016f,.043f),-.008f,brand.button,
                        found ? new Color(.10f,.10f,.105f,.96f) : new Color(.20f,.075f,.03f,.96f), missed);
                Text("Body",lines[i],new Vector3(-TextWidth/2,bodyY-.004f,-.014f),BodySize,found||missed ? Ink : ScalpalBrand.Ink70);
            }
            float y = -height/2 + buttonsHeight + .012f;
            if (pages.Count > 1)
            {
                AddTarget("Previous", new Rect(-TextWidth/2,y+.005f,.31f,.075f), -1, page > 0, LabelSize);
                Text("Page", $"{page+1} / {pages.Count}", new Vector3(0,y+.0425f,-.018f), BodySize, ScalpalBrand.Ink70, ScalpalTextRole.Body, TextAnchor.MiddleCenter);
                AddTarget("Next", new Rect(TextWidth/2-.31f,y+.005f,.31f,.075f), -2, page+1 < pages.Count, LabelSize);
            }
            for (int i=0; i<actions.Length; i++)
            {
                y -= actionHeights[i]+.018f;
                AddTarget(actions[i], new Rect(-TextWidth/2, y+.013f, TextWidth,actionHeights[i]), i, available == null || i >= available.Length || available[i], LabelSize);
            }
        }

        void AddTarget(string label, Rect bounds, int action, bool enabled, float size)
        {
            var surface = Surface("Action_"+action,bounds,-.008f,brand.button,enabled ? ScalpalBrand.ButtonTint : ScalpalBrand.ButtonDisabledTint);
            // A real collider per action: the shared aim-pose pointer presses it like every other Scalpal button.
            var collider = surface.gameObject.AddComponent<BoxCollider>();
            collider.center = new Vector3(bounds.center.x, bounds.center.y, -.012f); collider.size = new Vector3(bounds.width, bounds.height, .01f);
            var hit = surface.gameObject.AddComponent<HandoffCardTarget>(); hit.card = this; hit.index = targets.Count;
            targets.Add(new Target { bounds=bounds, renderer=surface, action=action, enabled=enabled, hit=hit });
            Text("ActionLabel",string.Join("\n",Wrap(label ?? "",size,bounds.width-.045f,ScalpalTextRole.Label)),new Vector3(bounds.center.x,bounds.center.y,-.018f),size,enabled ? Ink : ScalpalBrand.InkDisabled,ScalpalTextRole.Label,TextAnchor.MiddleCenter);
        }

        TextMeshPro Text(string name,string value,Vector3 position,float size,Color color,ScalpalTextRole role=ScalpalTextRole.Body,TextAnchor anchor=TextAnchor.UpperLeft)
        {
            int lines=Mathf.Max(1,(value??"").Split('\n').Length);
            var font=brand.Font(role);
            float pitch=size*ScalpalBrandLayout.ExtentPerEm(font);
            var text=brand.Text(content.transform,name,value,role,position,size,TextWidth+.02f,Mathf.Max(LineHeight,pitch)*lines*1.15f+.01f,anchor);
            // Fixed line pitch so wrapped rows and risk chips stay aligned with the authored grid.
            text.lineSpacing=role==ScalpalTextRole.Title?0:(LineHeight-pitch)/size*100;
            text.color=color; text.GetComponent<ScalpalTextFit>().Fit();
            return text;
        }

        List<string> Wrap(string value,float size,float width,ScalpalTextRole role=ScalpalTextRole.Body)
        {
            EnsureBrand();
            var font=brand.Font(role);
            var lines=new List<string>();
            foreach (string paragraph in value.Replace("\r","").Split('\n'))
            {
                var line=new StringBuilder(); float measured=0;
                foreach (string word in paragraph.Split(' '))
                {
                    string part=(line.Length>0 ? " " : "")+word;
                    float partWidth=Measure(font,part,size,role);
                    if (line.Length>0 && measured+partWidth>width) { lines.Add(line.ToString()); line.Clear(); measured=0; part=word; }
                    foreach (char c in part)
                    {
                        float advance=Measure(font,c.ToString(),size,role);
                        if (line.Length>0 && measured+advance>width) { lines.Add(line.ToString()); line.Clear(); measured=0; }
                        line.Append(c); measured+=advance;
                    }
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
        // Advance widths from the static SDF font asset (with its fallback), in metres at this em size.
        static float Measure(TMP_FontAsset font,string value,float size,ScalpalTextRole role)
        {
            float width=0, tracking=role==ScalpalTextRole.Title||role==ScalpalTextRole.Wordmark?ScalpalBrand.DisplayTracking*.01f*size:0;
            foreach (char c in value) width+=Advance(font,c)*size+tracking;
            return width;
        }
        static float Advance(TMP_FontAsset font,char c)
        {
            if (font.characterLookupTable.TryGetValue(c,out var character)) return character.glyph.metrics.horizontalAdvance/font.faceInfo.pointSize*font.faceInfo.scale*character.scale;
            if (font.fallbackFontAssetTable!=null) foreach (var fallback in font.fallbackFontAssetTable) if (fallback && fallback.characterLookupTable.ContainsKey(c)) return Advance(fallback,c);
            return c==' '?.3f:.6f;
        }

        Renderer Surface(string name,Rect rect,float z,Material material,Color tint,bool accent=false)
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
            var renderer=obj.GetComponent<Renderer>(); renderer.sharedMaterial=material; Tint(renderer,tint,accent);
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows=false; return renderer;
        }
        static void Tint(Renderer renderer,Color color,bool accent=false) => ScalpalBrand.Tint(renderer,new MaterialPropertyBlock(),color,accent);

        void Update()
        {
            if (!Visible || !focused || paused) { foreach (var pointer in pointers) pointer.Clear(); return; }
            if (!viewer) viewer=Camera.main;
            if (!viewer) return;
            if (!positioned) Recenter();
            StepPointers();
#if UNITY_EDITOR
            if (Mouse.current!=null && Mouse.current.leftButton.wasPressedThisFrame && Physics.Raycast(viewer.ScreenPointToRay(Mouse.current.position.ReadValue()),out var hit,8))
                hit.collider.GetComponent<HandoffCardTarget>()?.Press();
#endif
        }
        /// <summary>Either controller's aim-pose ray (or a pinching hand) focuses and presses this card's actions.</summary>
        public void StepPointers()
        {
            if (!Visible) return;
            var space=viewer ? viewer.transform.parent : null;
            int focus=-1;
            foreach (var pointer in pointers)
            {
                pointer.Step(space,space!=null,transform,collider=>{ var target=collider.GetComponent<HandoffCardTarget>(); return target && target.card==this ? target : null; });
                if (pointer.Hovered is HandoffCardTarget target && focus<0) focus=target.index;
            }
            if (focus!=hovered) { hovered=focus; for(int i=0;i<targets.Count;i++) Tint(targets[i].renderer,!targets[i].enabled ? ScalpalBrand.ButtonDisabledTint : i==focus ? ScalpalBrand.ButtonHoverTint : ScalpalBrand.ButtonTint,targets[i].enabled && i==focus); }
        }
        internal bool CanPress(int index) => Visible && index>=0 && index<targets.Count && targets[index].enabled;
        internal void PressTarget(int index)
        {
            if (!CanPress(index)) return;
            int action=targets[index].action;
            if(action<0) { page+=action==-1 ? -1 : 1; Render(); } else selected?.Invoke(action);
        }
        void OnApplicationFocus(bool value) { focused=value; foreach (var pointer in pointers) pointer.Clear(); }
        void OnApplicationPause(bool value) { paused=value; foreach (var pointer in pointers) pointer.Clear(); }
        static bool Same<T>(T[] a,T[] b)
        {
            if(a==null || b==null)return a==b;
            if(a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(!EqualityComparer<T>.Default.Equals(a[i],b[i]))return false;
            return true;
        }
        static void Dispose(UnityEngine.Object value) { if(Application.isPlaying)Destroy(value);else DestroyImmediate(value); }
        void OnDestroy() { foreach (var pointer in pointers) pointer.Destroy(); foreach(var value in owned)if(value)Dispose(value); foreach(var mesh in meshes)if(mesh)Dispose(mesh); }
    }
}
