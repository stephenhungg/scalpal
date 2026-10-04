using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Instruments.Editor
{
    // Original metric teaching geometry. This source is the editable model recipe;
    // generated Mesh assets are committed so a player never runs asset generation.
    internal static class OpenInstrumentModels
    {
        internal static readonly string[] Ids = {
            "skin_marker", "toothed_forceps", "retractor", "babcock", "hemostat",
            "right_angle_clamp", "metzenbaum_scissors", "suture_tie"
        };
        const string MeshFolder = "Assets/Scalpal/Instruments/Models/Open";

        internal static GameObject Build(string id)
        {
            if (Array.IndexOf(Ids, id) < 0)
                throw new ArgumentException("Unknown open instrument: " + id, nameof(id));
            Directory.CreateDirectory(MeshFolder);
            var root = new GameObject("Geometry");
            var steel = InstrumentAssetBuilder.Material("OpenSteel", new Color(.66f,.72f,.77f), .92f, .8f);
            var ink = InstrumentAssetBuilder.Material("OpenMarkerInk", new Color(.27f,.07f,.42f), 0, .36f);
            var polymer = InstrumentAssetBuilder.Material("OpenMarkerBody", new Color(.92f,.87f,.72f), 0, .5f);
            var thread = InstrumentAssetBuilder.Material("OpenSuture", new Color(.21f,.12f,.06f), 0, .24f);
            var body = new Shape();
            float length = .15f;
            switch (id)
            {
                case "skin_marker":
                    length = .105f;
                    body.Rod(new Vector3(0,0,-.03f), new Vector3(0,0,.085f), .006f);
                    Part(root.transform,id,"Body",body,polymer);
                    var nib = new Shape(); nib.Rod(new Vector3(0,0,.085f), new Vector3(0,0,length), .002f);
                    Part(root.transform,id,"InkNib",nib,ink);
                    break;
                case "retractor":
                    length = .155f;
                    body.Box(new Vector3(0,0,.046f),new Vector3(.013f,.005f,.15f));
                    body.Box(new Vector3(0,-.012f,.126f),new Vector3(.014f,.025f,.005f));
                    body.Box(new Vector3(0,-.023f,.14f),new Vector3(.023f,.004f,.03f));
                    body.Ring(new Vector3(0,0,-.036f),.014f,.02f,.002f);
                    Part(root.transform,id,"Body",body,steel);
                    // End of the bent blade is the working point; normalization is done by the shared builder.
                    Anchor(root.transform,"Tip",new Vector3(0,-.023f,length));
                    break;
                case "suture_tie":
                    length = .10f;
                    body.Ring(new Vector3(0,0,.085f),.016f,.015f,.0008f);
                    body.Rod(new Vector3(-.011f,0,.073f),new Vector3(-.005f,0,-.025f),.0008f);
                    body.Rod(new Vector3(.011f,0,.073f),new Vector3(.005f,0,-.025f),.0008f);
                    Part(root.transform,id,"ThreadLoopAndTails",body,thread);
                    break;
                case "toothed_forceps":
                    length = .145f;
                    body.Box(new Vector3(0,0,-.018f),new Vector3(.009f,.004f,.025f));
                    Part(root.transform,id,"Heel",body,steel);
                    foreach (int sign in new[]{1,-1})
                    {
                        var jaw = new Shape();
                        jaw.Rod(new Vector3(0,sign*.002f,-.008f),new Vector3(0,sign*.004f,.12f),.0025f);
                        jaw.Rod(new Vector3(0,sign*.004f,.12f),new Vector3(0,sign*.0008f,length),.0018f);
                        jaw.Box(new Vector3(0,0,.143f),new Vector3(.002f,.003f,.003f));
                        Part(root.transform,id,sign>0?"JawUpper":"JawLower",jaw,steel);
                    }
                    break;
                default:
                    length = id=="babcock" ? .155f : .15f;
                    body.Rod(new Vector3(0,0,.032f),new Vector3(0,0,.094f),.0025f);
                    body.Rod(new Vector3(-.009f,0,-.016f),new Vector3(.002f,0,.045f),.0024f);
                    body.Rod(new Vector3(.009f,0,-.016f),new Vector3(-.002f,0,.045f),.0024f);
                    body.Ring(new Vector3(-.014f,0,-.028f),.011f,.016f,.002f);
                    body.Ring(new Vector3(.014f,0,-.028f),.011f,.016f,.002f);
                    body.Box(new Vector3(0,.002f,-.008f),new Vector3(.018f,.003f,.006f));
                    Part(root.transform,id,"Body",body,steel);
                    foreach(int sign in new[]{1,-1})
                    {
                        var jaw = new Shape();
                        if(id=="babcock")
                        {
                            jaw.Rod(new Vector3(0,sign*.002f,0),new Vector3(0,sign*.003f,.03f),.002f);
                            jaw.Ring(new Vector3(0,sign*.002f,.045f),.01f,.016f,.0018f);
                        }
                        else if(id=="right_angle_clamp")
                        {
                            jaw.Rod(new Vector3(0,sign*.0015f,0),new Vector3(0,sign*.0015f,.038f),.0018f);
                            jaw.Rod(new Vector3(0,sign*.0015f,.038f),new Vector3(.016f,sign*.0015f,.038f),.0018f);
                        }
                        else
                        {
                            jaw.Box(new Vector3(0,sign*.0013f,.028f),new Vector3(.003f,.002f,.056f));
                            if(id=="hemostat") for(int tooth=0;tooth<6;tooth++)
                                jaw.Box(new Vector3(0,sign*.0007f,.033f+tooth*.004f),new Vector3(.004f,.0015f,.001f));
                        }
                        Part(root.transform,id,sign>0?"JawUpper":"JawLower",jaw,steel,new Vector3(0,0,.094f));
                    }
                    if(id=="right_angle_clamp") Anchor(root.transform,"Tip",new Vector3(.016f,0,.132f));
                    if(id=="metzenbaum_scissors")
                    {
                        Anchor(root.transform,"CutStart",new Vector3(0,0,.112f));
                        Anchor(root.transform,"CutEnd",new Vector3(0,0,length));
                    }
                    break;
            }
            Anchor(root.transform,"GripAnchor",Vector3.zero);
            if(root.transform.Find("Tip")==null) Anchor(root.transform,"Tip",new Vector3(0,0,length));
            return root;
        }
        static void Anchor(Transform parent,string name,Vector3 position)
        {
            var anchor=new GameObject(name).transform;anchor.SetParent(parent,false);anchor.localPosition=position;
        }
        static void Part(Transform parent,string id,string name,Shape shape,Material material,Vector3 position=default)
        {
            var mesh=shape.Mesh("open_"+id+"_"+name);
            string path=MeshFolder+"/"+mesh.name+".asset";
            var previous=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(previous!=null){EditorUtility.CopySerialized(mesh,previous);EditorUtility.SetDirty(previous);UnityEngine.Object.DestroyImmediate(mesh);mesh=previous;}
            else AssetDatabase.CreateAsset(mesh,path);
            var part=new GameObject(name);part.transform.SetParent(parent,false);part.transform.localPosition=position;
            part.AddComponent<MeshFilter>().sharedMesh=mesh;part.AddComponent<MeshRenderer>().sharedMaterial=material;
        }
        sealed class Shape
        {
            readonly List<Vector3> vertices=new List<Vector3>();
            readonly List<int> triangles=new List<int>();
            void Triangle(Vector3 a,Vector3 b,Vector3 c)
            {
                int offset=vertices.Count;vertices.AddRange(new[]{a,b,c});
                triangles.AddRange(new[]{offset,offset+1,offset+2});
            }
            void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d)
            {
                int offset=vertices.Count;vertices.AddRange(new[]{a,b,c,d});
                triangles.AddRange(new[]{offset,offset+1,offset+2,offset,offset+2,offset+3});
            }
            internal void Box(Vector3 center,Vector3 size)
            {
                Vector3 a=center-size*.5f,b=center+size*.5f;
                Quad(new Vector3(a.x,a.y,a.z),new Vector3(a.x,b.y,a.z),new Vector3(b.x,b.y,a.z),new Vector3(b.x,a.y,a.z));
                Quad(new Vector3(a.x,a.y,b.z),new Vector3(b.x,a.y,b.z),new Vector3(b.x,b.y,b.z),new Vector3(a.x,b.y,b.z));
                Quad(new Vector3(a.x,a.y,a.z),new Vector3(b.x,a.y,a.z),new Vector3(b.x,a.y,b.z),new Vector3(a.x,a.y,b.z));
                Quad(new Vector3(a.x,b.y,a.z),new Vector3(a.x,b.y,b.z),new Vector3(b.x,b.y,b.z),new Vector3(b.x,b.y,a.z));
                Quad(new Vector3(a.x,a.y,a.z),new Vector3(a.x,a.y,b.z),new Vector3(a.x,b.y,b.z),new Vector3(a.x,b.y,a.z));
                Quad(new Vector3(b.x,a.y,a.z),new Vector3(b.x,b.y,a.z),new Vector3(b.x,b.y,b.z),new Vector3(b.x,a.y,b.z));
            }
            internal void Rod(Vector3 a,Vector3 b,float radius)
            {
                Vector3 axis=(b-a).normalized;
                Vector3 side=Vector3.Cross(axis,Mathf.Abs(Vector3.Dot(axis,Vector3.up))>.9f?Vector3.right:Vector3.up).normalized*radius;
                Vector3 up=Vector3.Cross(axis,side);
                for(int i=0;i<8;i++)
                {
                    float p=i*Mathf.PI/4,q=(i+1)*Mathf.PI/4;
                    Vector3 u=side*Mathf.Cos(p)+up*Mathf.Sin(p),v=side*Mathf.Cos(q)+up*Mathf.Sin(q);
                    Quad(a+u,a+v,b+v,b+u);
                    Triangle(a,a+v,a+u);Triangle(b,b+u,b+v);
                }
            }
            internal void Ring(Vector3 center,float rx,float rz,float tube)
            {
                Vector3 Point(int i,int j)
                {
                    float a=i*Mathf.PI/8,b=j*Mathf.PI/2;
                    return center+new Vector3((rx+tube*Mathf.Cos(b))*Mathf.Cos(a),tube*Mathf.Sin(b),(rz+tube*Mathf.Cos(b))*Mathf.Sin(a));
                }
                for(int i=0;i<16;i++) for(int j=0;j<4;j++) Quad(Point(i,j),Point(i,j+1),Point(i+1,j+1),Point(i+1,j));
            }
            internal Mesh Mesh(string name)
            {
                var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
            }
        }
    }
}
