using System;

namespace Scalpal.Anatomy.Tissue
{
    // One material-point/cell history; dimensionless Green strain, second Piola stress in Pa.
    // The exponential constant-strain-rate update follows the primary PyLith derivation:
    // https://pylith.readthedocs.io/en/stable/user/governingeqns/elasticity/bulk-rheologies/linear-genmaxwell.html
    // Unlike that infinitesimal/deviatoric model, this approximation relaxes the full
    // VolumeMaterial St Venant-Kirchhoff law, with a common fractional spectrum for
    // shear and bulk response. It is not a measured tissue law or a multiplicative
    // finite-strain viscoelastic model. No parameters or tissue mapping are supplied.
    public sealed class MaxwellHistory
    {
        readonly float[] fractions, relaxationSeconds, decay, ramp, weights;
        readonly TissueTensor[] viscous, memory, nextViscous;
        TissueTensor previous;
        bool prepared;

        public int BranchCount => fractions.Length;
        public float EquilibriumFraction { get; }
        public bool HasHistory { get; private set; }

        public MaxwellHistory(float[] passiveFractions, float[] timeConstantsSeconds)
        {
            if (passiveFractions == null || timeConstantsSeconds == null || passiveFractions.Length != timeConstantsSeconds.Length)
                throw new ArgumentException("Maxwell branch arrays must have equal nonnull lengths");
            double total = 0;
            for (int i = 0; i < passiveFractions.Length; i++)
            {
                if (!Finite(passiveFractions[i]) || passiveFractions[i] < 0 || !Finite(timeConstantsSeconds[i]) || timeConstantsSeconds[i] <= 0)
                    throw new ArgumentException("Maxwell fractions must be finite and nonnegative; relaxation times must be finite positive seconds");
                total += passiveFractions[i];
            }
            if (total >= 1) throw new ArgumentException("Maxwell equilibrium fraction must remain positive (sum of branch fractions < 1)");
            EquilibriumFraction = (float)(1 - total);
            fractions = (float[])passiveFractions.Clone();
            relaxationSeconds = (float[])timeConstantsSeconds.Clone();
            decay = new float[BranchCount]; ramp = new float[BranchCount]; weights = new float[BranchCount];
            viscous = new TissueTensor[BranchCount]; memory = new TissueTensor[BranchCount]; nextViscous = new TissueTensor[BranchCount];
        }

        public float BranchFraction(int branch) => fractions[branch];
        public float RelaxationSeconds(int branch) => relaxationSeconds[branch];

        // Equivalent uniaxial Maxwell-arm viscosity under constant Poisson ratio:
        // eta_i = (g_i * Young modulus) * tau_i, Pa*s. Shear viscosity would instead
        // use the shear modulus. Neither quantity is a measured tissue viscosity.
        public double BranchViscosityPascalSeconds(int branch, VolumeMaterial material)
        {
            ValidateMaterial(material);
            return (double)fractions[branch] * material.youngPascals * relaxationSeconds[branch];
        }

        // Prepare only coefficients/potential for a trial; committed history is untouched.
        // Repeated/replaced prepares are safe after a rejected solver step.
        public void Prepare(float seconds)
        {
            if (!Finite(seconds) || seconds <= 0) throw new ArgumentException("Maxwell timestep must be finite positive seconds");
            for (int i = 0; i < BranchCount; i++)
            {
                double ratio = (double)seconds / relaxationSeconds[i];
                double d = Math.Exp(-ratio);
                // Avoid cancellation of 1-exp(-x) at short timesteps, including x << epsilon.
                double a = ratio < 1e-4 ? 1 - ratio / 2 + ratio * ratio / 6 - ratio * ratio * ratio / 24 : (1 - d) / ratio;
                decay[i] = (float)d; ramp[i] = (float)a; weights[i] = (float)(fractions[i] * a);
                memory[i] = previous - (previous - viscous[i]) * (float)(d / a);
            }
            prepared = true;
        }

        // Algorithmic trial potential density, J/m^3; derivative with respect to E is
        // Stress. This history-dependent potential is not total physical stored energy
        // or dissipated heat. Numerical node velocity damping is entirely separate.
        public float Energy(TissueTensor strain, VolumeMaterial material)
        {
            ValidateTrial(strain, material);
            float result = EquilibriumFraction * material.Energy(strain);
            for (int i = 0; i < BranchCount; i++) result += weights[i] * material.Energy(strain - memory[i]);
            return result;
        }

        public TissueTensor Stress(TissueTensor strain, VolumeMaterial material)
        {
            ValidateTrial(strain, material);
            var result = material.SecondPiola(strain) * EquilibriumFraction;
            for (int i = 0; i < BranchCount; i++) result += material.SecondPiola(strain - memory[i]) * weights[i];
            return result;
        }

        // Advance exactly once, only after every cell of a solver timestep is accepted.
        public void Commit(TissueTensor strain)
        {
            if (!prepared) throw new InvalidOperationException("Prepare a Maxwell trial before committing it");
            ValidateStrain(strain);
            for (int i = 0; i < BranchCount; i++)
            {
                nextViscous[i] = strain - ((previous - viscous[i]) * decay[i] + (strain - previous) * ramp[i]);
                ValidateStrain(nextViscous[i]);
            }
            Array.Copy(nextViscous, viscous, BranchCount);
            previous = strain; HasHistory = true; prepared = false;
        }

        // Physical stress at the last accepted state, not the next trial potential.
        // Read-only: force measurements must not advance material time/history.
        public TissueTensor CommittedStress(VolumeMaterial material)
        {
            ValidateMaterial(material);
            var result = material.SecondPiola(previous) * EquilibriumFraction;
            for (int i = 0; i < BranchCount; i++)
                result += material.SecondPiola(previous - viscous[i]) * fractions[i];
            return result;
        }

        public void Reset()
        {
            previous = default;
            Array.Clear(viscous, 0, BranchCount); Array.Clear(nextViscous, 0, BranchCount);
            Array.Clear(memory, 0, BranchCount); Array.Clear(weights, 0, BranchCount);
            Array.Clear(decay, 0, BranchCount); Array.Clear(ramp, 0, BranchCount);
            HasHistory = false; prepared = false;
        }

        void ValidateTrial(TissueTensor strain, VolumeMaterial material)
        {
            if (!prepared) throw new InvalidOperationException("Prepare a Maxwell trial before evaluating its potential/stress");
            ValidateStrain(strain); ValidateMaterial(material);
        }
        static void ValidateMaterial(VolumeMaterial material)
        {
            if (!material.HasValidUnits) throw new ArgumentException("Maxwell history requires a valid SI elastic VolumeMaterial");
        }
        static void ValidateStrain(TissueTensor strain)
        {
            if (!TissueCage.Finite(strain.x) || !TissueCage.Finite(strain.y) || !TissueCage.Finite(strain.z))
                throw new ArgumentException("Maxwell Green strain must be finite");
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
