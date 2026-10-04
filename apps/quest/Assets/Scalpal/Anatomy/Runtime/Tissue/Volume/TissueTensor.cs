using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Column-major 3x3 tensor; avoids homogeneous matrix work inside element solves.
    public struct TissueTensor
    {
        public Vector3 x, y, z;
        public TissueTensor(Vector3 a, Vector3 b, Vector3 c) { x=a; y=b; z=c; }
        public static TissueTensor Identity => new TissueTensor(Vector3.right,Vector3.up,Vector3.forward);
        public Vector3 Multiply(Vector3 v) => x*v.x+y*v.y+z*v.z;
        public TissueTensor Multiply(TissueTensor b) => new TissueTensor(Multiply(b.x),Multiply(b.y),Multiply(b.z));
        public TissueTensor Transpose() => new TissueTensor(new Vector3(x.x,y.x,z.x),new Vector3(x.y,y.y,z.y),new Vector3(x.z,y.z,z.z));
        public float Determinant => Vector3.Dot(x,Vector3.Cross(y,z));
        public float Trace => x.x+y.y+z.z;
        public float SquaredNorm => x.sqrMagnitude+y.sqrMagnitude+z.sqrMagnitude;
        public static TissueTensor operator +(TissueTensor a,TissueTensor b) => new TissueTensor(a.x+b.x,a.y+b.y,a.z+b.z);
        public static TissueTensor operator -(TissueTensor a,TissueTensor b) => new TissueTensor(a.x-b.x,a.y-b.y,a.z-b.z);
        public static TissueTensor operator *(TissueTensor a,float b) => new TissueTensor(a.x*b,a.y*b,a.z*b);
        public TissueTensor Inverse()
        {
            float d=Determinant;
            var rows=new TissueTensor(Vector3.Cross(y,z)/d,Vector3.Cross(z,x)/d,Vector3.Cross(x,y)/d);
            return rows.Transpose();
        }
    }

    [System.Serializable]
    public struct VolumeMaterial
    {
        public string id;
        public float youngPascals, poissonRatio, densityKgPerCubicMeter;
        public Color color;
        public string measurementSource;
        // Optional passive stress-relaxation spectrum. Empty means purely elastic.
        public float[] relaxationFractions, relaxationSeconds;
        public bool HasValidUnits => youngPascals>0 && youngPascals<1e9f && poissonRatio>=0 && poissonRatio<.499f && densityKgPerCubicMeter>0 && densityKgPerCubicMeter<3000;
        public float ShearModulus => youngPascals/(2*(1+poissonRatio));
        public float LameLambda => youngPascals*poissonRatio/((1+poissonRatio)*(1-2*poissonRatio));
        // St Venant-Kirchhoff energy per reference volume. E/P are strain/stress, in SI units.
        // This is a constitutive approximation; measured tensile modulus is not a full finite-strain fit.
        public float Energy(TissueTensor strain) => ShearModulus*strain.SquaredNorm + .5f*LameLambda*strain.Trace*strain.Trace;
        public TissueTensor SecondPiola(TissueTensor strain) => strain*(2*ShearModulus)+TissueTensor.Identity*(LameLambda*strain.Trace);
    }
}
