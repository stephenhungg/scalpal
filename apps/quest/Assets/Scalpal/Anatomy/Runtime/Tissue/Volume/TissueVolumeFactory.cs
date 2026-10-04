using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    public static class TissueVolumeFactory
    {
        public static TissueVolume Box(Bounds bounds,int nx,int ny,int nz,VolumeMaterial material,bool pinPerimeter=true)
        {
            if(nz<1||nz>15||!TissueCage.Finite(bounds.center)||!TissueCage.Finite(bounds.size)||bounds.size.z<=0) throw new ArgumentException("Invalid tissue depth grid");
            var depths=new float[nz+1];for(int z=0;z<=nz;z++)depths[z]=bounds.min.z+bounds.size.z*z/nz;
            return Grid(bounds,nx,ny,depths,new[]{material},new int[nz],pinPerimeter);
        }
        // Generic local wall coupon, not segmented tissue or a measured participant thickness.
        public static TissueVolume AbdominalWall(Vector2? projectedCenter=null)
        {
            Vector2 center=projectedCenter??new Vector2(0,1.017768f);
            if(float.IsNaN(center.x)||float.IsInfinity(center.x)||float.IsNaN(center.y)||float.IsInfinity(center.y))throw new ArgumentException("Invalid wall projection");
            var materials=new[] {
                new VolumeMaterial {id="synthetic_wall_skin",youngPascals=100000,poissonRatio=.45f,densityKgPerCubicMeter=1000,color=new Color(.72f,.49f,.38f),measurementSource="unfit demo; thickness2mm authored"},
                new VolumeMaterial {id="synthetic_wall_fat",youngPascals=20000,poissonRatio=.45f,densityKgPerCubicMeter=900,color=new Color(.94f,.73f,.26f),measurementSource="unfit demo; thickness12mm authored"},
                new VolumeMaterial {id="synthetic_wall_peritoneum",youngPascals=6790000,poissonRatio=.45f,densityKgPerCubicMeter=1000,color=new Color(.73f,.58f,.57f),measurementSource="Kriener2023 Table5 FNF tensile median6.79MPa; isotropic approximation, not full constitutive calibration; thickness1mm authored"}
            };
            // +X anatomical left, +Y cranial, +Z inward from source anterior torso surface.
            return Grid(new Bounds(new Vector3(center.x,center.y,-.115287f),new Vector3(.16f,.10f,.015f)),8,5,
                new[]{-.115287f,-.113287f,-.110287f,-.106287f,-.101287f,-.100287f},materials,new[]{0,1,1,1,2},true);
        }
        // Explicit open-body teaching layers, in the registered wound frame (+Z inward).
        // Parameters are authored approximations; the calibrated legacy coupon is unchanged.
        public static TissueVolume OpenAbdominalWall()
        {
            var materials=new[] {
                new VolumeMaterial{id="skin",youngPascals=100000,poissonRatio=.45f,densityKgPerCubicMeter=1000,color=new Color(.72f,.49f,.38f),measurementSource="authored open teaching layer; uncalibrated"},
                new VolumeMaterial{id="fat",youngPascals=20000,poissonRatio=.45f,densityKgPerCubicMeter=900,color=new Color(.94f,.73f,.26f),measurementSource="authored open teaching layer; uncalibrated"},
                new VolumeMaterial{id="fascia",youngPascals=400000,poissonRatio=.45f,densityKgPerCubicMeter=1000,color=new Color(.85f,.8f,.7f),measurementSource="authored open teaching layer; uncalibrated"},
                new VolumeMaterial{id="muscle",youngPascals=60000,poissonRatio=.45f,densityKgPerCubicMeter=1050,color=new Color(.5f,.12f,.1f),measurementSource="authored open teaching layer; uncalibrated"},
                new VolumeMaterial{id="peritoneum",youngPascals=6790000,poissonRatio=.45f,densityKgPerCubicMeter=1000,color=new Color(.73f,.58f,.57f),measurementSource="authored thickness; Kriener median modulus, no calibration"}
            };
            return Grid(new Bounds(new Vector3(0,0,.014f),new Vector3(.16f,.10f,.028f)),8,5,
                new[]{0f,.002f,.014f,.019f,.027f,.028f},materials,new[]{0,1,2,3,4},true);
        }
        static TissueVolume Grid(Bounds bounds,int nx,int ny,float[] depths,VolumeMaterial[] materials,int[] layer,bool pinPerimeter)
        {
            if(nx<1||ny<1||nx>32||ny>32||depths==null||depths.Length<2||depths.Length>16||layer.Length!=depths.Length-1||bounds.size.x<=0||bounds.size.y<=0)
                throw new ArgumentException("Invalid bounded tissue grid");
            int nz=depths.Length-1;
            for(int z=0;z<nz;z++)if(depths[z+1]<=depths[z])throw new ArgumentException("Layer depths must increase");
            int count=(nx+1)*(ny+1)*(nz+1);if(count>TissueVolume.MaxNodes)throw new ArgumentException("Grid exceeds tissue node budget");
            var nodes=new Vector3[count];var pins=new bool[count];
            int Index(int x,int y,int z)=>(z*(ny+1)+y)*(nx+1)+x;
            for(int z=0;z<=nz;z++)for(int y=0;y<=ny;y++)for(int x=0;x<=nx;x++)
            {
                int i=Index(x,y,z);nodes[i]=new Vector3(bounds.min.x+bounds.size.x*x/nx,bounds.min.y+bounds.size.y*y/ny,depths[z]);
                pins[i]=pinPerimeter&&(x==0||x==nx||y==0||y==ny);
            }
            var cells=new List<TissueVolume.Cell>();
            // Freudenthal triangulation uses the same shared-face diagonal in every adjacent cell.
            int[,] tets={{0,1,3,7},{0,3,2,7},{0,2,6,7},{0,6,4,7},{0,4,5,7},{0,5,1,7}};
            for(int z=0;z<nz;z++)for(int y=0;y<ny;y++)for(int x=0;x<nx;x++)
            {
                var box=new int[8];for(int v=0;v<8;v++)box[v]=Index(x+(v&1),y+((v>>1)&1),z+((v>>2)&1));
                for(int t=0;t<6;t++)cells.Add(new TissueVolume.Cell {a=box[tets[t,0]],b=box[tets[t,1]],c=box[tets[t,2]],d=box[tets[t,3]],material=layer[z]});
            }
            return new TissueVolume(nodes,cells.ToArray(),materials,pins);
        }
    }
}
