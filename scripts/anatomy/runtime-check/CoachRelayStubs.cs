using UnityEngine;
namespace Scalpal.Exercises.Coach
{
    // Compile-time transport double; native relay behavior has its own Unity gate.
    public class CoachRelay : MonoBehaviour
    {
        public string SessionId = "", SessionPatientId = "", SessionProcedureId = "";
        public string SessionCaseId = "", SessionMode = "", SessionInitialStepId = "";
        public bool Connected, IsSynchronized;
        public void UseSession(string sessionId) { }
        public void AdoptCurrentSession(string patient, string procedure, string caseId, string mode, string firstStep) { }
        public void AdoptSession(string sessionId, string patient, string procedure, string caseId, string mode, string firstStep) { }
        public void Forward(Scalpal.Exercises.Engine.CaseEvent value, string outcome = "") { }
    }
}
